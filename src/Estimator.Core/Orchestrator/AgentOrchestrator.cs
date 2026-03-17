using System.Text;
using System.Text.Json;
using Estimator.Core.Agents;
using Estimator.Core.Models;
using Estimator.Core.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Estimator.Core.Orchestrator
{
    public sealed class AgentOrchestrator
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

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

        private readonly DecomposerAgent _decomposer;
        private readonly EstimatorAgent _estimator;
        private readonly IEstimationPolicy _estimationPolicy;
        private readonly ILogger<AgentOrchestrator> _logger;
        private readonly int _maxEstimatorReworkCycles;
        private readonly int _minimumTasksPerWorkstream;
        private readonly EstimationPolicySettings _policySettings;

        public AgentOrchestrator(
            DecomposerAgent decomposer,
            EstimatorAgent estimator,
            ValidatorAgent _,
            IEstimationPolicy estimationPolicy,
            IOptions<AiSettings> options,
            ILogger<AgentOrchestrator> logger)
        {
            _decomposer = decomposer;
            _estimator = estimator;
            _estimationPolicy = estimationPolicy;
            _logger = logger;
            _maxEstimatorReworkCycles = Math.Max(1, options.Value.MaxValidationCycles);
            _minimumTasksPerWorkstream = Math.Max(1, options.Value.EstimationPolicy.MinimumTasksPerWorkstream);
            _policySettings = options.Value.EstimationPolicy;
        }

        public async Task<ProjectEstimationResult> RunWorkflowAsync(
            string projectDescription,
            CancellationToken cancellationToken = default)
        {
            var notes = new List<string>();
            var benchmark = _estimationPolicy.ResolveBenchmarkContext(projectDescription);
            var categoryBreakdown = new List<CategoryEstimateSummary>();
            var estimatorFeedback = string.Empty;

            var tasks = await DecomposeAsync(
                projectDescription,
                $"Target benchmark profile: {benchmark.ProfileName}. Build full production roadmap once.",
                benchmark,
                cancellationToken);

            tasks = EnsureCoverageTasks(tasks, benchmark, notes, "Roadmap preparation");

            for (var cycle = 1; cycle <= _maxEstimatorReworkCycles; cycle++)
            {
                var estimatedTasks = await EstimateAsync(projectDescription, tasks, estimatorFeedback, benchmark, cancellationToken);
                estimatedTasks = EnsureCoverageTasks(estimatedTasks, benchmark, notes, $"Estimator cycle {cycle}");

                var normalization = _estimationPolicy.Normalize(estimatedTasks, projectDescription);
                benchmark = normalization.Benchmark;
                categoryBreakdown = normalization.CategoryBreakdown;
                tasks = EnsureTaskIds(normalization.Tasks);

                if (normalization.Notes.Count > 0)
                {
                    notes.AddRange(normalization.Notes.Select(note => $"Calibration cycle {cycle}: {note}"));
                }

                var policyDecision = _estimationPolicy.EvaluateEstimatedPlan(tasks, projectDescription);
                benchmark = policyDecision.Benchmark;
                categoryBreakdown = policyDecision.CategoryBreakdown;
                if (!policyDecision.IsAccepted)
                {
                    estimatorFeedback = policyDecision.Reason;
                    notes.Add($"Policy rework cycle {cycle}: {policyDecision.Reason}");
                    _logger.LogWarning("Policy requested estimator rework cycle {Cycle}: {Reason}", cycle, policyDecision.Reason);
                    continue;
                }

                var validatorFeedback = ValidateEstimatesDeterministically(tasks, benchmark, categoryBreakdown);
                if (validatorFeedback.IsValid)
                {
                    return BuildResult(projectDescription, tasks, cycle, notes, benchmark, categoryBreakdown);
                }

                estimatorFeedback = BuildEstimatorFeedback(validatorFeedback, tasks);
                notes.Add($"Validator rework cycle {cycle}: {validatorFeedback.Reason}");
                _logger.LogWarning("Validator requested estimator rework cycle {Cycle}: {Reason}", cycle, validatorFeedback.Reason);
            }

            notes.Add("Max estimator rework cycles reached. Returning calibrated best-effort plan.");
            var finalNormalization = _estimationPolicy.Normalize(tasks, projectDescription);
            return BuildResult(
                projectDescription,
                EnsureTaskIds(finalNormalization.Tasks),
                _maxEstimatorReworkCycles,
                notes,
                finalNormalization.Benchmark,
                finalNormalization.CategoryBreakdown);
        }

        private async Task<List<ProjectTask>> DecomposeAsync(
            string projectDescription,
            string? feedback,
            BenchmarkContext benchmark,
            CancellationToken cancellationToken)
        {
            var request = new StringBuilder();
            request.AppendLine(projectDescription);
            request.AppendLine();
            request.AppendLine("Benchmark expectations:");
            request.AppendLine($"- Profile: {benchmark.ProfileName}");
            request.AppendLine($"- Minimum tasks: {benchmark.MinimumTaskCount}");
            request.AppendLine("- Required workstreams:");
            foreach (var workstream in benchmark.Workstreams)
            {
                request.AppendLine($"  - {workstream.Name}");
            }

            if (!string.IsNullOrWhiteSpace(feedback))
            {
                request.AppendLine();
                request.Append("Feedback: ");
                request.Append(feedback);
            }

            var raw = await _decomposer.ExecuteAsync(request.ToString(), cancellationToken);
            var tasks = EnsureTaskIds(ParseTasks(raw));
            return tasks.Count == 0 ? BuildFallbackRoadmap(benchmark) : tasks;
        }

        private async Task<List<ProjectTask>> EstimateAsync(
            string projectDescription,
            IReadOnlyCollection<ProjectTask> tasks,
            string? feedback,
            BenchmarkContext benchmark,
            CancellationToken cancellationToken)
        {
            var payload = new
            {
                project_description = projectDescription,
                benchmark_profile = benchmark.ProfileName,
                benchmark_total_hours = new
                {
                    minimum = benchmark.MinimumTotalHours,
                    maximum = benchmark.MaximumTotalHours
                },
                benchmark_categories = benchmark.Workstreams.Select(workstream => new
                {
                    key = workstream.Key,
                    name = workstream.Name,
                    min_hours = workstream.MinHours,
                    max_hours = workstream.MaxHours
                }),
                feedback = feedback ?? string.Empty,
                tasks
            };

            var raw = await _estimator.ExecuteAsync(JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);
            var estimatedTasks = EnsureTaskIds(ParseTasks(raw));

            if (estimatedTasks.Count == 0)
            {
                estimatedTasks = tasks
                    .Select(task => new ProjectTask
                    {
                        Id = task.Id,
                        Title = task.Title,
                        Description = task.Description,
                        TechStack = task.TechStack.ToList(),
                        EstimatedHours = task.EstimatedHours
                    })
                    .ToList();
            }

            return estimatedTasks;
        }

        private ValidatorFeedback ValidateEstimatesDeterministically(
            IReadOnlyCollection<ProjectTask> tasks,
            BenchmarkContext benchmark,
            IReadOnlyCollection<CategoryEstimateSummary> categoryBreakdown)
        {
            var invalidIds = new List<int>();
            var reasons = new List<string>();

            foreach (var task in tasks)
            {
                var hours = task.EstimatedHours ?? 0;
                if (hours <= 0)
                {
                    invalidIds.Add(task.Id);
                    reasons.Add($"Task {task.Id} has no valid estimate.");
                    continue;
                }

                if (!IsMicroTask(task) && !IsMultipleOf4(hours))
                {
                    invalidIds.Add(task.Id);
                    reasons.Add($"Task {task.Id} must use 4-hour increments.");
                    continue;
                }

                var minimum = GetTaskMinimumHours(task);
                if (hours < minimum)
                {
                    invalidIds.Add(task.Id);
                    reasons.Add($"Task {task.Id} appears underestimated ({hours:F0}h < {minimum}h).");
                }
            }

            var underRangeCategories = categoryBreakdown
                .Where(category => category.Hours < category.MinimumExpectedHours)
                .ToList();

            foreach (var category in underRangeCategories)
            {
                var matchingTasks = tasks
                    .Where(task => TaskMatchesWorkstream(task, benchmark.Workstreams.First(stream =>
                        stream.Key.Equals(category.CategoryKey, StringComparison.OrdinalIgnoreCase))))
                    .OrderBy(task => task.EstimatedHours ?? 0)
                    .Take(2)
                    .Select(task => task.Id)
                    .ToList();

                invalidIds.AddRange(matchingTasks);
                reasons.Add($"Category '{category.CategoryName}' is under minimum ({category.Hours}h < {category.MinimumExpectedHours}h).");
            }

            invalidIds = invalidIds.Distinct().ToList();
            if (invalidIds.Count == 0)
            {
                return new ValidatorFeedback
                {
                    Status = "VALID",
                    TargetAgent = "Estimator"
                };
            }

            return new ValidatorFeedback
            {
                Status = "REJECTED",
                TargetAgent = "Estimator",
                Reason = string.Join(" ", reasons.Distinct()),
                InvalidTaskIds = invalidIds
            };
        }

        private List<ProjectTask> EnsureCoverageTasks(
            IReadOnlyCollection<ProjectTask> sourceTasks,
            BenchmarkContext benchmark,
            List<string> notes,
            string stage)
        {
            var tasks = sourceTasks
                .Select(task => new ProjectTask
                {
                    Id = task.Id,
                    Title = task.Title,
                    Description = task.Description,
                    TechStack = task.TechStack.ToList(),
                    EstimatedHours = task.EstimatedHours
                })
                .ToList();

            var added = 0;
            foreach (var workstream in benchmark.Workstreams)
            {
                var matched = tasks.Count(task => TaskMatchesWorkstream(task, workstream));
                var required = Math.Max(_minimumTasksPerWorkstream, 1);
                var missing = Math.Max(0, required - matched);

                for (var index = 1; index <= missing; index++)
                {
                    tasks.Add(new ProjectTask
                    {
                        Title = $"{workstream.Name}: implementation {index}",
                        Description = $"Production-grade work for {workstream.Name}. Focus areas: {string.Join(", ", workstream.Keywords.Take(4))}.",
                        TechStack = [".NET", "C#", "Azure", "xUnit"]
                    });
                    added++;
                }
            }

            while (tasks.Count < benchmark.MinimumTaskCount)
            {
                tasks.Add(new ProjectTask
                {
                    Title = "Cross-cutting production hardening",
                    Description = "Add reliability, telemetry, release safeguards, and operational readiness.",
                    TechStack = [".NET", "C#", "Azure Monitor", "xUnit"]
                });
                added++;
            }

            if (added > 0)
            {
                notes.Add($"{stage}: Added {added} coverage tasks to satisfy production workstream minimums.");
            }

            return EnsureTaskIds(tasks);
        }

        private static string BuildEstimatorFeedback(ValidatorFeedback feedback, IReadOnlyCollection<ProjectTask> tasks)
        {
            if (feedback.InvalidTaskIds.Count == 0)
            {
                return feedback.Reason;
            }

            var affected = tasks
                .Where(task => feedback.InvalidTaskIds.Contains(task.Id))
                .Select(task => $"{task.Id}:{task.Title}")
                .ToList();

            if (affected.Count == 0)
            {
                return feedback.Reason;
            }

            return $"{feedback.Reason} Re-estimate tasks: {string.Join(", ", affected)}.";
        }

        private int GetTaskMinimumHours(ProjectTask task)
        {
            var text = $"{task.Title} {task.Description} {string.Join(' ', task.TechStack)}".ToLowerInvariant();
            if (CriticalComplexityKeywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal)))
            {
                return _policySettings.CriticalComplexityTaskFloorHours;
            }

            if (HighComplexityKeywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal)))
            {
                return _policySettings.HighComplexityTaskFloorHours;
            }

            if (MediumComplexityKeywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal)))
            {
                return _policySettings.MediumComplexityTaskFloorHours;
            }

            return _policySettings.MinimumHoursPerStandardTask;
        }

        private static bool TaskMatchesWorkstream(ProjectTask task, BenchmarkWorkstreamContext workstream)
        {
            var text = $"{task.Title} {task.Description} {string.Join(' ', task.TechStack)}".ToLowerInvariant();
            return workstream.Keywords.Any(keyword => text.Contains(keyword.ToLowerInvariant(), StringComparison.Ordinal));
        }

        private static bool IsMicroTask(ProjectTask task)
        {
            var text = $"{task.Title} {task.Description}".ToLowerInvariant();
            return MicroTaskKeywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal));
        }

        private static bool IsMultipleOf4(double value) => Math.Abs(value % 4) < 0.001d;

        private static ProjectEstimationResult BuildResult(
            string description,
            IReadOnlyCollection<ProjectTask> tasks,
            int validationIterations,
            IReadOnlyCollection<string> notes,
            BenchmarkContext benchmark,
            IReadOnlyCollection<CategoryEstimateSummary> categoryBreakdown)
        {
            var normalizedTasks = tasks.OrderBy(task => task.Id).ToList();
            var stack = normalizedTasks
                .SelectMany(task => task.TechStack)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item)
                .ToList();

            return new ProjectEstimationResult
            {
                ProjectSummary = description.Trim(),
                BenchmarkProfile = benchmark.ProfileName,
                Tasks = normalizedTasks,
                TechStack = stack,
                CategoryBreakdown = categoryBreakdown.OrderBy(category => category.CategoryName).ToList(),
                ValidationIterations = validationIterations,
                ValidationNotes = notes.ToList()
            };
        }

        private static List<ProjectTask> ParseTasks(string rawResponse)
        {
            var cleanJson = CleanJsonArray(rawResponse);
            try
            {
                return JsonSerializer.Deserialize<List<ProjectTask>>(cleanJson, JsonOptions) ?? [];
            }
            catch
            {
                return [];
            }
        }

        private List<ProjectTask> BuildFallbackRoadmap(BenchmarkContext benchmark)
        {
            var tasks = new List<ProjectTask>();
            foreach (var workstream in benchmark.Workstreams)
            {
                for (var index = 1; index <= _minimumTasksPerWorkstream; index++)
                {
                    tasks.Add(new ProjectTask
                    {
                        Title = $"{workstream.Name}: fallback task {index}",
                        Description = $"Fallback production roadmap task for {workstream.Name}.",
                        TechStack = [".NET", "C#", "Azure"]
                    });
                }
            }

            return EnsureCoverageTasks(tasks, benchmark, [], "Fallback");
        }

        private static List<ProjectTask> EnsureTaskIds(IReadOnlyCollection<ProjectTask> tasks)
        {
            var result = new List<ProjectTask>(tasks.Count);
            var nextId = 1;
            foreach (var task in tasks)
            {
                task.Id = task.Id <= 0 ? nextId : task.Id;
                nextId = Math.Max(nextId + 1, task.Id + 1);
                result.Add(task);
            }

            return result;
        }

        private static string CleanJsonArray(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "[]";
            }

            var cleaned = input.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();

            var start = cleaned.IndexOf('[');
            var end = cleaned.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                return cleaned.Substring(start, (end - start) + 1);
            }

            return "[]";
        }
    }
}
