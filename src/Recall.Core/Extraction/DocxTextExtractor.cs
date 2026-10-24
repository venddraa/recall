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
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string StrictRelationshipNamespace = "http://purl.oclc.org/ooxml/officeDocument/relationships";
    private const string PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
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
            var references = new DocumentReferences();
            var content = new StringBuilder();
            using (var xmlStream = OpenPart(document, references))
                ReadPartText(xmlStream, "document", content, references, cancellationToken);
            foreach (var (partPath, rootName) in ReadRelatedParts(archive, references, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = archive.GetEntry(partPath)
                    ?? throw new InvalidDataException("A referenced DOCX part is missing.");
                if (content.Length > 0 && content[^1] != '\n')
                {
                    if (content.Length >= maximumTextCharacters)
                        throw new TextLimitExceededException();
                    content.Append('\n');
                }
                using var partStream = OpenPart(part, references);
                ReadPartText(partStream, rootName, content, references, cancellationToken);
            }
            var text = content.ToString().ReplaceLineEndings("\n");

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

    private static Stream OpenPart(ZipArchiveEntry part, DocumentReferences references)
    {
        if (part.Length > MaximumXmlBytes - references.XmlBytesRead)
            throw new TextLimitExceededException();
        references.XmlBytesRead += part.Length;
        return part.Open();
    }

    private static XmlReader CreateReader(Stream stream) => XmlReader.Create(stream, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaximumXmlBytes,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true
    });

    private static IEnumerable<(string Path, string RootName)> ReadRelatedParts(ZipArchive archive,
        DocumentReferences references, CancellationToken cancellationToken)
    {
        if (references.Headers.Count + references.Footers.Count + references.Footnotes.Count + references.Endnotes.Count == 0)
            yield break;
        var relationships = archive.GetEntry("word/_rels/document.xml.rels");
        if (relationships is null)
            yield break;

        using var stream = OpenPart(relationships, references);
        using var reader = CreateReader(stream);
        var visitedParts = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Depth > 256)
                throw new InvalidDataException("DOCX relationships nesting is too deep.");
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            if (reader.Depth == 0 && (reader.LocalName != "Relationships" || reader.NamespaceURI != PackageRelationshipNamespace))
                throw new InvalidDataException("The DOCX relationships root is invalid.");
            if (reader.Depth != 1 || reader.LocalName != "Relationship" || reader.NamespaceURI != PackageRelationshipNamespace
                || reader.GetAttribute("TargetMode") is not (null or "Internal"))
                continue;

            var type = reader.GetAttribute("Type") ?? "";
            var kind = type.StartsWith(RelationshipNamespace + "/", StringComparison.Ordinal)
                ? type[(RelationshipNamespace.Length + 1)..]
                : type.StartsWith(StrictRelationshipNamespace + "/", StringComparison.Ordinal)
                    ? type[(StrictRelationshipNamespace.Length + 1)..] : "";
            var id = reader.GetAttribute("Id") ?? "";
            var rootName = kind switch
            {
                "header" when references.Headers.Contains(id) => "hdr",
                "footer" when references.Footers.Contains(id) => "ftr",
                "footnotes" when references.Footnotes.Count > 0 => "footnotes",
                "endnotes" when references.Endnotes.Count > 0 => "endnotes",
                _ => null
            };
            if (rootName is null)
                continue;

            var target = reader.GetAttribute("Target");
            if (string.IsNullOrEmpty(target) || !Uri.TryCreate(target, UriKind.Relative, out var relative))
                continue;
            // Resolve package-relative names only. The URI is never opened or requested.
            var packageBase = new Uri("https://recall.invalid/word/document.xml");
            if (!Uri.TryCreate(packageBase, relative, out var resolved)
                || resolved.Authority != packageBase.Authority || resolved.Scheme != packageBase.Scheme
                || resolved.Query.Length > 0 || resolved.Fragment.Length > 0)
                continue;
            var path = Uri.UnescapeDataString(resolved.AbsolutePath[1..]);
            if (visitedParts.Add(path))
                yield return (path, rootName);
        }
    }

    private void ReadPartText(Stream stream, string rootName, StringBuilder content,
        DocumentReferences references, CancellationToken cancellationToken)
    {
        using var reader = CreateReader(stream);
        var buffer = new char[4096];
        var bodyDepth = -1;
        var textDepth = -1;
        var skipDepth = -1;
        var bodyFound = rootName != "document";
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
                if (reader.LocalName != rootName || reader.NamespaceURI is not (WordNamespace or StrictWordNamespace))
                    throw new InvalidDataException("The DOCX document root is invalid.");
                documentNamespace = reader.NamespaceURI;
                if (rootName is "hdr" or "ftr")
                    bodyDepth = reader.IsEmptyElement ? -1 : reader.Depth;
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
                    if (rootName == "document" && reader.LocalName == "body" && reader.Depth == 1)
                    {
                        bodyFound = true;
                        bodyDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                    }
                    else if (rootName is "footnotes" or "endnotes" && reader.Depth == 1)
                    {
                        var noteName = rootName == "footnotes" ? "footnote" : "endnote";
                        var noteIds = rootName == "footnotes" ? references.Footnotes : references.Endnotes;
                        if (reader.LocalName == noteName && noteIds.Contains(reader.GetAttribute("id", documentNamespace) ?? "")
                            && reader.GetAttribute("type", documentNamespace) is null or "normal")
                            bodyDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                        else if (!reader.IsEmptyElement)
                            skipDepth = reader.Depth;
                    }
                    else if (bodyDepth >= 0)
                    {
                        switch (reader.LocalName)
                        {
                            case "del" or "moveFrom" or "sectPrChange" when !reader.IsEmptyElement:
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
                        if (rootName == "document")
                        {
                            var referenceId = reader.GetAttribute("id", RelationshipNamespace)
                                ?? reader.GetAttribute("id", StrictRelationshipNamespace);
                            switch (reader.LocalName)
                            {
                                case "headerReference" when referenceId is not null:
                                    references.Headers.Add(referenceId);
                                    break;
                                case "footerReference" when referenceId is not null:
                                    references.Footers.Add(referenceId);
                                    break;
                                case "footnoteReference":
                                    references.Footnotes.Add(reader.GetAttribute("id", documentNamespace) ?? "");
                                    break;
                                case "endnoteReference":
                                    references.Endnotes.Add(reader.GetAttribute("id", documentNamespace) ?? "");
                                    break;
                            }
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

        void Append(string value)
        {
            if (content.Length + value.Length > maximumTextCharacters)
                throw new TextLimitExceededException();
            content.Append(value);
        }
    }

    private sealed class TextLimitExceededException : Exception;

    private sealed class DocumentReferences
    {
        public HashSet<string> Headers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Footers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Footnotes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Endnotes { get; } = new(StringComparer.Ordinal);
        public long XmlBytesRead { get; set; }
    }
}
