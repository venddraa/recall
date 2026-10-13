using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml;
using Recall.Core.Scanning;

namespace Recall.Core.Extraction;

internal sealed class DocxTextExtractor(int maximumFileBytes, int maximumTextCharacters) : ITextExtractor
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string StrictWordNamespace = "http://purl.oclc.org/ooxml/wordprocessingml/main";
    private const string CompatibilityNamespace = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const long MaximumXmlBytes = 40 * 1024 * 1024;

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
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var document = archive.GetEntry("word/document.xml")
                ?? throw new InvalidDataException("The DOCX document part is missing.");
            if (document.Length > MaximumXmlBytes)
                return new(TextExtractionStatus.TooLarge);
            using var xmlStream = document.Open();
            var text = ReadBodyText(xmlStream, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            info.Refresh();
            if (ReadOnlyFileAccess.HasSymbolicLink(file.FullPath))
                return new(TextExtractionStatus.SymbolicLink);
            if (!info.Exists || info.Length != file.Size || info.LastWriteTimeUtc != file.ModifiedAtUtc
                || stream.Length != file.Size)
                return new(TextExtractionStatus.Changed);
            return string.IsNullOrWhiteSpace(text)
                ? new(TextExtractionStatus.NoSearchableText)
                : new(TextExtractionStatus.Extracted, text);
        }
        catch (TextLimitExceededException)
        {
            return new(TextExtractionStatus.TooLarge);
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or NotSupportedException)
        {
            // Parser messages can include document text. Log the path and exception type only.
            Trace.TraceWarning("DOCX parsing failed for {0}: {1}", file.FullPath, exception.GetType().Name);
            return new(TextExtractionStatus.ParserFailed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new(TextExtractionStatus.Unavailable);
        }
    }

    private string ReadBodyText(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        });
        var content = new StringBuilder();
        var buffer = new char[4096];
        var bodyDepth = -1;
        var textDepth = -1;
        var skipDepth = -1;
        var bodyFound = false;
        var alternateContent = new Stack<(int Depth, bool BranchRead)>();
        string? documentNamespace = null;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Depth > 256)
                throw new InvalidDataException("DOCX XML nesting is too deep.");
            if (skipDepth >= 0)
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == skipDepth)
                    skipDepth = -1;
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
            {
                if (reader.LocalName != "document" || reader.NamespaceURI is not (WordNamespace or StrictWordNamespace))
                    throw new InvalidDataException("The DOCX document root is invalid.");
                documentNamespace = reader.NamespaceURI;
            }
            // Word may store the same text box in a modern branch and a compatibility fallback.
            if (bodyDepth >= 0 && reader.NamespaceURI == CompatibilityNamespace)
            {
                if (reader.LocalName == "AlternateContent")
                {
                    if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
                        alternateContent.Push((reader.Depth, false));
                    else if (reader.NodeType == XmlNodeType.EndElement && alternateContent.Count > 0)
                        alternateContent.Pop();
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "Choice" or "Fallback"
                    && alternateContent.TryPeek(out var alternate) && reader.Depth == alternate.Depth + 1)
                {
                    if (alternate.BranchRead)
                    {
                        if (!reader.IsEmptyElement)
                            skipDepth = reader.Depth;
                    }
                    else
                    {
                        alternateContent.Pop();
                        alternateContent.Push((alternate.Depth, true));
                    }
                }
            }
            if (reader.NamespaceURI == documentNamespace)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName == "body" && reader.Depth == 1)
                    {
                        bodyFound = true;
                        bodyDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                    }
                    else if (bodyDepth >= 0)
                    {
                        switch (reader.LocalName)
                        {
                            case "del" or "moveFrom" when !reader.IsEmptyElement:
                                skipDepth = reader.Depth;
                                break;
                            case "t" when !reader.IsEmptyElement:
                                textDepth = reader.Depth;
                                break;
                            case "tab" or "ptab":
                                Append("\t");
                                break;
                            case "noBreakHyphen":
                                Append("-");
                                break;
                            case "br" or "cr":
                                Append("\n");
                                break;
                        }
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.Depth == textDepth)
                        textDepth = -1;
                    if (bodyDepth >= 0 && reader.LocalName is "p" or "tc" or "tr")
                        Append("\n");
                    if (reader.Depth == bodyDepth)
                        bodyDepth = -1;
                }
            }
            if (textDepth >= 0 && reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
            {
                int read;
                while ((read = reader.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (content.Length + read > maximumTextCharacters)
                        throw new TextLimitExceededException();
                    content.Append(buffer, 0, read);
                }
            }
        }
        if (!bodyFound)
            throw new InvalidDataException("The DOCX document body is missing.");
        return content.ToString().ReplaceLineEndings("\n");

        void Append(string value)
        {
            if (content.Length + value.Length > maximumTextCharacters)
                throw new TextLimitExceededException();
            content.Append(value);
        }
    }

    private sealed class TextLimitExceededException : Exception;
}
