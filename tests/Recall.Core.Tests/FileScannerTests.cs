using Recall.Core.Scanning;

namespace Recall.Core.Tests;

public sealed class FileScannerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-scanner-tests-{Guid.NewGuid():N}");
    private readonly FileScanner _scanner = new();

    private string CreateFile(string relativePath, string text = "123")
    {
        var path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task ReadsNestedMetadataAndIncludesHiddenAndUnknownFileTypes()
    {
        var note = CreateFile("selected/notes.txt");
        var hidden = CreateFile("selected/.hidden/config.json");
        var binary = CreateFile("selected/nested/archive.bin");
        CreateFile("outside/not-selected.txt");
        var timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(note, timestamp);

        var result = await _scanner.ScanAsync([Path.Combine(_directory, "selected")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { hidden, binary, note }.Order(StringComparer.Ordinal),
            result.Files.Select(file => file.FullPath).Order(StringComparer.Ordinal));
        var metadata = Assert.Single(result.Files, file => file.FullPath == note);
        Assert.Equal("notes.txt", metadata.Filename);
        Assert.Equal(".txt", metadata.Extension);
        Assert.Equal(3, metadata.Size);
        Assert.Equal(timestamp, metadata.ModifiedAtUtc);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task OverlappingAndRepeatedRootsDoNotDuplicateFiles()
    {
        var first = CreateFile("selected/first.txt");
        var second = CreateFile("selected/child/second.txt");
        var root = Path.Combine(_directory, "selected");
        var child = Path.Combine(root, "child");

        var result = await _scanner.ScanAsync([child, root, root + "/", child],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal),
            result.Files.Select(file => file.FullPath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task MissingRootIsReportedWhileOtherFoldersStillScan()
    {
        var existing = CreateFile("selected/notes.txt");
        var missing = Path.Combine(_directory, "removed-folder");

        var result = await _scanner.ScanAsync([missing, Path.GetDirectoryName(existing)!],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(existing, Assert.Single(result.Files).FullPath);
        Assert.Equal(missing, Assert.Single(result.Failures).Path);
    }

    [Fact]
    public async Task DirectoryLinksFileLinksAndLoopsAreSkipped()
    {
        var inside = CreateFile("selected/inside.txt");
        var outside = CreateFile("outside/secret.txt");
        var root = Path.GetDirectoryName(inside)!;
        Directory.CreateSymbolicLink(Path.Combine(root, "outside-link"), Path.GetDirectoryName(outside)!);
        Directory.CreateSymbolicLink(Path.Combine(root, "loop"), root);
        File.CreateSymbolicLink(Path.Combine(root, "file-link.txt"), outside);
        File.CreateSymbolicLink(Path.Combine(root, "broken-link.txt"), Path.Combine(_directory, "missing.txt"));

        var result = await _scanner.ScanAsync([root], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(inside, Assert.Single(result.Files).FullPath);
        Assert.Equal(4, result.SkippedLinks);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task ExplicitSymbolicLinkRootIsNotTraversed()
    {
        CreateFile("outside/secret.txt");
        var link = Path.Combine(_directory, "selected-link");
        Directory.CreateSymbolicLink(link, Path.Combine(_directory, "outside"));

        var result = await _scanner.ScanAsync([link], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Files);
        Assert.Equal(1, result.SkippedLinks);
    }

    [Fact]
    public async Task EmptySelectionAndEmptyFolderProduceEmptyResults()
    {
        Directory.CreateDirectory(_directory);
        Assert.Empty((await _scanner.ScanAsync([], cancellationToken: TestContext.Current.CancellationToken)).Files);
        Assert.Empty((await _scanner.ScanAsync([_directory], cancellationToken: TestContext.Current.CancellationToken)).Files);
    }

    [Fact]
    public async Task PreCancelledScanDoesNotStart()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _scanner.ScanAsync([_directory],
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task CancellationRequestedFromProgressIsHonored()
    {
        CreateFile("selected/notes.txt");
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _scanner.ScanAsync(
            [Path.Combine(_directory, "selected")], progress, cancellation.Token));
    }

    [Fact]
    public async Task FinalProgressMatchesSummaryAndScanningDoesNotChangeDocuments()
    {
        var file = CreateFile("selected/original.txt", "Keep this document unchanged.");
        var before = File.ReadAllBytes(file);
        var modifiedAt = File.GetLastWriteTimeUtc(file);
        ScanProgress? final = null;
        var progress = new ImmediateProgress(value => final = value);

        var result = await _scanner.ScanAsync([Path.GetDirectoryName(file)!], progress,
            TestContext.Current.CancellationToken);

        Assert.NotNull(final);
        Assert.Equal(result.Files.Count, final.FileCount);
        Assert.Equal(result.Failures.Count, final.FailureCount);
        Assert.Equal(result.SkippedLinks, final.SkippedLinks);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(modifiedAt, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public async Task InaccessibleDirectoryDoesNotPreventScanningReadableEntries()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
        {
            return;
        }

        var readable = CreateFile("selected/readable.txt");
        var blocked = Path.Combine(_directory, "selected", "blocked");
        Directory.CreateDirectory(blocked);
        File.SetUnixFileMode(blocked, UnixFileMode.None);
        try
        {
            var result = await _scanner.ScanAsync([Path.Combine(_directory, "selected")],
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(readable, Assert.Single(result.Files).FullPath);
            Assert.Equal(blocked, Assert.Single(result.Failures).Path);
        }
        finally
        {
            File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private sealed class ImmediateProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
