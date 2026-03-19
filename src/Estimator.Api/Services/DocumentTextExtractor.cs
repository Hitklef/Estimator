using System.Text;
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
        private readonly int _maxExtractedCharacters;
        private readonly int _repeatedLineThreshold;
        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf",
            ".doc"
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

            var extension = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(extension) || !SupportedExtensions.Contains(extension))
            {
                return DocumentExtractionResult.Failure("Unsupported file type. Only .doc and .pdf are supported.");
            }

            await using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream, cancellationToken);
            memoryStream.Position = 0;

            string extractedText;
            try
            {
                extractedText = extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                    ? ExtractPdf(memoryStream)
                    : ExtractDoc(memoryStream);
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
                return DocumentExtractionResult.Failure("The uploaded file was parsed, but no readable text was found.");
            }

            return DocumentExtractionResult.Success(extractedText, extension);
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
