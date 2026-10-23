using System.Diagnostics;
using System.Globalization;
using System.Text;
using Recall.Core.Diagnostics;

namespace Recall.Core.Tests;

public sealed class LocalFileTraceListenerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-log-tests-{Guid.NewGuid():N}");
    private string LogPath => Path.Combine(_directory, "recall.log");

    [Fact]
    public void RecordsWarningsErrorsAndSessionEventsButNotInfoOrRawUiValues()
    {
        using var log = new LocalFileTraceListener(LogPath);
        log.SessionStarted();
        log.TraceEvent(null, "Recall", TraceEventType.Information, 0, "skipped file info");
        log.TraceEvent(null, "Recall", TraceEventType.Verbose, 0, "verbose detail");
        log.Write("private UI value");
        log.WriteLine("private UI value");
        foreach (var level in new[] { TraceEventType.Warning, TraceEventType.Error, TraceEventType.Critical })
            log.TraceEvent(null, "Recall", level, 0, "diagnostic");
        log.SessionEnded();

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(5, lines.Length);
        Assert.Contains("Recall session started", lines[0]);
        Assert.Contains("Recall session ended", lines[^1]);
        Assert.DoesNotContain("private UI value", string.Join("\n", lines));
        foreach (var line in lines)
        {
            Assert.True(DateTimeOffset.TryParse(line.Split(" [")[0], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time));
            Assert.Equal(TimeSpan.Zero, time.Offset);
        }
        Assert.True(log.IsAvailable);
    }

    [Fact]
    public void ExceptionsDoNotPersistTheirMessagesOrInnerExceptionContents()
    {
        using var log = new LocalFileTraceListener(LogPath);
        var error = new InvalidDataException("PRIVATE_DOCUMENT_BODY", new Exception("INNER_PRIVATE_BODY"));
        try { throw error; }
        catch (InvalidDataException) { }
        log.TraceEvent(null, "Recall", TraceEventType.Error, 0, "Parser failed: {0}", error);

        var contents = File.ReadAllText(LogPath);
        Assert.Contains("InvalidDataException", contents);
        Assert.Contains("0x", contents);
        Assert.Contains(nameof(ExceptionsDoNotPersistTheirMessagesOrInnerExceptionContents), contents);
        Assert.DoesNotContain("PRIVATE_DOCUMENT_BODY", contents);
        Assert.DoesNotContain("INNER_PRIVATE_BODY", contents);
    }

    [Fact]
    public void RotationKeepsOnlyOneBackupAndBoundsBothFiles()
    {
        using var log = new LocalFileTraceListener(LogPath, 256);
        for (var index = 0; index < 12; index++)
            log.TraceEvent(null, "Recall", TraceEventType.Warning, 0, $"event-{index} " + new string('x', 70));

        var files = Directory.GetFiles(_directory);
        Assert.Equal(2, files.Length);
        Assert.Contains(LogPath + ".1", files);
        Assert.All(files, path => Assert.InRange(new FileInfo(path).Length, 1L, 256L));
        Assert.Contains("event-11", File.ReadAllText(LogPath));
    }

    [Fact]
    public void OversizedUnicodeMessagesAreTruncatedWithoutBreakingUtf8()
    {
        using var log = new LocalFileTraceListener(LogPath, 128);
        log.TraceEvent(null, "Recall", TraceEventType.Warning, 0, string.Concat(Enumerable.Repeat("🐧", 1000)));

        var bytes = File.ReadAllBytes(LogPath);
        var text = new UTF8Encoding(false, true).GetString(bytes);
        Assert.InRange(bytes.Length, 1, 128);
        Assert.Contains('…', text);
        Assert.Single(File.ReadAllLines(LogPath));
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
    }

    [Fact]
    public void ControlCharactersCannotCreateExtraLogRecords()
    {
        using var log = new LocalFileTraceListener(LogPath);
        log.TraceEvent(null, "Recall", TraceEventType.Warning, 0, "first\nsecond\t\0");

        var line = Assert.Single(File.ReadAllLines(LogPath));
        Assert.Contains("first\\u000Asecond\\u0009\\u0000", line);
    }

    [Fact]
    public void InvalidMessageFormatsDoNotThrow()
    {
        using var log = new LocalFileTraceListener(LogPath);
        log.TraceEvent(null, "Recall", TraceEventType.Error, 0, "invalid {2}", "PRIVATE_INPUT");
        Assert.Contains("could not be formatted", File.ReadAllText(LogPath));
        Assert.DoesNotContain("PRIVATE_INPUT", File.ReadAllText(LogPath));
    }

    [Fact]
    public void UnwritableDestinationDoesNotThrowOrOverwriteOtherFiles()
    {
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "blocked");
        File.WriteAllText(blocked, "keep this file");
        using var log = new LocalFileTraceListener(Path.Combine(blocked, "recall.log"));

        log.TraceEvent(null, "Recall", TraceEventType.Error, 0, "error");
        log.SessionEnded();

        Assert.False(log.IsAvailable);
        Assert.Equal("keep this file", File.ReadAllText(blocked));
    }

    [Fact]
    public void FailedRotationLeavesTheCurrentLogIntact()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(LogPath, new string('x', 250));
        Directory.CreateDirectory(LogPath + ".1");
        using var log = new LocalFileTraceListener(LogPath, 256);

        log.TraceEvent(null, "Recall", TraceEventType.Error, 0, "rotation fails");

        Assert.False(log.IsAvailable);
        Assert.Equal(new string('x', 250), File.ReadAllText(LogPath));
    }

    [Fact]
    public async Task ConcurrentEventsRemainSeparateAndComplete()
    {
        using var log = new LocalFileTraceListener(LogPath);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
            log.TraceEvent(null, "Recall", TraceEventType.Warning, 0, $"event-{index}"),
            TestContext.Current.CancellationToken)));

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(100, lines.Length);
        foreach (var index in Enumerable.Range(0, 100))
            Assert.Single(lines, line => line.EndsWith($"event-{index}", StringComparison.Ordinal));
    }

    [Fact]
    public void ClosedListenerDoesNotCreateFiles()
    {
        using var log = new LocalFileTraceListener(LogPath);
        log.Close();
        log.SessionStarted();
        log.TraceEvent(null, "Recall", TraceEventType.Error, 0, "late event");
        Assert.False(log.IsAvailable);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void LogFilesAndBackupsArePrivateOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var log = new LocalFileTraceListener(LogPath, 128);
        for (var index = 0; index < 5; index++)
            log.TraceEvent(null, "Recall", TraceEventType.Error, 0, new string('x', 60));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(_directory));
        foreach (var path in Directory.GetFiles(_directory))
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void LinkedLogFileCannotModifyItsTarget()
    {
        Directory.CreateDirectory(_directory);
        var document = Path.Combine(_directory, "original.txt");
        File.WriteAllText(document, "original document");
        File.CreateSymbolicLink(LogPath, document);
        using var log = new LocalFileTraceListener(LogPath);
        log.SessionStarted();
        Assert.False(log.IsAvailable);
        Assert.Equal("original document", File.ReadAllText(document));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(127)]
    public void TooSmallLogLimitsAreRejected(long size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalFileTraceListener(LogPath, size));

    [Fact]
    public void RelativeAndRootOnlyPathsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new LocalFileTraceListener("relative.log"));
        Assert.Throws<ArgumentException>(() => new LocalFileTraceListener(Path.GetPathRoot(_directory)!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
