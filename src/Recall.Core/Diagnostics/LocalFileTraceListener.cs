using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using Recall.Core.Folders;

namespace Recall.Core.Diagnostics;

public sealed class LocalFileTraceListener : TraceListener
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly long _maximumBytes;
    private bool _unavailable;
    private bool _closed;

    public LocalFileTraceListener(string filePath, long maximumBytes = 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 128);
        _filePath = FolderPath.Normalize(filePath);
        if (Path.GetDirectoryName(_filePath) is null)
            throw new ArgumentException("A log file path is required.", nameof(filePath));
        _maximumBytes = maximumBytes;
    }

    public override bool IsThreadSafe => true;
    public bool IsAvailable { get { lock (_gate) return !_unavailable && !_closed; } }

    public void SessionStarted() => Append(TraceEventType.Information, "Recall session started");
    public void SessionEnded() => Append(TraceEventType.Information, "Recall session ended");

    // Unstructured UI traces may contain binding values. Persist only application events.
    public override void Write(string? message) { }
    public override void WriteLine(string? message) { }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType,
        int id, string? message)
    {
        if (eventType is TraceEventType.Critical or TraceEventType.Error or TraceEventType.Warning)
            Append(eventType, message ?? "");
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType,
        int id, string? format, params object?[]? args)
    {
        if (eventType is not (TraceEventType.Critical or TraceEventType.Error or TraceEventType.Warning))
            return;
        var safeArgs = args?.Select(SafeArgument).ToArray();
        string message;
        try
        {
            message = safeArgs is null ? format ?? ""
                : string.Format(CultureInfo.InvariantCulture, format ?? "", safeArgs);
        }
        catch (FormatException)
        {
            message = "A diagnostic event could not be formatted.";
        }
        Append(eventType, message);
    }

    private static object? SafeArgument(object? argument)
    {
        // Keep diagnostic call sites, but not exception messages/inner exceptions that may contain parser input.
        return argument is Exception exception
            ? $"{exception.GetType().Name} (0x{exception.HResult:X8}) {exception.StackTrace}"
            : argument;
    }

    private void Append(TraceEventType level, string message)
    {
        lock (_gate)
        {
            if (_unavailable || _closed)
                return;
            try
            {
                var directory = Path.GetDirectoryName(_filePath)!;
                if (OperatingSystem.IsLinux())
                    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                else
                    Directory.CreateDirectory(directory);
                // Never append diagnostics through a linked log file or replace a linked backup.
                if (new FileInfo(_filePath).LinkTarget is not null
                    || new FileInfo(_filePath + ".1").LinkTarget is not null)
                {
                    _unavailable = true;
                    return;
                }
                var bytes = Encoding.UTF8.GetBytes(FormatRecord(level, message));
                var existing = new FileInfo(_filePath);
                if (existing.Exists && existing.Length + bytes.Length > _maximumBytes)
                    File.Move(_filePath, _filePath + ".1", overwrite: true);
                var options = new FileStreamOptions
                {
                    Mode = FileMode.Append,
                    Access = FileAccess.Write,
                    Share = FileShare.Read | FileShare.Delete
                };
                if (OperatingSystem.IsLinux())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var stream = new FileStream(_filePath, options);
                stream.Write(bytes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Logging must never stop search/indexing, and must not recursively log its own failure.
                _unavailable = true;
            }
        }
    }

    private string FormatRecord(TraceEventType level, string message)
    {
        var prefix = $"{DateTimeOffset.UtcNow:O} [{level}] ";
        var remaining = (int)Math.Min(_maximumBytes, 4096) - Encoding.UTF8.GetByteCount(prefix) - 4;
        var builder = new StringBuilder(prefix);
        foreach (var rune in message.EnumerateRunes())
        {
            var text = Rune.IsControl(rune) ? $"\\u{rune.Value:X4}" : rune.ToString();
            var size = Encoding.UTF8.GetByteCount(text);
            if (size > remaining)
            {
                builder.Append('…');
                break;
            }
            builder.Append(text);
            remaining -= size;
        }
        return builder.Append('\n').ToString();
    }

    public override void Close() { lock (_gate) _closed = true; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close();
        base.Dispose(disposing);
    }
}
