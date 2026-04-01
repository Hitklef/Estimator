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
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly DecomposerAgent _decomposer;
        private readonly EstimatorAgent _estimator;
        private readonly ValidatorAgent _validator;
        private readonly ILogger<AgentOrchestrator> _logger;
        private readonly int _maxValidationCycles;

        public AgentOrchestrator(
            DecomposerAgent decomposer,
            EstimatorAgent estimator,
            ValidatorAgent validator,
            IOptions<AiSettings> options,
            ILogger<AgentOrchestrator> logger)
        {
            _decomposer = decomposer;
            _estimator = estimator;
            _validator = validator;
            _logger = logger;
            _maxValidationCycles = Math.Max(1, options.Value.MaxValidationCycles);
        }

        public async Task<ProjectEstimationResult> RunWorkflowAsync(
            string projectDescription,
            CancellationToken cancellationToken = default)
        {
            var notes = new List<string>();

            var decomposerRaw = await _decomposer.ExecuteAsync(projectDescription, cancellationToken);
            var decomposedTasks = ParseTasksOrThrow(decomposerRaw, "Decomposer");

            var estimatedTasks = await EstimateTasksAsync(projectDescription, decomposedTasks, null, cancellationToken);

            var validationIterations = 0;
            for (var cycle = 1; cycle <= _maxValidationCycles; cycle++)
            {
                validationIterations = cycle;

                var validatorInput = BuildValidatorInput(projectDescription, estimatedTasks);
                var validatorRaw = await _validator.ExecuteAsync(validatorInput, cancellationToken);
                var feedback = ParseValidatorFeedback(validatorRaw);

                if (feedback.IsValid)
                {
                    return BuildResult(projectDescription, estimatedTasks, validationIterations, notes);
                }

                if (!feedback.TargetAgent.Equals("Estimator", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Validator routed rework to unsupported agent '{feedback.TargetAgent}'.");
                }

                if (!string.IsNullOrWhiteSpace(feedback.Reason))
                {
                    notes.Add($"Validation cycle {cycle}: {feedback.Reason}");
                }

                _logger.LogWarning("Validator rejected estimate in cycle {Cycle}: {Reason}", cycle, feedback.Reason);

                if (cycle == _maxValidationCycles)
                {
                    break;
                }

                estimatedTasks = await EstimateTasksAsync(projectDescription, estimatedTasks, feedback, cancellationToken);
            }

            notes.Add("Maximum validation cycles reached. Returning latest estimator output.");
            return BuildResult(projectDescription, estimatedTasks, validationIterations, notes);
        }

        private async Task<List<ProjectTask>> EstimateTasksAsync(
            string projectDescription,
            IReadOnlyCollection<ProjectTask> tasks,
            ValidatorFeedback? validatorFeedback,
            CancellationToken cancellationToken)
        {
            var estimatorInput = BuildEstimatorInput(projectDescription, tasks, validatorFeedback);
            var estimatorRaw = await _estimator.ExecuteAsync(estimatorInput, cancellationToken);
            return ParseTasksOrThrow(estimatorRaw, "Estimator");
        }

        private static string BuildEstimatorInput(
            string projectDescription,
            IReadOnlyCollection<ProjectTask> tasks,
            ValidatorFeedback? validatorFeedback)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Project description:");
            builder.AppendLine(projectDescription);
            builder.AppendLine();
            builder.AppendLine("Roadmap tasks (JSON):");
            builder.AppendLine(JsonSerializer.Serialize(tasks, SerializerOptions));

            if (validatorFeedback is not null && !validatorFeedback.IsValid)
            {
                builder.AppendLine();
                builder.AppendLine("Validator feedback (JSON):");
                builder.AppendLine(JsonSerializer.Serialize(validatorFeedback, SerializerOptions));
            }

            return builder.ToString();
        }

        private static string BuildValidatorInput(string projectDescription, IReadOnlyCollection<ProjectTask> estimatedTasks)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Project description:");
            builder.AppendLine(projectDescription);
            builder.AppendLine();
            builder.AppendLine("Estimated roadmap tasks (JSON):");
            builder.AppendLine(JsonSerializer.Serialize(estimatedTasks, SerializerOptions));
            return builder.ToString();
        }

        private static List<ProjectTask> ParseTasksOrThrow(string rawResponse, string agentName)
        {
            var json = ExtractJsonArray(rawResponse);
            if (json is null)
            {
                throw new InvalidOperationException($"{agentName} response is not a valid JSON array.");
            }

            var tasks = JsonSerializer.Deserialize<List<ProjectTask>>(json, SerializerOptions);
            if (tasks is null || tasks.Count == 0)
            {
                throw new InvalidOperationException($"{agentName} returned no tasks.");
            }

            return tasks;
        }

        private static ValidatorFeedback ParseValidatorFeedback(string rawResponse)
        {
            var trimmed = CleanText(rawResponse);
            if (trimmed.Equals("VALID", StringComparison.OrdinalIgnoreCase))
            {
                return new ValidatorFeedback
                {
                    Status = "VALID",
                    TargetAgent = "Estimator"
                };
            }

            var json = ExtractJsonObject(trimmed);
            if (json is null)
            {
                throw new InvalidOperationException("Validator response is neither VALID nor a valid JSON object.");
            }

            var feedback = JsonSerializer.Deserialize<ValidatorFeedback>(json, SerializerOptions);
            if (feedback is null)
            {
                throw new InvalidOperationException("Validator response could not be parsed.");
            }

            return feedback;
        }

        private static ProjectEstimationResult BuildResult(
            string projectDescription,
            IReadOnlyCollection<ProjectTask> tasks,
            int validationIterations,
            IReadOnlyCollection<string> notes)
        {
            var normalizedTasks = tasks.ToList();
            var techStack = normalizedTasks
                .SelectMany(task => task.TechStack)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item)
                .ToList();

            return new ProjectEstimationResult
            {
                ProjectSummary = projectDescription,
                TechStack = techStack,
                Tasks = normalizedTasks,
                ValidationIterations = validationIterations,
                ValidationNotes = notes.ToList()
            };
        }

        private static string CleanText(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            return input
                .Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
        }

        private static string? ExtractJsonArray(string input)
        {
            var cleaned = CleanText(input);
            var start = cleaned.IndexOf('[');
            var end = cleaned.LastIndexOf(']');
            if (start < 0 || end <= start)
            {
                return null;
            }

            return cleaned.Substring(start, end - start + 1);
        }

        private static string? ExtractJsonObject(string input)
        {
            var cleaned = CleanText(input);
            var start = cleaned.IndexOf('{');
            var end = cleaned.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return null;
            }

            return cleaned.Substring(start, end - start + 1);
        }
    }
}
