using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Text.RegularExpressions;
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
        private readonly int _maxDecomposerCallsPerSession;

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
            _maxClarificationRounds = Math.Max(0, options.Value.MaxClarificationRounds);
            _projectContextMaxCharacters = Math.Max(2000, options.Value.DecomposerProjectContextMaxCharacters);
            _maxDecomposerCallsPerSession = Math.Max(1, options.Value.MaxDecomposerCallsPerSession);
        }

        public async Task<WorkflowStepResult> RunSessionAsync(
            WorkflowSession session,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            var workflowStopwatch = Stopwatch.StartNew();
            _logger.LogInformation(
                "Workflow session started. SessionId={SessionId} DescriptionChars={DescriptionChars} ClarificationCount={ClarificationCount} QuestionsAsked={QuestionsAsked}",
                session.SessionId,
                session.ProjectDescription?.Length ?? 0,
                session.Clarifications.Count,
                session.QuestionsAsked);

            if (session.Result is not null)
            {
                _logger.LogInformation(
                    "Workflow session {SessionId} already has cached result. Returning immediately.",
                    session.SessionId);
                return WorkflowStepResult.Completed(session.Result);
            }

            if (string.IsNullOrWhiteSpace(session.ProjectDescription))
            {
                throw new InvalidOperationException("Project description is required.");
            }

            var decomposition = await DecomposeAsync(session, cancellationToken);
            if (decomposition.NeedsClarification)
            {
                workflowStopwatch.Stop();
                session.PendingQuestion = decomposition.Question;
                session.QuestionsAsked += 1;
                session.UpdatedAtUtc = DateTime.UtcNow;
                _logger.LogInformation(
                    "Workflow session {SessionId} needs clarification. Question='{Question}' DurationMs={DurationMs}",
                    session.SessionId,
                    decomposition.Question,
                    workflowStopwatch.ElapsedMilliseconds);
                return WorkflowStepResult.NeedsClarification(decomposition.Question!);
            }

            session.PendingQuestion = null;

            var notes = new List<string>();
            _logger.LogInformation("Session {SessionId}: Estimation phase started.", session.SessionId);
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
                var cycleStopwatch = Stopwatch.StartNew();
                _logger.LogInformation("Session {SessionId}: Validation cycle {Cycle}/{MaxCycles} started.", session.SessionId, cycle, _maxValidationCycles);

                var validatorInput = BuildValidatorInput(
                    session.ProjectDescription,
                    session.Clarifications,
                    estimatedTasks,
                    _projectContextMaxCharacters);

                var validatorRaw = await _validator.ExecuteAsync(validatorInput, cancellationToken);
                var feedback = await ParseValidatorFeedbackWithRepairAsync(
                    validatorInput,
                    validatorRaw,
                    cancellationToken);

                if (feedback.IsValid)
                {
                    cycleStopwatch.Stop();
                    var validatedResult = BuildResult(
                        session.ProjectDescription,
                        session.Clarifications,
                        estimatedTasks,
                        validationIterations,
                        notes);

                    session.Result = validatedResult;
                    session.UpdatedAtUtc = DateTime.UtcNow;
                    workflowStopwatch.Stop();
                    _logger.LogInformation(
                        "Session {SessionId}: Validation passed in cycle {Cycle}. Workflow completed. TotalDurationMs={TotalDurationMs} CycleDurationMs={CycleDurationMs}",
                        session.SessionId,
                        cycle,
                        workflowStopwatch.ElapsedMilliseconds,
                        cycleStopwatch.ElapsedMilliseconds);
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
                cycleStopwatch.Stop();
                _logger.LogInformation(
                    "Session {SessionId}: Validation cycle {Cycle} finished with rejection. CycleDurationMs={CycleDurationMs}",
                    session.SessionId,
                    cycle,
                    cycleStopwatch.ElapsedMilliseconds);

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
            workflowStopwatch.Stop();
            _logger.LogWarning(
                "Session {SessionId}: Max validation cycles reached. Returning latest output. TotalDurationMs={TotalDurationMs}",
                session.SessionId,
                workflowStopwatch.ElapsedMilliseconds);
            return WorkflowStepResult.Completed(finalResult);
        }

        private async Task<DecomposerDecision> DecomposeAsync(
            WorkflowSession session,
            CancellationToken cancellationToken)
        {
            var decomposerCalls = 0;
            async Task<string> ExecuteDecomposerAsync(string reason, string inputText)
            {
                decomposerCalls++;
                if (decomposerCalls > _maxDecomposerCallsPerSession)
                {
                    throw new InvalidOperationException(
                        $"Decomposer call limit exceeded ({_maxDecomposerCallsPerSession}) in session {session.SessionId}.");
                }

                var callStopwatch = Stopwatch.StartNew();
                _logger.LogInformation(
                    "Session {SessionId}: Decomposer call {Call}/{MaxCalls} started. Reason={Reason} InputChars={InputChars}",
                    session.SessionId,
                    decomposerCalls,
                    _maxDecomposerCallsPerSession,
                    reason,
                    inputText.Length);

                var output = await _decomposer.ExecuteAsync(inputText, cancellationToken);
                callStopwatch.Stop();

                _logger.LogInformation(
                    "Session {SessionId}: Decomposer call {Call}/{MaxCalls} completed. OutputChars={OutputChars} DurationMs={DurationMs}",
                    session.SessionId,
                    decomposerCalls,
                    _maxDecomposerCallsPerSession,
                    output.Length,
                    callStopwatch.ElapsedMilliseconds);

                return output;
            }

            var canAskMoreQuestions = session.QuestionsAsked < _maxClarificationRounds;
            var forceFinalizeFromStart = !canAskMoreQuestions;
            var input = BuildDecomposerInput(
                session.ProjectDescription,
                session.Clarifications,
                session.QuestionsAsked,
                canAskMoreQuestions,
                forceFinalize: forceFinalizeFromStart,
                projectContextMaxCharacters: _projectContextMaxCharacters,
                strictNoQuestionMode: false);

            _logger.LogInformation(
                "Session {SessionId}: Decomposer mode prepared. CanAskMoreQuestions={CanAskMoreQuestions} ForceFinalize={ForceFinalize} QuestionsAsked={QuestionsAsked}/{MaxRounds}",
                session.SessionId,
                canAskMoreQuestions,
                forceFinalizeFromStart,
                session.QuestionsAsked,
                _maxClarificationRounds);

            var raw = await ExecuteDecomposerAsync("initial", input);
            var decision = await ParseDecomposerDecisionWithRepairAsync(input, raw, cancellationToken);

            if (decision.NeedsClarification && !canAskMoreQuestions)
            {
                _logger.LogWarning(
                    "Session {SessionId}: Decomposer requested clarification but budget exhausted. Forcing finalize.",
                    session.SessionId);
                var forcedInput = BuildDecomposerInput(
                    session.ProjectDescription,
                    session.Clarifications,
                    session.QuestionsAsked,
                    canAskMoreQuestions: false,
                    forceFinalize: true,
                    projectContextMaxCharacters: _projectContextMaxCharacters,
                    strictNoQuestionMode: true);

                var forcedRaw = await ExecuteDecomposerAsync("forced-finalize-after-budget", forcedInput);
                decision = await ParseDecomposerDecisionWithRepairAsync(forcedInput, forcedRaw, cancellationToken);
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
                    _logger.LogWarning(
                        "Session {SessionId}: Decomposer repeated an already asked question. Forcing finalize.",
                        session.SessionId);
                    var forcedInput = BuildDecomposerInput(
                        session.ProjectDescription,
                        session.Clarifications,
                        session.QuestionsAsked,
                        canAskMoreQuestions: false,
                        forceFinalize: true,
                        projectContextMaxCharacters: _projectContextMaxCharacters,
                        strictNoQuestionMode: true);

                    var forcedRaw = await ExecuteDecomposerAsync("forced-finalize-after-duplicate-question", forcedInput);
                    decision = await ParseDecomposerDecisionWithRepairAsync(forcedInput, forcedRaw, cancellationToken);
                }
            }

            if (decision.Tasks is { Count: > 0 })
            {
                _logger.LogInformation(
                    "Session {SessionId}: Decomposer returned roadmap with {TaskCount} tasks.",
                    session.SessionId,
                    decision.Tasks.Count);
                return decision;
            }

            if (decision.NeedsClarification)
            {
                _logger.LogInformation(
                    "Session {SessionId}: Decomposer returned clarification request.",
                    session.SessionId);
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
            var estimateStopwatch = Stopwatch.StartNew();
            var estimatorInput = BuildEstimatorInput(
                projectDescription,
                clarifications,
                tasks,
                validatorFeedback,
                _projectContextMaxCharacters);
            _logger.LogInformation(
                "Estimator phase request. InputTaskCount={InputTaskCount} ValidatorFeedbackPresent={HasValidatorFeedback} InputChars={InputChars}",
                tasks.Count,
                validatorFeedback is not null,
                estimatorInput.Length);
            var estimatorRaw = await _estimator.ExecuteAsync(estimatorInput, cancellationToken);
            var parsed = await ParseTasksWithRepairAsync(
                estimatorInput,
                estimatorRaw,
                "Estimator",
                cancellationToken);
            estimateStopwatch.Stop();
            _logger.LogInformation(
                "Estimator phase completed. OutputTaskCount={OutputTaskCount} DurationMs={DurationMs}",
                parsed.Count,
                estimateStopwatch.ElapsedMilliseconds);
            return parsed;
        }

        private async Task<DecomposerDecision> ParseDecomposerDecisionWithRepairAsync(
            string originalInput,
            string rawResponse,
            CancellationToken cancellationToken)
        {
            try
            {
                return ParseDecomposerDecision(rawResponse);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Decomposer response parse failed. Starting format repair call. RawChars={RawChars}",
                    rawResponse?.Length ?? 0);
                var repairInput = originalInput +
                                  "\n\nFORMAT REPAIR: Your previous reply was not valid JSON. " +
                                  "Return ONLY valid JSON matching the required schema. No prose, no markdown.";

                var repairedRaw = await _decomposer.ExecuteAsync(repairInput, cancellationToken);
                _logger.LogInformation(
                    "Decomposer format repair response received. RawChars={RawChars}",
                    repairedRaw?.Length ?? 0);
                return ParseDecomposerDecision(repairedRaw ?? string.Empty);
            }
        }

        private async Task<List<ProjectTask>> ParseTasksWithRepairAsync(
            string originalInput,
            string rawResponse,
            string agentName,
            CancellationToken cancellationToken)
        {
            try
            {
                return ParseTasksOrThrow(rawResponse, agentName);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "{AgentName} response parse failed. Starting format repair call. RawChars={RawChars}",
                    agentName,
                    rawResponse?.Length ?? 0);
                var repairInput = originalInput +
                                  "\n\nFORMAT REPAIR: Your previous reply was not valid JSON. " +
                                  "Return ONLY a valid JSON array of tasks. No prose, no markdown.";

                var repairedRaw = await _estimator.ExecuteAsync(repairInput, cancellationToken);
                _logger.LogInformation(
                    "{AgentName} format repair response received. RawChars={RawChars}",
                    agentName,
                    repairedRaw?.Length ?? 0);
                return ParseTasksOrThrow(repairedRaw ?? string.Empty, agentName);
            }
        }

        private string BuildDecomposerInput(
            string projectDescription,
            IReadOnlyCollection<ClarificationExchange> clarifications,
            int questionsAsked,
            bool canAskMoreQuestions,
            bool forceFinalize,
            int projectContextMaxCharacters,
            bool strictNoQuestionMode)
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
                if (strictNoQuestionMode)
                {
                    builder.AppendLine("Strict mode: returning NEEDS_CLARIFICATION is forbidden. Return READY with tasks only.");
                }
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

            var response = TryDeserializeWithAutoRepair<DecomposerResponse>(objectJson);
            if (response is null)
            {
                throw new InvalidOperationException("Decomposer response contains malformed JSON.");
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
                var arrayTasks = TryDeserializeWithAutoRepair<List<ProjectTask>>(arrayJson);
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

            var objectPayload = TryDeserializeWithAutoRepair<TasksPayload>(objectJson);
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

            var feedback = TryDeserializeWithAutoRepair<ValidatorFeedback>(json);
            if (feedback is null)
            {
                throw new InvalidOperationException("Validator response contains malformed JSON.");
            }

            return feedback;
        }

        private async Task<ValidatorFeedback> ParseValidatorFeedbackWithRepairAsync(
            string originalInput,
            string rawResponse,
            CancellationToken cancellationToken)
        {
            try
            {
                return ParseValidatorFeedback(rawResponse);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Validator response parse failed. Starting format repair call. RawChars={RawChars}",
                    rawResponse?.Length ?? 0);
                var repairInput = originalInput +
                                  "\n\nFORMAT REPAIR: Your previous reply was not valid. " +
                                  "Return EXACTLY either 'VALID' or the required JSON object only.";

                var repairedRaw = await _validator.ExecuteAsync(repairInput, cancellationToken);
                _logger.LogInformation(
                    "Validator format repair response received. RawChars={RawChars}",
                    repairedRaw?.Length ?? 0);
                return ParseValidatorFeedback(repairedRaw ?? string.Empty);
            }
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
            if (start < 0)
            {
                return null;
            }

            if (end > start)
            {
                return cleaned.Substring(start, end - start + 1);
            }

            return cleaned[start..];
        }

        private static string? ExtractJsonObject(string input)
        {
            var cleaned = CleanText(input);
            var start = cleaned.IndexOf('{');
            var end = cleaned.LastIndexOf('}');
            if (start < 0)
            {
                return null;
            }

            if (end > start)
            {
                return cleaned.Substring(start, end - start + 1);
            }

            return cleaned[start..];
        }

        private static T? TryDeserializeWithAutoRepair<T>(string json)
            where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json, SerializerOptions);
            }
            catch (JsonException)
            {
                var repairedJson = TryAutoCloseJson(json);
                if (string.Equals(repairedJson, json, StringComparison.Ordinal))
                {
                    return null;
                }

                try
                {
                    return JsonSerializer.Deserialize<T>(repairedJson, SerializerOptions);
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        private static string TryAutoCloseJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return json;
            }

            var stack = new Stack<char>();
            var builder = new StringBuilder(json.Length + 32);
            var inString = false;
            var escape = false;

            foreach (var ch in json)
            {
                builder.Append(ch);

                if (inString)
                {
                    if (escape)
                    {
                        escape = false;
                        continue;
                    }

                    if (ch == '\\')
                    {
                        escape = true;
                        continue;
                    }

                    if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                switch (ch)
                {
                    case '"':
                        inString = true;
                        break;
                    case '{':
                        stack.Push('}');
                        break;
                    case '[':
                        stack.Push(']');
                        break;
                    case '}':
                    case ']':
                        if (stack.Count > 0 && stack.Peek() == ch)
                        {
                            stack.Pop();
                        }
                        break;
                }
            }

            var repaired = builder.ToString().TrimEnd();
            if (inString)
            {
                repaired += "\"";
            }

            repaired = Regex.Replace(repaired, @",\s*$", string.Empty, RegexOptions.CultureInvariant);
            while (stack.Count > 0)
            {
                repaired = Regex.Replace(repaired, @",\s*$", string.Empty, RegexOptions.CultureInvariant);
                repaired += stack.Pop();
            }

            repaired = Regex.Replace(repaired, @",\s*([}\]])", "$1", RegexOptions.CultureInvariant);
            return repaired;
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
