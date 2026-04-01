using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Estimator.Core.Models.Options;
using Microsoft.Extensions.Options;
using NPOI.HWPF;
using NPOI.HWPF.Extractor;
using UglyToad.PdfPig;

namespace Estimator.Api.Services
{
    public interface IDocumentTextExtractor
    {
        Task<DocumentExtractionResult> ExtractTextAsync(IFormFile file, CancellationToken cancellationToken = default);
    }

    public sealed class DocumentTextExtractor : IDocumentTextExtractor
    {
        private const long MaxFileSizeBytes = 25 * 1024 * 1024;
        private static readonly Encoding Utf8Encoding = new UTF8Encoding(false, true);
        private static readonly Encoding Utf16LeEncoding = new UnicodeEncoding(false, true, true);
        private static readonly Encoding Utf16BeEncoding = new UnicodeEncoding(true, true, true);
        private readonly int _maxExtractedCharacters;
        private readonly int _repeatedLineThreshold;
        private static readonly HashSet<string> StructuredDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf",
            ".doc",
            ".docx",
            ".xlsx",
            ".pptx"
        };

        public DocumentTextExtractor(IOptions<AiSettings> options)
        {
            _maxExtractedCharacters = Math.Max(4000, options.Value.DocumentMaxExtractedCharacters);
            _repeatedLineThreshold = Math.Max(3, options.Value.DocumentRepeatedLineThreshold);
        }

        public async Task<DocumentExtractionResult> ExtractTextAsync(IFormFile file, CancellationToken cancellationToken = default)
        {
            if (file is null || file.Length == 0)
            {
                return DocumentExtractionResult.Failure("Uploaded file is empty.");
            }

            if (file.Length > MaxFileSizeBytes)
            {
                return DocumentExtractionResult.Failure("File is too large. Maximum supported size is 25 MB.");
            }

            var extension = Path.GetExtension(file.FileName) ?? string.Empty;

            await using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream, cancellationToken);
            memoryStream.Position = 0;

            string extractedText;
            try
            {
                extractedText = ExtractTextByType(memoryStream, extension);
            }
            catch (Exception ex)
            {
                return DocumentExtractionResult.Failure($"Failed to parse document: {ex.Message}");
            }

            extractedText = NormalizeText(extractedText);
            extractedText = RemoveRepeatedNoise(extractedText, _repeatedLineThreshold);
            extractedText = LimitCharacters(extractedText, _maxExtractedCharacters);
            if (string.IsNullOrWhiteSpace(extractedText))
            {
                return DocumentExtractionResult.Failure(
                    "The uploaded file did not contain directly readable text. Text documents, Office files, PDFs, spreadsheets, slides, logs, and source files are supported. Images, executables, and archives are not OCR-parsed.");
            }

            return DocumentExtractionResult.Success(extractedText, extension);
        }

        private static string ExtractTextByType(Stream stream, string extension)
        {
            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractPdf(stream);
            }

            if (extension.Equals(".doc", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractDoc(stream);
            }

            if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractDocx(stream);
            }

            if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractXlsx(stream);
            }

            if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractPptx(stream);
            }

            return ExtractBestEffortText(stream);
        }

        private static string ExtractPdf(Stream stream)
        {
            var builder = new StringBuilder();
            using var document = PdfDocument.Open(stream);
            foreach (var page in document.GetPages())
            {
                builder.AppendLine(page.Text);
            }

            return builder.ToString();
        }

        private static string ExtractDoc(Stream stream)
        {
            var document = new HWPFDocument(stream);
            var extractor = new WordExtractor(document);
            return extractor.Text;
        }

        private static string ExtractDocx(Stream stream)
        {
            stream.Position = 0;

            using var document = WordprocessingDocument.Open(stream, false);
            var body = document.MainDocumentPart?.Document?.Body;
            if (body is null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var element in body.Elements())
            {
                AppendDocxElementText(builder, element);
            }

            return builder.ToString();
        }

        private static void AppendDocxElementText(StringBuilder builder, OpenXmlElement element)
        {
            if (element is Paragraph paragraph)
            {
                var text = paragraph.InnerText?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.AppendLine(text);
                }

                return;
            }

            if (element is not DocumentFormat.OpenXml.Wordprocessing.Table table)
            {
                return;
            }

            foreach (var row in table.Elements<TableRow>())
            {
                var cells = row.Elements<TableCell>()
                    .Select(cell => cell.InnerText?.Trim())
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();

                if (cells.Count > 0)
                {
                    builder.AppendLine(string.Join(" | ", cells));
                }
            }
        }

        private static string ExtractXlsx(Stream stream)
        {
            stream.Position = 0;

            using var document = SpreadsheetDocument.Open(stream, false);
            var workbookPart = document.WorkbookPart;
            if (workbookPart?.Workbook is null)
            {
                return string.Empty;
            }

            var sharedStringTable = workbookPart.SharedStringTablePart?.SharedStringTable;
            var builder = new StringBuilder();

            foreach (var sheet in workbookPart.Workbook.Sheets?.Elements<Sheet>() ?? Enumerable.Empty<Sheet>())
            {
                builder.AppendLine($"Sheet: {sheet.Name}");

                var worksheetPart = workbookPart.GetPartById(sheet.Id!) as WorksheetPart;
                var rows = worksheetPart?.Worksheet?.Descendants<Row>() ?? Enumerable.Empty<Row>();
                foreach (var row in rows)
                {
                    var values = row.Elements<Cell>()
                        .Select(cell => ResolveSpreadsheetCellText(cell, sharedStringTable))
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .ToList();

                    if (values.Count > 0)
                    {
                        builder.AppendLine(string.Join(" | ", values));
                    }
                }

                builder.AppendLine();
            }

            return builder.ToString();
        }

        private static string ResolveSpreadsheetCellText(Cell cell, SharedStringTable? sharedStringTable)
        {
            var rawValue = cell.CellValue?.Text ?? cell.InnerText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return string.Empty;
            }

            if (cell.DataType?.Value == CellValues.SharedString &&
                int.TryParse(rawValue, out var sharedStringIndex) &&
                sharedStringTable is not null &&
                sharedStringIndex >= 0 &&
                sharedStringIndex < sharedStringTable.Count())
            {
                return sharedStringTable.ElementAt(sharedStringIndex).InnerText?.Trim() ?? string.Empty;
            }

            return rawValue.Trim();
        }

        private static string ExtractPptx(Stream stream)
        {
            stream.Position = 0;

            using var document = PresentationDocument.Open(stream, false);
            var presentationPart = document.PresentationPart;
            if (presentationPart?.Presentation is null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var slideIndex = 1;

            foreach (var slideId in presentationPart.Presentation.SlideIdList?.Elements<SlideId>() ?? Enumerable.Empty<SlideId>())
            {
                var slidePart = presentationPart.GetPartById(slideId.RelationshipId!) as SlidePart;
                var texts = slidePart?.Slide?.Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                    .Select(text => text.Text?.Trim())
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();

                if (texts is not { Count: > 0 })
                {
                    slideIndex++;
                    continue;
                }

                builder.AppendLine($"Slide {slideIndex}:");
                foreach (var text in texts)
                {
                    builder.AppendLine(text);
                }

                builder.AppendLine();
                slideIndex++;
            }

            return builder.ToString();
        }

        private static string ExtractBestEffortText(Stream stream)
        {
            stream.Position = 0;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var bytes = copy.ToArray();

            if (bytes.Length == 0)
            {
                return string.Empty;
            }

            if (LooksBinary(bytes))
            {
                return string.Empty;
            }

            foreach (var encoding in GetCandidateEncodings(bytes))
            {
                try
                {
                    var preambleLength = GetPreambleLength(bytes, encoding);
                    return encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
                }
                catch (DecoderFallbackException)
                {
                }
            }

            return Encoding.UTF8.GetString(bytes);
        }

        private static IEnumerable<Encoding> GetCandidateEncodings(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                yield return Utf8Encoding;
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                yield return Utf16LeEncoding;
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                yield return Utf16BeEncoding;
            }

            yield return Utf8Encoding;
            yield return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }

        private static int GetPreambleLength(byte[] bytes, Encoding encoding)
        {
            var preamble = encoding.GetPreamble();
            return preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble)
                ? preamble.Length
                : 0;
        }

        private static bool LooksBinary(byte[] bytes)
        {
            var sampleLength = Math.Min(bytes.Length, 4096);
            var controlCharacters = 0;
            var suspiciousZeros = 0;

            for (var index = 0; index < sampleLength; index++)
            {
                var value = bytes[index];
                if (value == 0)
                {
                    suspiciousZeros++;
                    continue;
                }

                var isAllowedControl = value is 9 or 10 or 13;
                var isPrintableAscii = value is >= 32 and <= 126;
                var isExtendedText = value >= 128;

                if (!isAllowedControl && !isPrintableAscii && !isExtendedText)
                {
                    controlCharacters++;
                }
            }

            return suspiciousZeros > sampleLength / 100 ||
                   controlCharacters > sampleLength / 12;
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var normalized = text
                .Replace("\0", string.Empty, StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal)
                .Trim();

            while (normalized.Contains("\n\n\n", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
            }

            return normalized;
        }

        private static string RemoveRepeatedNoise(string text, int repeatedLineThreshold)
        {
            var lines = text.Split('\n', StringSplitOptions.None)
                .Select(line => line.Trim())
                .ToList();

            var lineCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                lineCounts[line] = lineCounts.TryGetValue(line, out var count) ? count + 1 : 1;
            }

            var builder = new StringBuilder(text.Length);
            string? previous = null;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (!string.IsNullOrWhiteSpace(previous))
                    {
                        builder.AppendLine();
                    }

                    previous = line;
                    continue;
                }

                var isOverRepeatedShortLine =
                    line.Length <= 160 &&
                    lineCounts.TryGetValue(line, out var count) &&
                    count >= repeatedLineThreshold;

                var isConsecutiveDuplicate = previous is not null &&
                                             previous.Equals(line, StringComparison.Ordinal);

                if (isOverRepeatedShortLine || isConsecutiveDuplicate)
                {
                    continue;
                }

                builder.AppendLine(line);
                previous = line;
            }

            return builder.ToString().Trim();
        }

        private static string LimitCharacters(string text, int maxCharacters)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length <= maxCharacters)
            {
                return text;
            }

            const string marker = "\n\n[... document content truncated to fit model context ...]\n\n";
            if (maxCharacters <= marker.Length + 200)
            {
                return text[..maxCharacters];
            }

            var headLength = (int)(maxCharacters * 0.75);
            var tailLength = maxCharacters - headLength - marker.Length;
            tailLength = Math.Max(50, tailLength);

            return string.Concat(
                text.AsSpan(0, Math.Min(headLength, text.Length)),
                marker,
                text.AsSpan(Math.Max(0, text.Length - tailLength), Math.Min(tailLength, text.Length)));
        }
    }

    public sealed record DocumentExtractionResult(bool IsSuccess, string Text, string? Error, string? FileExtension)
    {
        public static DocumentExtractionResult Success(string text, string fileExtension) =>
            new(true, text, null, fileExtension);

        public static DocumentExtractionResult Failure(string error) =>
            new(false, string.Empty, error, null);
    }
}
