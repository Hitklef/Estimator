using System.Text;
using System.Text.RegularExpressions;
using Estimator.Core.Models.Options;
using Microsoft.Extensions.Options;

namespace Estimator.Core.Services
{
    public interface IProjectBriefPreprocessor
    {
        PreparedProjectBrief Prepare(string rawDescription);
    }

    public sealed record PreparedProjectBrief(
        string ModelInput,
        string ProjectSummary,
        bool WasCondensed,
        int OriginalCharacterCount,
        int PreparedCharacterCount);

    public sealed class ProjectBriefPreprocessor : IProjectBriefPreprocessor
    {
        private static readonly Regex MultiWhitespaceRegex = new("\\s+", RegexOptions.Compiled);
        private static readonly Regex UserStoryRegex = new(
            "^(?<id>US[\\d\\.]+)\\s*[:\\.]?\\s*(?:As an? [^,]+,\\s*)?I want (?<want>.+?)(?:,\\s*so that.*| so that.*|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] ImportantKeywords =
        [
            "windows", "desktop", "usb", "i2c", "eeprom", "transceiver", "coding box", "sfp", "sfp+", "qsfp", "osfp",
            "auth", "login", "offline", "sync", "cloud", "database", "website", "history", "audit", "queue", "checksum",
            "hex", "ascii", "vendor", "read all", "write", "verify", "dom", "ddm", "tunable", "power on", "manual tab"
        ];

        private readonly int _maxCharacters;
        private readonly int _maxUserStories;
        private readonly int _maxRequirementLines;
        private readonly int _repeatedLineThreshold;

        public ProjectBriefPreprocessor(IOptions<AiSettings> options)
        {
            var settings = options.Value;
            _maxCharacters = Math.Max(4000, settings.ProjectBriefMaxCharacters);
            _maxUserStories = Math.Max(8, settings.ProjectBriefMaxUserStories);
            _maxRequirementLines = Math.Max(12, settings.ProjectBriefMaxRequirementLines);
            _repeatedLineThreshold = Math.Max(3, settings.DocumentRepeatedLineThreshold);
        }

        public PreparedProjectBrief Prepare(string rawDescription)
        {
            var normalized = NormalizeText(rawDescription);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return new PreparedProjectBrief(string.Empty, string.Empty, false, 0, 0);
            }

            var compactLines = CompactLines(normalized);
            var compactText = string.Join('\n', compactLines).Trim();
            if (compactText.Length <= _maxCharacters)
            {
                return new PreparedProjectBrief(
                    compactText,
                    compactText,
                    compactText.Length < normalized.Length,
                    normalized.Length,
                    compactText.Length);
            }

            var prioritizedBrief = BuildPrioritizedBrief(compactLines);
            return new PreparedProjectBrief(
                prioritizedBrief,
                prioritizedBrief,
                true,
                normalized.Length,
                prioritizedBrief.Length);
        }

        private List<string> CompactLines(string normalized)
        {
            var rawLines = normalized.Split('\n', StringSplitOptions.None);
            var compact = new List<string>(rawLines.Length);

            foreach (var rawLine in rawLines)
            {
                var line = NormalizeLine(rawLine);
                if (string.IsNullOrWhiteSpace(line) || IsDecorative(line))
                {
                    continue;
                }

                if (TryCompactUserStory(line, out var compactUserStory))
                {
                    compact.Add(compactUserStory);
                    continue;
                }

                if (IsLowSignalDetail(line) && !IsImportant(line))
                {
                    continue;
                }

                compact.Add(StripBulletPrefix(line));
            }

            return RemoveRepeatedLines(compact, _repeatedLineThreshold);
        }

        private string BuildPrioritizedBrief(IReadOnlyCollection<string> compactLines)
        {
            var titles = compactLines.Where(IsTitleLike).Take(3).ToList();
            var constraints = compactLines.Where(IsConstraintLike).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
            var requirements = compactLines.Where(IsRequirementLike).Distinct(StringComparer.OrdinalIgnoreCase).Take(_maxRequirementLines).ToList();
            var userStories = compactLines.Where(IsCompactUserStory).Distinct(StringComparer.OrdinalIgnoreCase).Take(_maxUserStories).ToList();
            var acceptance = compactLines.Where(IsAcceptanceLike).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
            var supporting = compactLines
                .Where(line => IsImportant(line) && !titles.Contains(line, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(16)
                .ToList();

            var builder = new StringBuilder(_maxCharacters + 256);
            AppendSection(builder, "Project Brief", titles, required: true);
            AppendSection(builder, "Key Constraints", constraints);
            AppendSection(builder, "Core Requirements", requirements);
            AppendSection(builder, "Key User Stories", userStories);
            AppendSection(builder, "Acceptance Criteria", acceptance);
            AppendSection(builder, "Additional Details", supporting);

            if (builder.Length < _maxCharacters)
            {
                var captured = new HashSet<string>(
                    titles.Concat(constraints).Concat(requirements).Concat(userStories).Concat(acceptance).Concat(supporting),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var line in compactLines)
                {
                    if (captured.Contains(line))
                    {
                        continue;
                    }

                    if (!TryAppendLine(builder, $"- {line}"))
                    {
                        break;
                    }
                }
            }

            return builder.ToString().Trim();
        }

        private void AppendSection(StringBuilder builder, string title, IReadOnlyCollection<string> lines, bool required = false)
        {
            if (lines.Count == 0)
            {
                return;
            }

            if (builder.Length > 0 && !TryAppendLine(builder, string.Empty))
            {
                return;
            }

            if (!TryAppendLine(builder, $"{title}:"))
            {
                return;
            }

            foreach (var line in lines)
            {
                if (!TryAppendLine(builder, $"- {line}"))
                {
                    return;
                }
            }

            if (required && builder.Length == 0)
            {
                TryAppendLine(builder, $"{title}:");
            }
        }

        private bool TryAppendLine(StringBuilder builder, string line)
        {
            var value = string.IsNullOrEmpty(line) ? Environment.NewLine : line + Environment.NewLine;
            if (builder.Length + value.Length > _maxCharacters)
            {
                return false;
            }

            builder.Append(value);
            return true;
        }

        private static List<string> RemoveRepeatedLines(IEnumerable<string> sourceLines, int repeatedLineThreshold)
        {
            var lines = sourceLines.ToList();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in lines)
            {
                counts[line] = counts.TryGetValue(line, out var count) ? count + 1 : 1;
            }

            var cleaned = new List<string>(lines.Count);
            string? previous = null;
            foreach (var line in lines)
            {
                if (counts.TryGetValue(line, out var count) &&
                    count >= repeatedLineThreshold &&
                    line.Length <= 180)
                {
                    continue;
                }

                if (previous is not null &&
                    previous.Equals(line, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                cleaned.Add(line);
                previous = line;
            }

            return cleaned;
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var normalized = text
                .Replace("\0", string.Empty, StringComparison.Ordinal)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();

            while (normalized.Contains("\n\n\n", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
            }

            return normalized;
        }

        private static string NormalizeLine(string line)
        {
            var normalized = MultiWhitespaceRegex.Replace(line.Trim(), " ");
            return normalized.Trim();
        }

        private static bool TryCompactUserStory(string line, out string compactUserStory)
        {
            compactUserStory = string.Empty;
            var match = UserStoryRegex.Match(line);
            if (!match.Success)
            {
                return false;
            }

            var id = match.Groups["id"].Value.Trim();
            var want = match.Groups["want"].Value.Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(want))
            {
                return false;
            }

            compactUserStory = $"{id}: {want}.";
            return true;
        }

        private static string StripBulletPrefix(string line) =>
            line.TrimStart('-', '*', '●', '▪', 'o').Trim();

        private static bool IsDecorative(string line) =>
            line.All(character => character is '-' or '_' or '*' or '.' or ' ');

        private static bool IsLowSignalDetail(string line) =>
            line.StartsWith("Click on ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Display below", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Display both", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Expanded Menu", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Collapsed Menu", StringComparison.OrdinalIgnoreCase);

        private static bool IsTitleLike(string line) =>
            line.Length <= 120 &&
            !line.Contains(':', StringComparison.Ordinal) &&
            !line.StartsWith("US", StringComparison.OrdinalIgnoreCase) &&
            !line.StartsWith("As ", StringComparison.OrdinalIgnoreCase) &&
            !line.StartsWith("o ", StringComparison.OrdinalIgnoreCase) &&
            !line.StartsWith("●", StringComparison.OrdinalIgnoreCase);

        private static bool IsConstraintLike(string line)
        {
            var lowered = line.ToLowerInvariant();
            return lowered.Contains("windows", StringComparison.Ordinal) ||
                   lowered.Contains("usb", StringComparison.Ordinal) ||
                   lowered.Contains("i2c", StringComparison.Ordinal) ||
                   lowered.Contains("offline", StringComparison.Ordinal) ||
                   lowered.Contains("cloud", StringComparison.Ordinal) ||
                   lowered.Contains("database", StringComparison.Ordinal) ||
                   lowered.Contains("website", StringComparison.Ordinal) ||
                   lowered.Contains("network drive", StringComparison.Ordinal) ||
                   lowered.Contains("phase 1", StringComparison.Ordinal) ||
                   lowered.Contains("single type of coding box", StringComparison.Ordinal);
        }

        private static bool IsRequirementLike(string line)
        {
            if (IsCompactUserStory(line))
            {
                return false;
            }

            var lowered = line.ToLowerInvariant();
            return lowered.Contains("should", StringComparison.Ordinal) ||
                   lowered.Contains("must", StringComparison.Ordinal) ||
                   lowered.Contains("allow", StringComparison.Ordinal) ||
                   lowered.Contains("support", StringComparison.Ordinal) ||
                   lowered.Contains("read", StringComparison.Ordinal) ||
                   lowered.Contains("write", StringComparison.Ordinal) ||
                   lowered.Contains("verify", StringComparison.Ordinal) ||
                   lowered.Contains("track", StringComparison.Ordinal) ||
                   lowered.Contains("authenticate", StringComparison.Ordinal) ||
                   lowered.Contains("sync", StringComparison.Ordinal) ||
                   lowered.Contains("display", StringComparison.Ordinal) ||
                   lowered.Contains("editable", StringComparison.Ordinal) ||
                   lowered.Contains("read-only", StringComparison.Ordinal);
        }

        private static bool IsAcceptanceLike(string line) =>
            line.StartsWith("Acceptance Criteria", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(line, "^\\d+\\.\\s", RegexOptions.CultureInvariant);

        private static bool IsCompactUserStory(string line) =>
            line.StartsWith("US", StringComparison.OrdinalIgnoreCase) &&
            line.Contains(':', StringComparison.Ordinal);

        private static bool IsImportant(string line) =>
            ImportantKeywords.Any(keyword => line.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
