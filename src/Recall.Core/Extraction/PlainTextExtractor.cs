using System.Security;
using System.Text;
using Recall.Core.Scanning;

namespace Recall.Core.Extraction;

public sealed class PlainTextExtractor : ITextExtractor
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

    public bool CanReuse(ScannedFile file) => ReadOnlyFileAccess.CanReuse(file, MaximumFileBytes);

    public TextExtractionResult Extract(ScannedFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Extensions.Contains(Path.GetExtension(file.FullPath)))
            return new(TextExtractionStatus.Unsupported);

        try
        {
            if (ReadOnlyFileAccess.HasSymbolicLink(file.FullPath))
                return new(TextExtractionStatus.SymbolicLink);

            var info = new FileInfo(file.FullPath);
            if (!info.Exists)
                return new(TextExtractionStatus.Unavailable);
            if (info.Length > MaximumFileBytes)
                return new(TextExtractionStatus.TooLarge);
            if (info.Length != file.Size || info.LastWriteTimeUtc != file.ModifiedAtUtc)
                return new(TextExtractionStatus.Changed);

            using var stream = ReadOnlyFileAccess.Open(file.FullPath);
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
            if (ReadOnlyFileAccess.HasSymbolicLink(file.FullPath))
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
}
