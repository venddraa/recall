using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using Recall.Core.Scanning;

namespace Recall.Core.Extraction;

internal sealed class NotebookTextExtractor(int maximumFileBytes, int maximumTextCharacters) : ITextExtractor
{
    public bool CanReuse(ScannedFile file) => ReadOnlyFileAccess.CanReuse(file, maximumFileBytes);

    public TextExtractionResult Extract(ScannedFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (ReadOnlyFileAccess.HasSymbolicLink(file.FullPath))
                return new(TextExtractionStatus.SymbolicLink);
            var info = new FileInfo(file.FullPath);
            if (!info.Exists)
                return new(TextExtractionStatus.Unavailable);
            if (info.Length > maximumFileBytes)
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
                if (bytes.Length + read > maximumFileBytes)
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

            var json = bytes.GetBuffer().AsMemory(0, (int)bytes.Length);
            if (json.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
                json = json[3..];
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            cancellationToken.ThrowIfCancellationRequested();
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("nbformat", out var format)
                || !format.TryGetInt32(out var version) || version <= 0)
                throw new InvalidDataException("The notebook format is missing or invalid.");
            if (version != 4)
                return new(TextExtractionStatus.Unsupported);
            if (!root.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The notebook cells are missing or invalid.");

            var text = ReadCells(cells, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            info.Refresh();
            if (ReadOnlyFileAccess.HasSymbolicLink(file.FullPath))
                return new(TextExtractionStatus.SymbolicLink);
            if (!info.Exists || info.Length != file.Size || info.LastWriteTimeUtc != file.ModifiedAtUtc)
                return new(TextExtractionStatus.Changed);
            return string.IsNullOrWhiteSpace(text)
                ? new(TextExtractionStatus.NoSearchableText)
                : new(TextExtractionStatus.Extracted, text);
        }
        catch (TextLimitExceededException)
        {
            return new(TextExtractionStatus.TooLarge);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException)
        {
            // JSON exception messages can contain document text. Log the type only.
            Trace.TraceWarning("Notebook parsing failed for {0}: {1}", file.FullPath, exception.GetType().Name);
            return new(TextExtractionStatus.ParserFailed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new(TextExtractionStatus.Unavailable);
        }
    }

    private string ReadCells(JsonElement cells, CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        foreach (var cell in cells.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cell.ValueKind != JsonValueKind.Object || !cell.TryGetProperty("cell_type", out var type)
                || type.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The notebook cell type is missing or invalid.");
            var cellType = type.GetString();
            if (cellType is not ("markdown" or "code" or "raw"))
                continue;
            if (!cell.TryGetProperty("source", out var source))
                throw new InvalidDataException("The notebook cell source is missing.");
            AppendBlock(source);

            if (cellType != "code" || !cell.TryGetProperty("outputs", out var outputs))
                continue;
            if (outputs.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The notebook outputs are invalid.");
            foreach (var output in outputs.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (output.ValueKind != JsonValueKind.Object || !output.TryGetProperty("output_type", out var outputType)
                    || outputType.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("The notebook output type is missing or invalid.");
                switch (outputType.GetString())
                {
                    case "stream":
                        if (!output.TryGetProperty("text", out var streamText))
                            throw new InvalidDataException("The notebook stream text is missing.");
                        AppendBlock(streamText);
                        break;
                    case "display_data" or "execute_result":
                        if (!output.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException("The notebook output data is missing or invalid.");
                        if (data.TryGetProperty("text/plain", out var plainText))
                            AppendBlock(plainText);
                        break;
                }
            }
        }
        return content.ToString().ReplaceLineEndings("\n");

        void AppendBlock(JsonElement value)
        {
            if (content.Length > 0)
                Append("\n");
            if (value.ValueKind == JsonValueKind.String)
                Append(value.GetString()!);
            else if (value.ValueKind == JsonValueKind.Array)
            {
                // nbformat stores multiline strings as either one string or fragments joined without separators.
                foreach (var part in value.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (part.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("Notebook text fragments must be strings.");
                    Append(part.GetString()!);
                }
            }
            else
                throw new InvalidDataException("Notebook text must be a string or an array of strings.");
        }

        void Append(string value)
        {
            if (content.Length + value.Length > maximumTextCharacters)
                throw new TextLimitExceededException();
            content.Append(value);
        }
    }

    private sealed class TextLimitExceededException : Exception;
}
