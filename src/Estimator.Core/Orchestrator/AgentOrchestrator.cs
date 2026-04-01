using System.Text;
using Estimator.Core.Agents;
using Estimator.Core.Models;
using Estimator.Core.Models.Options;
using Estimator.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Estimator.Core.Orchestrator
{
    public sealed class AgentOrchestrator
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

        private static readonly Dictionary<string, List<RoadmapTemplate>> WorkstreamTemplates = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CoreSetupArchitecture"] =
            [
                new("Set up development environment", "Install required SDKs, configure local tooling, and establish the base solution structure.", [".NET", "C#", "Azure DevOps"]),
                new("Design solution architecture", "Define the application layers, domain boundaries, integration points, and deployment approach.", [".NET", "C#", "Azure"]),
                new("Model core data structures", "Design the persistence model, database schema, and data access conventions for the solution.", [".NET", "C#", "SQL Server"])
            ],
            ["CoreFeatures"] =
            [
                new("Implement core business workflows", "Build the main user-facing workflows and application services required for the primary use case.", [".NET", "C#", "xUnit"]),
                new("Add validation and business rules", "Implement input validation, domain rules, and error handling across the main feature set.", [".NET", "C#", "xUnit"]),
                new("Integrate key external dependencies", "Connect the system to the required external APIs, hardware interfaces, or vendor libraries.", [".NET", "C#", "Azure"])
            ],
            ["ExperienceAndInterface"] =
            [
                new("Design primary application screens", "Define the main user interface layout, navigation, and interaction flow for the application.", [".NET", "C#", "React"]),
                new("Implement user interface workflows", "Build the screens, forms, and interaction states required for day-to-day usage.", [".NET", "C#", "React"]),
                new("Polish usability and visual feedback", "Improve accessibility, loading states, validation feedback, and overall user experience.", [".NET", "C#", "React"])
            ],
            ["TreeGrowthSystem"] =
            [
                new("Implement progression and growth logic", "Build the rules that govern progression, milestones, and state changes in the growth system.", [".NET", "C#", "xUnit"]),
                new("Create visual growth feedback", "Develop the visual representation and update flow for the growth and progression experience.", [".NET", "C#", "React"]),
                new("Add milestone and streak handling", "Implement milestone triggers, streak tracking, and special progression states.", [".NET", "C#", "xUnit"])
            ],
            ["CommunityPrivacySecurity"] =
            [
                new("Implement authentication and access control", "Add secure sign-in, authorization rules, and role-aware access handling.", [".NET", "C#", "Azure AD"]),
                new("Build privacy and data protection flows", "Implement privacy rules, audit coverage, and controls for sensitive data handling.", [".NET", "C#", "SQL Server"]),
                new("Add sharing and community safeguards", "Implement safe community views, moderation constraints, and anonymized data handling where needed.", [".NET", "C#", "Azure"])
            ],
            ["QualityTestingPmUx"] =
            [
                new("Set up automated testing and QA workflow", "Create the testing foundations for unit, integration, and regression coverage.", [".NET", "C#", "xUnit"]),
                new("Run product and UX review cycle", "Validate the experience with PM and UX review feedback and capture follow-up changes.", [".NET", "C#", "Azure DevOps"]),
                new("Prepare release hardening and rollout checks", "Complete release-readiness validation, monitoring preparation, and deployment verification.", [".NET", "C#", "Azure Monitor"])
            ]
        };

        private static readonly List<RoadmapTemplate> CrossCuttingTemplates =
        [
            new("Add observability and operational telemetry", "Implement logging, monitoring, and diagnostics to support production operations.", [".NET", "C#", "Azure Monitor"]),
            new("Prepare release readiness checklist", "Validate deployment safeguards, rollback plans, and release criteria before launch.", [".NET", "C#", "Azure DevOps"]),
            new("Document support and maintenance procedures", "Capture support guidance, operational runbooks, and maintenance responsibilities.", [".NET", "C#", "Markdown"])
        ];

        private readonly DecomposerAgent _decomposer;
        private readonly EstimatorAgent _estimator;
        private readonly IEstimationPolicy _estimationPolicy;
        private readonly IProjectBriefPreprocessor _projectBriefPreprocessor;
        private readonly ILogger<AgentOrchestrator> _logger;
        private readonly int _maxEstimatorReworkCycles;
        private readonly int _minimumTasksPerWorkstream;
        private readonly EstimationPolicySettings _policySettings;
        private readonly int _decomposerProjectContextMaxCharacters;
        private readonly int _estimatorProjectContextMaxCharacters;
        private readonly int _estimatorTaskDescriptionMaxCharacters;
        private readonly int _estimatorFeedbackMaxCharacters;

        public AgentOrchestrator(
            DecomposerAgent decomposer,
            EstimatorAgent estimator,
            ValidatorAgent _,
            IEstimationPolicy estimationPolicy,
            IProjectBriefPreprocessor projectBriefPreprocessor,
            IOptions<AiSettings> options,
            ILogger<AgentOrchestrator> logger)
        {
            _decomposer = decomposer;
            _estimator = estimator;
            _estimationPolicy = estimationPolicy;
            _projectBriefPreprocessor = projectBriefPreprocessor;
            _logger = logger;
            _maxEstimatorReworkCycles = Math.Max(1, options.Value.MaxValidationCycles);
            _minimumTasksPerWorkstream = Math.Max(1, options.Value.EstimationPolicy.MinimumTasksPerWorkstream);
            _policySettings = options.Value.EstimationPolicy;
            _decomposerProjectContextMaxCharacters = Math.Max(2000, options.Value.DecomposerProjectContextMaxCharacters);
            _estimatorProjectContextMaxCharacters = Math.Max(1200, options.Value.EstimatorProjectContextMaxCharacters);
            _estimatorTaskDescriptionMaxCharacters = Math.Max(80, options.Value.EstimatorTaskDescriptionMaxCharacters);
            _estimatorFeedbackMaxCharacters = Math.Max(120, options.Value.EstimatorFeedbackMaxCharacters);
        }

        public async Task<ProjectEstimationResult> RunWorkflowAsync(
            string projectDescription,
            CancellationToken cancellationToken = default)
        {
            var notes = new List<string>();
            var preparedBrief = _projectBriefPreprocessor.Prepare(projectDescription);
            if (preparedBrief.WasCondensed)
            {
                notes.Add(
                    $"Input preparation: Condensed project description from {preparedBrief.OriginalCharacterCount} to {preparedBrief.PreparedCharacterCount} characters before agent processing.");
            }

            var benchmark = _estimationPolicy.ResolveBenchmarkContext(projectDescription);
            var categoryBreakdown = new List<CategoryEstimateSummary>();
            var estimatorFeedback = string.Empty;

            var tasks = await DecomposeAsync(
                LimitForPrompt(preparedBrief.ModelInput, _decomposerProjectContextMaxCharacters),
                $"Target benchmark profile: {benchmark.ProfileName}. Build full production roadmap once.",
                benchmark,
                cancellationToken);

            tasks = EnsureCoverageTasks(tasks, benchmark, notes, "Roadmap preparation");

            for (var cycle = 1; cycle <= _maxEstimatorReworkCycles; cycle++)
            {
                var estimatedTasks = await EstimateAsync(preparedBrief.ModelInput, tasks, estimatorFeedback, benchmark, cancellationToken);
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
                    return BuildResult(preparedBrief.ProjectSummary, tasks, cycle, notes, benchmark, categoryBreakdown);
                }

                estimatorFeedback = BuildEstimatorFeedback(validatorFeedback, tasks);
                notes.Add($"Validator rework cycle {cycle}: {validatorFeedback.Reason}");
                _logger.LogWarning("Validator requested estimator rework cycle {Cycle}: {Reason}", cycle, validatorFeedback.Reason);
            }

            notes.Add("Max estimator rework cycles reached. Returning calibrated best-effort plan.");
            var finalNormalization = _estimationPolicy.Normalize(tasks, projectDescription);
            return BuildResult(
                preparedBrief.ProjectSummary,
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
            var payload = BuildEstimatorPayload(projectDescription, tasks, feedback, benchmark);
            var raw = await _estimator.ExecuteAsync(payload, cancellationToken);
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
                    tasks.Add(CreateCoverageTask(workstream, matched + index));
                    added++;
                }
            }

            while (tasks.Count < benchmark.MinimumTaskCount)
            {
                tasks.Add(CreateCrossCuttingTask(tasks.Count + 1));
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
                return System.Text.Json.JsonSerializer.Deserialize<List<ProjectTask>>(cleanJson, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? [];
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
                    tasks.Add(CreateCoverageTask(workstream, index));
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

        private ProjectTask CreateCoverageTask(BenchmarkWorkstreamContext workstream, int templateIndex)
        {
            var templates = ResolveTemplatesForWorkstream(workstream);
            var template = templates[(Math.Max(templateIndex, 1) - 1) % templates.Count];

            return new ProjectTask
            {
                Title = template.Title,
                Description = template.Description,
                TechStack = [.. template.TechStack]
            };
        }

        private static List<RoadmapTemplate> ResolveTemplatesForWorkstream(BenchmarkWorkstreamContext workstream)
        {
            if (WorkstreamTemplates.TryGetValue(workstream.Key, out var templates) && templates.Count > 0)
            {
                return templates;
            }

            return
            [
                new(
                    $"Plan {workstream.Name.ToLowerInvariant()} implementation",
                    $"Plan and implement {workstream.Name.ToLowerInvariant()} with production-ready coverage for {string.Join(", ", workstream.Keywords.Take(4))}.",
                    [".NET", "C#", "Azure"]),
                new(
                    $"Build {workstream.Name.ToLowerInvariant()} workflow",
                    $"Deliver the main workflow for {workstream.Name.ToLowerInvariant()} and validate it end to end.",
                    [".NET", "C#", "xUnit"])
            ];
        }

        private static ProjectTask CreateCrossCuttingTask(int templateIndex)
        {
            var template = CrossCuttingTemplates[(Math.Max(templateIndex, 1) - 1) % CrossCuttingTemplates.Count];
            return new ProjectTask
            {
                Title = template.Title,
                Description = template.Description,
                TechStack = [.. template.TechStack]
            };
        }

        private string BuildEstimatorPayload(
            string projectDescription,
            IReadOnlyCollection<ProjectTask> tasks,
            string? feedback,
            BenchmarkContext benchmark)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Project context:");
            builder.AppendLine(LimitForPrompt(projectDescription, _estimatorProjectContextMaxCharacters));
            builder.AppendLine();
            builder.AppendLine($"Benchmark profile: {benchmark.ProfileName}");
            builder.AppendLine($"Target total hours: {benchmark.MinimumTotalHours}-{benchmark.MaximumTotalHours}");
            builder.AppendLine("Category targets:");

            foreach (var workstream in benchmark.Workstreams)
            {
                builder.AppendLine($"- {workstream.Name}: {workstream.MinHours}-{workstream.MaxHours}h");
            }

            if (!string.IsNullOrWhiteSpace(feedback))
            {
                builder.AppendLine();
                builder.AppendLine($"Feedback: {LimitForPrompt(feedback, _estimatorFeedbackMaxCharacters)}");
            }

            builder.AppendLine();
            builder.AppendLine("Tasks to estimate:");
            foreach (var task in tasks.OrderBy(task => task.Id))
            {
                var description = LimitForPrompt(task.Description, _estimatorTaskDescriptionMaxCharacters);
                var stack = task.TechStack
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(4);

                builder.Append("- ");
                builder.Append(task.Id);
                builder.Append(". ");
                builder.Append(task.Title);

                if (!string.IsNullOrWhiteSpace(description))
                {
                    builder.Append(" | ");
                    builder.Append(description);
                }

                var techStack = string.Join(", ", stack);
                if (!string.IsNullOrWhiteSpace(techStack))
                {
                    builder.Append(" | Tech: ");
                    builder.Append(techStack);
                }

                builder.AppendLine();
            }

            return builder.ToString().Trim();
        }

        private static string LimitForPrompt(string input, int maxCharacters)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var normalized = input.Trim();
            maxCharacters = Math.Max(100, maxCharacters);
            if (normalized.Length <= maxCharacters)
            {
                return normalized;
            }

            const string marker = " [...] ";
            if (maxCharacters <= marker.Length + 20)
            {
                return normalized[..maxCharacters];
            }

            var headLength = (int)(maxCharacters * 0.8);
            var tailLength = Math.Max(20, maxCharacters - headLength - marker.Length);
            return string.Concat(
                normalized.AsSpan(0, Math.Min(headLength, normalized.Length)),
                marker,
                normalized.AsSpan(Math.Max(0, normalized.Length - tailLength), Math.Min(tailLength, normalized.Length)));
        }

        private sealed record RoadmapTemplate(string Title, string Description, string[] TechStack);
    }
}
