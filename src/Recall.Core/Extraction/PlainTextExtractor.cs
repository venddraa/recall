using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Recall.Core.Scanning;

namespace Recall.Core.Extraction;

public sealed class PlainTextExtractor
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".c", ".cpp", ".h", ".hpp",
        ".cs", ".java", ".py", ".js", ".ts", ".html", ".css", ".yml", ".yaml", ".sql"
    };

    public PlainTextExtractor(int maximumFileBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        MaximumFileBytes = maximumFileBytes;
    }

    public int MaximumFileBytes { get; }

    public Task<TextExtractionResult> ExtractAsync(ScannedFile file, CancellationToken cancellationToken = default)
        => Task.Run(() => Extract(file, cancellationToken), cancellationToken);

    internal bool CanReuse(ScannedFile file)
    {
        try
        {
            if (file.Size > MaximumFileBytes || HasSymbolicLink(file.FullPath))
                return false;
            using var stream = OpenReadOnly(file.FullPath);
            var info = new FileInfo(file.FullPath);
            return info.Exists && info.Length == file.Size && info.LastWriteTimeUtc == file.ModifiedAtUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    internal TextExtractionResult Extract(ScannedFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Extensions.Contains(Path.GetExtension(file.FullPath)))
            return new(TextExtractionStatus.Unsupported);

        try
        {
            if (HasSymbolicLink(file.FullPath))
                return new(TextExtractionStatus.SymbolicLink);

            var info = new FileInfo(file.FullPath);
            if (!info.Exists)
                return new(TextExtractionStatus.Unavailable);
            if (info.Length > MaximumFileBytes)
                return new(TextExtractionStatus.TooLarge);
            if (info.Length != file.Size || info.LastWriteTimeUtc != file.ModifiedAtUtc)
                return new(TextExtractionStatus.Changed);

            using var stream = OpenReadOnly(file.FullPath);
            using var bytes = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bytes.Length + read > MaximumFileBytes)
                    return new(TextExtractionStatus.TooLarge);
                bytes.Write(buffer, 0, read);
            }

            cancellationToken.ThrowIfCancellationRequested();
            info.Refresh();
            if (HasSymbolicLink(file.FullPath))
                return new(TextExtractionStatus.SymbolicLink);
            if (!info.Exists || info.Length != file.Size || info.LastWriteTimeUtc != file.ModifiedAtUtc
                || bytes.Length != file.Size)
                return new(TextExtractionStatus.Changed);

            var text = Decode(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
            if (text.Any(character => char.IsControl(character) && character is not ('\t' or '\r' or '\n' or '\f')))
                return new(TextExtractionStatus.Binary);
            return new(TextExtractionStatus.Extracted, text.ReplaceLineEndings("\n"));
        }
        catch (DecoderFallbackException)
        {
            return new(TextExtractionStatus.InvalidEncoding);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new(TextExtractionStatus.Unavailable);
        }
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        // Check UTF-32 before UTF-16: the little-endian BOMs share their first two bytes.
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 }))
            return new UTF32Encoding(false, false, true).GetString(bytes[4..]);
        if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff }))
            return new UTF32Encoding(true, false, true).GetString(bytes[4..]);
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe }))
            return new UnicodeEncoding(false, false, true).GetString(bytes[2..]);
        if (bytes.StartsWith(new byte[] { 0xfe, 0xff }))
            return new UnicodeEncoding(true, false, true).GetString(bytes[2..]);
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            bytes = bytes[3..];
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static bool HasSymbolicLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return true;
        for (var directory = new FileInfo(path).Directory; directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }

    private static FileStream OpenReadOnly(string path)
    {
        if (!OperatingSystem.IsLinux())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // A FIFO named *.txt must never block indexing. O_NOFOLLOW also rejects a replaced file symlink.
        var descriptor = NativeMethods.Open(path, 0x80000 | 0x800 | 0x20000); // CLOEXEC | NONBLOCK | NOFOLLOW, RDONLY = 0
        if (descriptor < 0)
            throw new IOException("Could not open the file read-only.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            if (NativeMethods.Statx(descriptor, "", 0x1000, 1, out var stat) != 0 || (stat.Mode & 0xf000) != 0x8000)
                throw new IOException("Only regular files can be read for content indexing.");
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Explicit, Size = 256)]
        internal struct FileStat
        {
            [FieldOffset(28)] internal ushort Mode;
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        internal static extern int Statx(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags, uint mask, out FileStat stat);
    }
}
