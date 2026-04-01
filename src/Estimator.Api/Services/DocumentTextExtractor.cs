using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
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

            if (string.IsNullOrWhiteSpace(extractedText))
            {
                return DocumentExtractionResult.Failure(
                    "Could not extract readable text from this file. Please upload a text-readable document.");
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
            foreach (var paragraph in body.Elements<Paragraph>())
            {
                var text = paragraph.InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.AppendLine(text);
                }
            }

            if (builder.Length == 0)
            {
                return body.InnerText ?? string.Empty;
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
    }

    public sealed record DocumentExtractionResult(bool IsSuccess, string Text, string? Error, string? FileExtension)
    {
        public static DocumentExtractionResult Success(string text, string fileExtension) =>
            new(true, text, null, fileExtension);

        public static DocumentExtractionResult Failure(string error) =>
            new(false, string.Empty, error, null);
    }
}
