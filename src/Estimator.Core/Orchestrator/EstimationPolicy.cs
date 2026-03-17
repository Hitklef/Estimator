using Estimator.Core.Models;
using Estimator.Core.Models.Options;
using Microsoft.Extensions.Options;

namespace Estimator.Core.Orchestrator
{
    public interface IEstimationPolicy
    {
        BenchmarkContext ResolveBenchmarkContext(string projectDescription);
        PolicyDecision EvaluateRoadmap(IReadOnlyCollection<ProjectTask> tasks, string projectDescription);
        PolicyNormalizationResult Normalize(IReadOnlyCollection<ProjectTask> tasks, string projectDescription);
        PolicyDecision EvaluateEstimatedPlan(IReadOnlyCollection<ProjectTask> tasks, string projectDescription);
    }

    public sealed class EstimationPolicy : IEstimationPolicy
    {
        private static readonly string[] MicroTaskKeywords =
        [
            "typo", "copy update", "text tweak", "rename", "minor style", "simple config", "metadata", "lint", "small refactor"
        ];

        private static readonly string[] MediumComplexityKeywords =
        [
            "api", "endpoint", "workflow", "screen", "frontend", "backend", "database", "service", "validation"
        ];

        private static readonly string[] HighComplexityKeywords =
        [
            "oauth", "authentication", "authorization", "security", "privacy", "anonym", "integration", "migration",
            "sentiment", "nlp", "animation", "state machine", "event", "queue", "background job", "community"
        ];

        private static readonly string[] CriticalComplexityKeywords =
        [
            "compliance", "encryption", "safety", "moderation", "trauma", "zero downtime", "multi-tenant", "hipaa",
            "high availability", "offline sync", "recovery"
        ];

        private readonly AiSettings _settings;
        private readonly EstimationPolicySettings _policy;

        public EstimationPolicy(IOptions<AiSettings> options)
        {
            _settings = options.Value;
            _policy = _settings.EstimationPolicy;
        }

        public BenchmarkContext ResolveBenchmarkContext(string projectDescription)
        {
            var profile = _settings.ResolveBenchmarkProfile(projectDescription);
            return new BenchmarkContext
            {
                ProfileName = profile.Name,
                MinimumTotalHours = profile.MinimumTotalHours,
                MaximumTotalHours = profile.MaximumTotalHours,
                MinimumTaskCount = profile.MinimumTaskCount,
                Workstreams = profile.Workstreams
                    .Select(workstream => new BenchmarkWorkstreamContext
                    {
                        Key = workstream.Key,
                        Name = workstream.Name,
                        MinHours = workstream.MinHours,
                        MaxHours = workstream.MaxHours,
                        Keywords = workstream.Keywords.ToList()
                    })
                    .ToList()
            };
        }

        public PolicyDecision EvaluateRoadmap(IReadOnlyCollection<ProjectTask> tasks, string projectDescription)
        {
            var benchmark = ResolveBenchmarkContext(projectDescription);
            var normalized = CleanupTasks(tasks);
            var summaries = BuildCategoryBreakdown(normalized, benchmark);

            if (normalized.Count < benchmark.MinimumTaskCount)
            {
                return Rejected(
                    benchmark,
                    summaries,
                    "Decomposer",
                    $"Roadmap is too shallow ({normalized.Count} tasks). Minimum expected is {benchmark.MinimumTaskCount} tasks.");
            }

            var missingRequired = summaries
                .Where(summary => summary.MinimumExpectedHours > 0 && summary.TaskCount == 0)
                .Select(summary => summary.CategoryName)
                .ToList();

            if (missingRequired.Count > 0)
            {
                return Rejected(
                    benchmark,
                    summaries,
                    "Decomposer",
                    $"Missing required workstreams: {string.Join(", ", missingRequired)}.");
            }

            var underDetailed = summaries
                .Where(summary => summary.MinimumExpectedHours > 0 && summary.TaskCount < _policy.MinimumTasksPerWorkstream)
                .Select(summary => summary.CategoryName)
                .ToList();

            if (underDetailed.Count > 0)
            {
                return Rejected(
                    benchmark,
                    summaries,
                    "Decomposer",
                    $"Roadmap granularity is too low in: {string.Join(", ", underDetailed)}. Add concrete implementation tasks.");
            }

            return Accepted(benchmark, summaries, "Roadmap coverage meets required production workstreams.");
        }

        public PolicyNormalizationResult Normalize(IReadOnlyCollection<ProjectTask> tasks, string projectDescription)
        {
            var benchmark = ResolveBenchmarkContext(projectDescription);
            var normalized = CleanupTasks(tasks);
            var notes = new List<string>();

            if (normalized.Count == 0)
            {
                return new PolicyNormalizationResult
                {
                    Tasks = normalized,
                    CategoryBreakdown = BuildCategoryBreakdown(normalized, benchmark),
                    Benchmark = benchmark,
                    Notes = ["No tasks available for normalization."]
                };
            }

            foreach (var task in normalized)
            {
                task.EstimatedHours = NormalizeTaskHours(task);
            }

            CalibrateWorkstreamMinimums(normalized, benchmark, notes);
            EnforceQualityOverhead(normalized, benchmark, notes);
            EnforceTotalMinimum(normalized, benchmark, notes);

            var summaries = BuildCategoryBreakdown(normalized, benchmark);
            return new PolicyNormalizationResult
            {
                Tasks = normalized.OrderBy(task => task.Id).ToList(),
                CategoryBreakdown = summaries,
                Benchmark = benchmark,
                Notes = notes
            };
        }

        public PolicyDecision EvaluateEstimatedPlan(IReadOnlyCollection<ProjectTask> tasks, string projectDescription)
        {
            var benchmark = ResolveBenchmarkContext(projectDescription);
            var normalized = CleanupTasks(tasks);
            var summaries = BuildCategoryBreakdown(normalized, benchmark);

            if (normalized.Count == 0)
            {
                return Rejected(benchmark, summaries, "Estimator", "No tasks were produced.");
            }

            foreach (var task in normalized)
            {
                var hours = task.EstimatedHours ?? 0;
                if (hours <= 0)
                {
                    return Rejected(benchmark, summaries, "Estimator", $"Task '{task.Title}' has an invalid estimate.");
                }

                if (!IsMicroTask(task) && !IsMultipleOf4(hours))
                {
                    return Rejected(benchmark, summaries, "Estimator", $"Task '{task.Title}' must use 4-hour increments.");
                }
            }

            if (normalized.Count < benchmark.MinimumTaskCount)
            {
                return Rejected(
                    benchmark,
                    summaries,
                    "Estimator",
                    $"Roadmap has only {normalized.Count} tasks. Minimum expected is {benchmark.MinimumTaskCount}.");
            }

            var missingRequired = summaries
                .Where(summary => summary.MinimumExpectedHours > 0 && summary.TaskCount == 0)
                .Select(summary => summary.CategoryName)
                .ToList();

            if (missingRequired.Count > 0)
            {
                return Rejected(
                    benchmark,
                    summaries,
                    "Estimator",
                    $"Missing required workstreams after estimation: {string.Join(", ", missingRequired)}.");
            }

            var underMinRanges = summaries
                .Where(summary => summary.MinimumExpectedHours > 0 && summary.Hours < summary.MinimumExpectedHours)
                .Select(summary => $"{summary.CategoryName} ({summary.Hours}h < {summary.MinimumExpectedHours}h)")
                .ToList();

            if (underMinRanges.Count > 0)
            {
                return Rejected(
                    benchmark,
                    summaries,
                    "Estimator",
                    $"Category estimates are below production baseline: {string.Join("; ", underMinRanges)}.");
            }

            var total = (int)Math.Round(normalized.Sum(task => task.EstimatedHours ?? 0), MidpointRounding.AwayFromZero);
            if (total < benchmark.MinimumTotalHours)
            {
                return Rejected(
                    benchmark,
                    summaries,
                    "Estimator",
                    $"Total estimate {total}h is below benchmark minimum {benchmark.MinimumTotalHours}h.");
            }

            var qualitySummary = summaries.FirstOrDefault(summary =>
                summary.CategoryKey.Equals("QualityTestingPmUx", StringComparison.OrdinalIgnoreCase));
            if (qualitySummary is not null)
            {
                var nonQualityHours = Math.Max(0, total - qualitySummary.Hours);
                var qualityFloor = RoundUpTo4(nonQualityHours * ResolveQualityOverheadRatio(benchmark));
                if (qualitySummary.Hours < qualityFloor)
                {
                    return Rejected(
                        benchmark,
                        summaries,
                        "Estimator",
                        $"QA/testing/PM-UX effort is too low ({qualitySummary.Hours}h). Minimum expected is {qualityFloor}h.");
                }
            }

            return Accepted(benchmark, summaries, "Estimated plan passed deterministic production checks.");
        }

        private static List<ProjectTask> CleanupTasks(IReadOnlyCollection<ProjectTask> tasks) =>
            tasks
                .Where(task => !string.IsNullOrWhiteSpace(task.Title))
                .Select(CloneTask)
                .ToList();

        private static ProjectTask CloneTask(ProjectTask task) =>
            new()
            {
                Id = task.Id,
                Title = task.Title,
                Description = task.Description,
                TechStack = task.TechStack.ToList(),
                EstimatedHours = task.EstimatedHours
            };

        private double NormalizeTaskHours(ProjectTask task)
        {
            var current = task.EstimatedHours ?? 0;
            if (IsMicroTask(task))
            {
                if (current <= 1)
                {
                    return 1;
                }

                if (current <= 2)
                {
                    return 2;
                }
            }

            var score = ComputeComplexityScore(task);
            var floor = score switch
            {
                <= 1 => _policy.MinimumHoursPerStandardTask,
                <= 3 => _policy.MediumComplexityTaskFloorHours,
                <= 5 => _policy.HighComplexityTaskFloorHours,
                _ => _policy.CriticalComplexityTaskFloorHours
            };

            var adjusted = Math.Max(current, floor);
            return RoundUpTo4(adjusted);
        }

        private void CalibrateWorkstreamMinimums(
            List<ProjectTask> tasks,
            BenchmarkContext benchmark,
            List<string> notes)
        {
            foreach (var workstream in benchmark.Workstreams.Where(stream => stream.MinHours > 0))
            {
                var streamTasks = tasks
                    .Where(task => ResolveWorkstreamKey(task, benchmark).Equals(workstream.Key, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (streamTasks.Count == 0)
                {
                    continue;
                }

                var currentHours = streamTasks.Sum(task => task.EstimatedHours ?? 0);
                var targetHours = workstream.MinHours;
                if (currentHours >= targetHours)
                {
                    continue;
                }

                IncreaseTaskHours(streamTasks, targetHours - currentHours);
                notes.Add($"Raised '{workstream.Name}' from {currentHours:F0}h to meet minimum {targetHours}h.");
            }
        }

        private void EnforceQualityOverhead(List<ProjectTask> tasks, BenchmarkContext benchmark, List<string> notes)
        {
            var qualityStream = benchmark.Workstreams.FirstOrDefault(stream =>
                stream.Key.Equals("QualityTestingPmUx", StringComparison.OrdinalIgnoreCase));
            if (qualityStream is null)
            {
                return;
            }

            var qualityTasks = tasks
                .Where(task => ResolveWorkstreamKey(task, benchmark).Equals(qualityStream.Key, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (qualityTasks.Count == 0)
            {
                return;
            }

            var totalHours = tasks.Sum(task => task.EstimatedHours ?? 0);
            var qualityHours = qualityTasks.Sum(task => task.EstimatedHours ?? 0);
            var nonQualityHours = Math.Max(0, totalHours - qualityHours);
            var requiredFromRatio = RoundUpTo4(nonQualityHours * ResolveQualityOverheadRatio(benchmark));
            var target = Math.Max(qualityStream.MinHours, requiredFromRatio);

            if (qualityHours >= target)
            {
                return;
            }

            IncreaseTaskHours(qualityTasks, target - qualityHours);
            notes.Add($"Raised QA/testing/PM-UX from {qualityHours:F0}h to {target}h.");
        }

        private void EnforceTotalMinimum(List<ProjectTask> tasks, BenchmarkContext benchmark, List<string> notes)
        {
            var total = tasks.Sum(task => task.EstimatedHours ?? 0);
            var target = Math.Max(benchmark.MinimumTotalHours, _policy.MinimumProjectHours);
            if (total >= target)
            {
                return;
            }

            var nonMicroTasks = tasks.Where(task => !IsMicroTask(task)).OrderByDescending(ComputeComplexityScore).ToList();
            if (nonMicroTasks.Count == 0)
            {
                nonMicroTasks = tasks.ToList();
            }

            IncreaseTaskHours(nonMicroTasks, target - total);
            notes.Add($"Raised project total from {total:F0}h to minimum {target}h.");
        }

        private static void IncreaseTaskHours(List<ProjectTask> tasks, double hoursToAdd)
        {
            if (tasks.Count == 0 || hoursToAdd <= 0)
            {
                return;
            }

            var increments = Math.Max(1, (int)Math.Ceiling(hoursToAdd / 4d));
            for (var index = 0; index < increments; index++)
            {
                var task = tasks[index % tasks.Count];
                var baseHours = task.EstimatedHours ?? 0;
                task.EstimatedHours = baseHours + 4;
            }
        }

        private List<CategoryEstimateSummary> BuildCategoryBreakdown(
            IReadOnlyCollection<ProjectTask> tasks,
            BenchmarkContext benchmark)
        {
            var result = new List<CategoryEstimateSummary>(benchmark.Workstreams.Count);
            foreach (var workstream in benchmark.Workstreams)
            {
                var streamTasks = tasks.Where(task =>
                    ResolveWorkstreamKey(task, benchmark).Equals(workstream.Key, StringComparison.OrdinalIgnoreCase)).ToList();

                result.Add(new CategoryEstimateSummary
                {
                    CategoryKey = workstream.Key,
                    CategoryName = workstream.Name,
                    Hours = (int)Math.Round(streamTasks.Sum(task => task.EstimatedHours ?? 0), MidpointRounding.AwayFromZero),
                    MinimumExpectedHours = workstream.MinHours,
                    MaximumExpectedHours = workstream.MaxHours,
                    TaskCount = streamTasks.Count
                });
            }

            return result;
        }

        private static string ResolveWorkstreamKey(ProjectTask task, BenchmarkContext benchmark)
        {
            var text = $"{task.Title} {task.Description} {string.Join(' ', task.TechStack)}".ToLowerInvariant();

            string? bestKey = null;
            var bestScore = 0;
            foreach (var workstream in benchmark.Workstreams)
            {
                var score = workstream.Keywords.Count(keyword =>
                    text.Contains(keyword.ToLowerInvariant(), StringComparison.Ordinal));

                if (score > bestScore)
                {
                    bestScore = score;
                    bestKey = workstream.Key;
                }
            }

            if (!string.IsNullOrWhiteSpace(bestKey))
            {
                return bestKey;
            }

            var fallbackQuality = benchmark.Workstreams.FirstOrDefault(workstream =>
                workstream.Key.Equals("QualityTestingPmUx", StringComparison.OrdinalIgnoreCase));
            if (fallbackQuality is not null &&
                (text.Contains("test", StringComparison.Ordinal) || text.Contains("qa", StringComparison.Ordinal) ||
                 text.Contains("review", StringComparison.Ordinal) || text.Contains("release", StringComparison.Ordinal)))
            {
                return fallbackQuality.Key;
            }

            return benchmark.Workstreams.FirstOrDefault()?.Key ?? "Uncategorized";
        }

        private int ComputeComplexityScore(ProjectTask task)
        {
            var text = $"{task.Title} {task.Description} {string.Join(' ', task.TechStack)}".ToLowerInvariant();
            var score = 0;

            score += MediumComplexityKeywords.Count(keyword => text.Contains(keyword, StringComparison.Ordinal));
            score += HighComplexityKeywords.Count(keyword => text.Contains(keyword, StringComparison.Ordinal)) * 2;
            score += CriticalComplexityKeywords.Count(keyword => text.Contains(keyword, StringComparison.Ordinal)) * 3;

            if (task.Description.Length > 140)
            {
                score += 1;
            }

            return score;
        }

        private static bool IsMicroTask(ProjectTask task)
        {
            var text = $"{task.Title} {task.Description}".ToLowerInvariant();
            return MicroTaskKeywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal));
        }

        private static bool IsMultipleOf4(double value) => Math.Abs(value % 4) < 0.001d;

        private static int RoundUpTo4(double value) => (int)(Math.Ceiling(value / 4d) * 4d);

        private double ResolveQualityOverheadRatio(BenchmarkContext benchmark)
        {
            var profile = _settings.BenchmarkProfiles.FirstOrDefault(candidate =>
                candidate.Name.Equals(benchmark.ProfileName, StringComparison.OrdinalIgnoreCase));
            return profile?.QualityOverheadRatio ?? 0.2;
        }

        private static PolicyDecision Accepted(
            BenchmarkContext benchmark,
            IReadOnlyCollection<CategoryEstimateSummary> categories,
            string reason) =>
            new()
            {
                IsAccepted = true,
                TargetAgent = "Estimator",
                Reason = reason,
                Benchmark = benchmark,
                CategoryBreakdown = categories.ToList()
            };

        private static PolicyDecision Rejected(
            BenchmarkContext benchmark,
            IReadOnlyCollection<CategoryEstimateSummary> categories,
            string targetAgent,
            string reason) =>
            new()
            {
                IsAccepted = false,
                TargetAgent = targetAgent,
                Reason = reason,
                Benchmark = benchmark,
                CategoryBreakdown = categories.ToList()
            };
    }

    public sealed class PolicyDecision
    {
        public bool IsAccepted { get; init; }
        public string TargetAgent { get; init; } = "Estimator";
        public string Reason { get; init; } = string.Empty;
        public BenchmarkContext Benchmark { get; init; } = new();
        public List<CategoryEstimateSummary> CategoryBreakdown { get; init; } = new();
    }

    public sealed class PolicyNormalizationResult
    {
        public List<ProjectTask> Tasks { get; init; } = new();
        public BenchmarkContext Benchmark { get; init; } = new();
        public List<CategoryEstimateSummary> CategoryBreakdown { get; init; } = new();
        public List<string> Notes { get; init; } = new();
    }

    public sealed class BenchmarkContext
    {
        public string ProfileName { get; init; } = string.Empty;
        public int MinimumTotalHours { get; init; }
        public int MaximumTotalHours { get; init; }
        public int MinimumTaskCount { get; init; }
        public List<BenchmarkWorkstreamContext> Workstreams { get; init; } = new();
    }

    public sealed class BenchmarkWorkstreamContext
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int MinHours { get; init; }
        public int MaxHours { get; init; }
        public List<string> Keywords { get; init; } = new();
    }
}
