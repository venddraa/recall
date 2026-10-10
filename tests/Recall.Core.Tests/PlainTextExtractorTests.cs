using System.Runtime.InteropServices;
using System.Text;
using Recall.Core.Extraction;
using Recall.Core.Scanning;

namespace Recall.Core.Tests;

public sealed class PlainTextExtractorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-text-tests-{Guid.NewGuid():N}");
    private readonly PlainTextExtractor _extractor = new(1024);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    private ScannedFile Record(string path)
    {
        var info = new FileInfo(path);
        return new(path, info.Name, info.Extension, info.Length, info.LastWriteTimeUtc);
    }

    private async Task<ScannedFile> CreateFile(byte[] bytes, string name = "notes.txt")
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        await File.WriteAllBytesAsync(path, bytes, Token);
        return Record(path);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".csv")]
    [InlineData(".json")]
    [InlineData(".xml")]
    [InlineData(".c")]
    [InlineData(".cpp")]
    [InlineData(".h")]
    [InlineData(".hpp")]
    [InlineData(".cs")]
    [InlineData(".java")]
    [InlineData(".py")]
    [InlineData(".js")]
    [InlineData(".ts")]
    [InlineData(".html")]
    [InlineData(".css")]
    [InlineData(".yml")]
    [InlineData(".yaml")]
    [InlineData(".sql")]
    [InlineData(".TXT")]
    public async Task SupportsPlannedExtensionsAndNormalizesLineEndings(string extension)
    {
        var file = await CreateFile(Encoding.UTF8.GetBytes("café 123\r\nclear-sky\rindex\n"), "file" + extension);
        var result = await _extractor.ExtractAsync(file, Token);
        Assert.Equal(TextExtractionStatus.Extracted, result.Status);
        Assert.Equal("café 123\nclear-sky\nindex\n", result.Text);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    [InlineData("utf32be")]
    public async Task RecognizesBomWithoutSavingItAsContent(string name)
    {
        Encoding encoding = name switch
        {
            "utf8" => new UTF8Encoding(true, true),
            "utf16le" => new UnicodeEncoding(false, true, true),
            "utf16be" => new UnicodeEncoding(true, true, true),
            "utf32le" => new UTF32Encoding(false, true, true),
            _ => new UTF32Encoding(true, true, true)
        };
        const string text = "ingat 123 — matahari ☀";
        var file = await CreateFile([.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);
        Assert.Equal(text, (await _extractor.ExtractAsync(file, Token)).Text);
    }

    [Fact]
    public async Task SizeLimitIsInBytesAndNeverTruncatesText()
    {
        var file = await CreateFile(Encoding.UTF8.GetBytes("éé"));
        Assert.Equal("éé", (await new PlainTextExtractor(4).ExtractAsync(file, Token)).Text);
        var rejected = await new PlainTextExtractor(3).ExtractAsync(file, Token);
        Assert.Equal(TextExtractionStatus.TooLarge, rejected.Status);
        Assert.Null(rejected.Text);
    }

    [Fact]
    public async Task RejectsInvalidUtf8AndMalformedBomEncodings()
    {
        foreach (byte[] bytes in new byte[][]
        {
            [0xc3, 0x28], [0xff, 0xfe, 0x00, 0xd8], [0xff, 0xfe, 0x00, 0x00, 0x41]
        })
        {
            var file = await CreateFile(bytes);
            var result = await _extractor.ExtractAsync(file, Token);
            Assert.Equal(TextExtractionStatus.InvalidEncoding, result.Status);
            Assert.Null(result.Text);
        }
    }

    [Fact]
    public async Task BinaryDataWithTextExtensionIsNotSavedAsText()
    {
        var file = await CreateFile([65, 0, 66]);
        var result = await _extractor.ExtractAsync(file, Token);
        Assert.Equal(TextExtractionStatus.Binary, result.Status);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task EmptyFileIsValidText()
    {
        var file = await CreateFile([]);
        var result = await _extractor.ExtractAsync(file, Token);
        Assert.Equal(TextExtractionStatus.Extracted, result.Status);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task UnsupportedExtensionIsSkippedWithoutOpeningThePath()
    {
        var file = new ScannedFile("/not-selected/missing.pdf", "missing.pdf", ".pdf", 0, DateTime.UtcNow);
        Assert.Equal(TextExtractionStatus.Unsupported, (await _extractor.ExtractAsync(file, Token)).Status);
    }

    [Fact]
    public async Task FileDeletedOrChangedAfterScanIsSkipped()
    {
        var file = await CreateFile(Encoding.UTF8.GetBytes("before"));
        await File.WriteAllTextAsync(file.FullPath, "changed size", Token);
        Assert.Equal(TextExtractionStatus.Changed, (await _extractor.ExtractAsync(file, Token)).Status);
        File.Delete(file.FullPath);
        Assert.Equal(TextExtractionStatus.Unavailable, (await _extractor.ExtractAsync(file, Token)).Status);
    }

    [Fact]
    public async Task ChangedTimestampIsDetectedEvenWhenSizeMatches()
    {
        var file = await CreateFile(Encoding.UTF8.GetBytes("before"));
        File.SetLastWriteTimeUtc(file.FullPath, file.ModifiedAtUtc.AddSeconds(10));
        Assert.Equal(TextExtractionStatus.Changed, (await _extractor.ExtractAsync(file, Token)).Status);
    }

    [Fact]
    public async Task SymlinkAndLinkedParentAreNeverRead()
    {
        var file = await CreateFile(Encoding.UTF8.GetBytes("private"));
        var link = Path.Combine(_directory, "link.txt");
        File.CreateSymbolicLink(link, file.FullPath);
        Assert.Equal(TextExtractionStatus.SymbolicLink, (await _extractor.ExtractAsync(Record(link), Token)).Status);
        var directoryLink = Path.Combine(_directory, "linked-directory");
        Directory.CreateSymbolicLink(directoryLink, _directory);
        var linkedFile = Record(Path.Combine(directoryLink, "notes.txt"));
        Assert.Equal(TextExtractionStatus.SymbolicLink, (await _extractor.ExtractAsync(linkedFile, Token)).Status);
    }

    [Fact]
    public async Task LinuxFifoWithTextExtensionDoesNotBlock()
    {
        if (!OperatingSystem.IsLinux())
            return;
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "pipe.txt");
        Assert.Equal(0, MkFifo(path, 0x180)); // owner read/write
        var task = _extractor.ExtractAsync(Record(path), Token);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        Assert.Equal(TextExtractionStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task PermissionDeniedIsSkippedAndDoesNotModifyTheDocument()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var file = await CreateFile(Encoding.UTF8.GetBytes("private"));
        File.SetUnixFileMode(file.FullPath, UnixFileMode.None);
        try
        {
            Assert.Equal(TextExtractionStatus.Unavailable, (await _extractor.ExtractAsync(file, Token)).Status);
        }
        finally
        {
            File.SetUnixFileMode(file.FullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Assert.Equal("private", await File.ReadAllTextAsync(file.FullPath, Token));
        Assert.Equal(file.ModifiedAtUtc, File.GetLastWriteTimeUtc(file.FullPath));
    }

    [Fact]
    public async Task ReadingPreservesOriginalBytesAndModifiedTimestamp()
    {
        byte[] bytes = [0xef, 0xbb, 0xbf, 65, 13, 10, 66];
        var file = await CreateFile(bytes);
        await _extractor.ExtractAsync(file, Token);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file.FullPath, Token));
        Assert.Equal(file.ModifiedAtUtc, File.GetLastWriteTimeUtc(file.FullPath));
    }

    [Fact]
    public async Task CancellationIsPropagated()
    {
        var file = await CreateFile(Encoding.UTF8.GetBytes("123"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _extractor.ExtractAsync(file, cancellation.Token));
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
