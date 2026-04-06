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
        private readonly int _maxClarificationRounds;
        private readonly int _projectContextMaxCharacters;

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
            _maxClarificationRounds = Math.Max(1, options.Value.MaxClarificationRounds);
            _projectContextMaxCharacters = Math.Max(2000, options.Value.DecomposerProjectContextMaxCharacters);
        }

        public async Task<WorkflowStepResult> RunSessionAsync(
            WorkflowSession session,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);

            if (session.Result is not null)
            {
                return WorkflowStepResult.Completed(session.Result);
            }

            if (string.IsNullOrWhiteSpace(session.ProjectDescription))
            {
                throw new InvalidOperationException("Project description is required.");
            }

            var decomposition = await DecomposeAsync(session, cancellationToken);
            if (decomposition.NeedsClarification)
            {
                session.PendingQuestion = decomposition.Question;
                session.QuestionsAsked += 1;
                session.UpdatedAtUtc = DateTime.UtcNow;
                return WorkflowStepResult.NeedsClarification(decomposition.Question!);
            }

            session.PendingQuestion = null;

            var notes = new List<string>();
            var estimatedTasks = await EstimateTasksAsync(
                session.ProjectDescription,
                session.Clarifications,
                decomposition.Tasks!,
                null,
                cancellationToken);

            var validationIterations = 0;
            for (var cycle = 1; cycle <= _maxValidationCycles; cycle++)
            {
                validationIterations = cycle;

                var validatorInput = BuildValidatorInput(
                    session.ProjectDescription,
                    session.Clarifications,
                    estimatedTasks,
                    _projectContextMaxCharacters);

                var validatorRaw = await _validator.ExecuteAsync(validatorInput, cancellationToken);
                var feedback = ParseValidatorFeedback(validatorRaw);

                if (feedback.IsValid)
                {
                    var validatedResult = BuildResult(
                        session.ProjectDescription,
                        session.Clarifications,
                        estimatedTasks,
                        validationIterations,
                        notes);

                    session.Result = validatedResult;
                    session.UpdatedAtUtc = DateTime.UtcNow;
                    return WorkflowStepResult.Completed(validatedResult);
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

                estimatedTasks = await EstimateTasksAsync(
                    session.ProjectDescription,
                    session.Clarifications,
                    estimatedTasks,
                    feedback,
                    cancellationToken);
            }

            notes.Add("Maximum validation cycles reached. Returning latest estimator output.");

            var finalResult = BuildResult(
                session.ProjectDescription,
                session.Clarifications,
                estimatedTasks,
                validationIterations,
                notes);

            session.Result = finalResult;
            session.UpdatedAtUtc = DateTime.UtcNow;
            return WorkflowStepResult.Completed(finalResult);
        }

        private async Task<DecomposerDecision> DecomposeAsync(
            WorkflowSession session,
            CancellationToken cancellationToken)
        {
            var canAskMoreQuestions = session.QuestionsAsked < _maxClarificationRounds;
            var input = BuildDecomposerInput(
                session.ProjectDescription,
                session.Clarifications,
                session.QuestionsAsked,
                canAskMoreQuestions,
                forceFinalize: false,
                projectContextMaxCharacters: _projectContextMaxCharacters);

            var raw = await _decomposer.ExecuteAsync(input, cancellationToken);
            var decision = ParseDecomposerDecision(raw);

            if (decision.NeedsClarification && !canAskMoreQuestions)
            {
                var forcedInput = BuildDecomposerInput(
                    session.ProjectDescription,
                    session.Clarifications,
                    session.QuestionsAsked,
                    canAskMoreQuestions: false,
                    forceFinalize: true,
                    projectContextMaxCharacters: _projectContextMaxCharacters);

                var forcedRaw = await _decomposer.ExecuteAsync(forcedInput, cancellationToken);
                decision = ParseDecomposerDecision(forcedRaw);
            }

            if (decision.NeedsClarification)
            {
                var question = decision.Question?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(question))
                {
                    throw new InvalidOperationException("Decomposer requested clarification without a valid question.");
                }

                var alreadyAsked = session.Clarifications.Any(item =>
                    item.Question.Equals(question, StringComparison.OrdinalIgnoreCase));

                if (alreadyAsked)
                {
                    var forcedInput = BuildDecomposerInput(
                        session.ProjectDescription,
                        session.Clarifications,
                        session.QuestionsAsked,
                        canAskMoreQuestions: false,
                        forceFinalize: true,
                        projectContextMaxCharacters: _projectContextMaxCharacters);

                    var forcedRaw = await _decomposer.ExecuteAsync(forcedInput, cancellationToken);
                    decision = ParseDecomposerDecision(forcedRaw);
                }
            }

            if (decision.Tasks is { Count: > 0 })
            {
                return decision;
            }

            if (decision.NeedsClarification)
            {
                return decision;
            }

            throw new InvalidOperationException("Decomposer did not return valid clarification or tasks.");
        }

        private async Task<List<ProjectTask>> EstimateTasksAsync(
            string projectDescription,
            IReadOnlyCollection<ClarificationExchange> clarifications,
            IReadOnlyCollection<ProjectTask> tasks,
            ValidatorFeedback? validatorFeedback,
            CancellationToken cancellationToken)
        {
            var estimatorInput = BuildEstimatorInput(
                projectDescription,
                clarifications,
                tasks,
                validatorFeedback,
                _projectContextMaxCharacters);
            var estimatorRaw = await _estimator.ExecuteAsync(estimatorInput, cancellationToken);
            return ParseTasksOrThrow(estimatorRaw, "Estimator");
        }

        private string BuildDecomposerInput(
            string projectDescription,
            IReadOnlyCollection<ClarificationExchange> clarifications,
            int questionsAsked,
            bool canAskMoreQuestions,
            bool forceFinalize,
            int projectContextMaxCharacters)
        {
            var boundedDescription = LimitText(projectDescription, projectContextMaxCharacters);

            var builder = new StringBuilder();
            builder.AppendLine("Project description:");
            builder.AppendLine(boundedDescription);
            builder.AppendLine();
            builder.AppendLine("Clarification history (JSON):");
            builder.AppendLine(JsonSerializer.Serialize(clarifications, SerializerOptions));
            builder.AppendLine();
            builder.AppendLine($"Clarification budget: asked {questionsAsked} of {_maxClarificationRounds}.");

            if (forceFinalize || !canAskMoreQuestions)
            {
                builder.AppendLine("Mode: Finalize roadmap now. You are not allowed to ask more questions in this turn.");
            }
            else
            {
                builder.AppendLine("Mode: You may ask at most one blocking clarification question if required.");
            }

            return builder.ToString();
        }

        private static string BuildEstimatorInput(
            string projectDescription,
            IReadOnlyCollection<ClarificationExchange> clarifications,
            IReadOnlyCollection<ProjectTask> tasks,
            ValidatorFeedback? validatorFeedback,
            int projectContextMaxCharacters)
        {
            var boundedDescription = LimitText(projectDescription, projectContextMaxCharacters);

            var builder = new StringBuilder();
            builder.AppendLine("Project description:");
            builder.AppendLine(boundedDescription);
            builder.AppendLine();
            builder.AppendLine("Clarification history (JSON):");
            builder.AppendLine(JsonSerializer.Serialize(clarifications, SerializerOptions));
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

        private static string BuildValidatorInput(
            string projectDescription,
            IReadOnlyCollection<ClarificationExchange> clarifications,
            IReadOnlyCollection<ProjectTask> estimatedTasks,
            int projectContextMaxCharacters)
        {
            var boundedDescription = LimitText(projectDescription, projectContextMaxCharacters);

            var builder = new StringBuilder();
            builder.AppendLine("Project description:");
            builder.AppendLine(boundedDescription);
            builder.AppendLine();
            builder.AppendLine("Clarification history (JSON):");
            builder.AppendLine(JsonSerializer.Serialize(clarifications, SerializerOptions));
            builder.AppendLine();
            builder.AppendLine("Estimated roadmap tasks (JSON):");
            builder.AppendLine(JsonSerializer.Serialize(estimatedTasks, SerializerOptions));
            return builder.ToString();
        }

        private static string LimitText(string value, int maxCharacters)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim();
            if (maxCharacters <= 0 || normalized.Length <= maxCharacters)
            {
                return normalized;
            }

            var truncatedCharacters = normalized.Length - maxCharacters;
            return normalized[..maxCharacters] +
                   $"\n\n[Input truncated by orchestrator. Removed {truncatedCharacters} trailing characters.]";
        }

        private static DecomposerDecision ParseDecomposerDecision(string rawResponse)
        {
            var directTasks = TryParseTasks(rawResponse);
            if (directTasks is { Count: > 0 })
            {
                return DecomposerDecision.Ready(directTasks);
            }

            var objectJson = ExtractJsonObject(rawResponse);
            if (objectJson is null)
            {
                throw new InvalidOperationException("Decomposer response is not valid JSON.");
            }

            var response = JsonSerializer.Deserialize<DecomposerResponse>(objectJson, SerializerOptions);
            if (response is null)
            {
                throw new InvalidOperationException("Decomposer response could not be parsed.");
            }

            if (response.Tasks is { Count: > 0 } &&
                (string.IsNullOrWhiteSpace(response.Status) || response.Status.Equals("READY", StringComparison.OrdinalIgnoreCase)))
            {
                return DecomposerDecision.Ready(response.Tasks);
            }

            if (response.Status.Equals("NEEDS_CLARIFICATION", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(response.Question))
            {
                return DecomposerDecision.Ask(response.Question);
            }

            if (!string.IsNullOrWhiteSpace(response.Question) && response.Tasks.Count == 0)
            {
                return DecomposerDecision.Ask(response.Question);
            }

            throw new InvalidOperationException("Decomposer response does not contain valid clarification or tasks.");
        }

        private static List<ProjectTask> ParseTasksOrThrow(string rawResponse, string agentName)
        {
            var tasks = TryParseTasks(rawResponse);
            if (tasks is null || tasks.Count == 0)
            {
                throw new InvalidOperationException($"{agentName} response is not a valid task collection.");
            }

            return tasks;
        }

        private static List<ProjectTask>? TryParseTasks(string rawResponse)
        {
            var arrayJson = ExtractJsonArray(rawResponse);
            if (arrayJson is not null)
            {
                var arrayTasks = JsonSerializer.Deserialize<List<ProjectTask>>(arrayJson, SerializerOptions);
                if (arrayTasks is { Count: > 0 })
                {
                    return arrayTasks;
                }
            }

            var objectJson = ExtractJsonObject(rawResponse);
            if (objectJson is null)
            {
                return null;
            }

            var objectPayload = JsonSerializer.Deserialize<TasksPayload>(objectJson, SerializerOptions);
            if (objectPayload?.Tasks is { Count: > 0 })
            {
                return objectPayload.Tasks;
            }

            return null;
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
            IReadOnlyCollection<ClarificationExchange> clarifications,
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
                ValidationNotes = notes.ToList(),
                ClarificationHistory = clarifications
                    .Select(item => new ClarificationExchange
                    {
                        Question = item.Question,
                        Answer = item.Answer
                    })
                    .ToList()
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

        private sealed class DecomposerResponse
        {
            public string Status { get; set; } = string.Empty;
            public string Question { get; set; } = string.Empty;
            public List<ProjectTask> Tasks { get; set; } = new();
        }

        private sealed class TasksPayload
        {
            public List<ProjectTask> Tasks { get; set; } = new();
        }

        private sealed class DecomposerDecision
        {
            public bool NeedsClarification { get; private init; }
            public string? Question { get; private init; }
            public List<ProjectTask>? Tasks { get; private init; }

            public static DecomposerDecision Ask(string question) =>
                new()
                {
                    NeedsClarification = true,
                    Question = question
                };

            public static DecomposerDecision Ready(List<ProjectTask> tasks) =>
                new()
                {
                    NeedsClarification = false,
                    Tasks = tasks
                };
        }
    }
}
