using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Estimator.Core.Agents;
using Estimator.Core.Models;
using Estimator.Core.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Estimator.Core.Services
{
    public sealed class LlamaCppHttpModelService : IAgentModelGateway
    {
        private readonly AiSettings _settings;
        private readonly LlamaCppServerManager _serverManager;
        private readonly ILogger<LlamaCppHttpModelService> _logger;
        private readonly ModelPromptFormatter _promptFormatter;
        private readonly ModelOutputConsoleTracer _outputTracer;
        private readonly HttpClient _httpClient;
        private volatile bool _preferLegacyCompletionEndpoint;

        public LlamaCppHttpModelService(
            IOptions<AiSettings> options,
            LlamaCppServerManager serverManager,
            ILogger<LlamaCppHttpModelService> logger,
            ModelPromptFormatter promptFormatter,
            ModelOutputConsoleTracer outputTracer)
        {
            _settings = options.Value;
            _serverManager = serverManager;
            _logger = logger;
            _promptFormatter = promptFormatter;
            _outputTracer = outputTracer;

            var baseUrl = $"http://{_settings.LlamaCppServerHost}:{_settings.LlamaCppServerPort}/";
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        public async Task<string> CompleteAsync(
            ModelCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            await _serverManager.EnsureStartedAsync(cancellationToken);

            var profile = _settings.ResolveProfile(request.Role.ToString());
            var maxAttempts = Math.Max(1, _settings.LlamaCppMaxInferenceAttempts);
            var perAttemptTimeout = _settings.LlamaCppPerAttemptTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(_settings.LlamaCppPerAttemptTimeoutSeconds)
                : (TimeSpan?)null;
            var safetyTimeout = _settings.LlamaCppSafetyTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(_settings.LlamaCppSafetyTimeoutSeconds)
                : (TimeSpan?)null;
            var normalizedSystemPrompt = request.SystemPrompt ?? string.Empty;
            var attemptUserInput = request.UserInput ?? string.Empty;
            Exception? lastTimeoutException = null;

            if (perAttemptTimeout is null && safetyTimeout is null)
            {
                _logger.LogWarning(
                    "All model timeouts are disabled. Requests may run indefinitely. Role={Role}",
                    request.Role);
            }

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var maxTokens = attempt switch
                {
                    1 => profile.MaxTokens,
                    2 => Math.Max(256, profile.MaxTokens / 2),
                    _ => Math.Max(128, profile.MaxTokens / (attempt + 1))
                };

                using var attemptTimeoutCts = perAttemptTimeout.HasValue
                    ? new CancellationTokenSource(perAttemptTimeout.Value)
                    : null;
                using var safetyTimeoutCts = safetyTimeout.HasValue
                    ? new CancellationTokenSource(safetyTimeout.Value)
                    : null;
                using var linkedCts = attemptTimeoutCts is null
                    ? (safetyTimeoutCts is null
                        ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                        : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, safetyTimeoutCts.Token))
                    : (safetyTimeoutCts is null
                        ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptTimeoutCts.Token)
                        : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptTimeoutCts.Token, safetyTimeoutCts.Token));

                var attemptStopwatch = Stopwatch.StartNew();
                _logger.LogInformation(
                    "LLM attempt started. Role={Role} Attempt={Attempt}/{MaxAttempts} MaxTokens={MaxTokens} InputChars={InputChars} SystemChars={SystemChars} EndpointPreference={EndpointPreference} PerAttemptTimeoutSec={PerAttemptTimeoutSec} SafetyTimeoutSec={SafetyTimeoutSec}",
                    request.Role,
                    attempt,
                    maxAttempts,
                    maxTokens,
                    attemptUserInput.Length,
                    normalizedSystemPrompt.Length,
                    _preferLegacyCompletionEndpoint ? "legacy" : "chat",
                    perAttemptTimeout?.TotalSeconds,
                    safetyTimeout?.TotalSeconds);

                try
                {
                    using var trace = _outputTracer.BeginStream(
                        _logger,
                        "llama.cpp",
                        request.Role,
                        attempt,
                        maxAttempts);

                    var content = await CompleteSingleAttemptAsync(
                        request,
                        normalizedSystemPrompt,
                        attemptUserInput,
                        profile,
                        maxTokens,
                        trace,
                        linkedCts.Token);

                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        trace.Complete();
                        _outputTracer.LogResponsePreview(_logger, "llama.cpp", request.Role, content);
                        attemptStopwatch.Stop();
                        _logger.LogInformation(
                            "LLM attempt completed successfully. Role={Role} Attempt={Attempt}/{MaxAttempts} OutputChars={OutputChars} DurationMs={DurationMs}",
                            request.Role,
                            attempt,
                            maxAttempts,
                            content.Length,
                            attemptStopwatch.ElapsedMilliseconds);
                        return content;
                    }

                    _logger.LogWarning(
                        "llama-server returned empty content for role {Role} (attempt {Attempt}/{MaxAttempts}, n_predict={MaxTokens}).",
                        request.Role,
                        attempt,
                        maxAttempts,
                        maxTokens);

                    if (attempt < maxAttempts)
                    {
                        attemptUserInput = ReduceInputForRetry(attemptUserInput, request.Role, attempt + 1);
                    }
                }
                catch (OperationCanceledException ex)
                    when (attemptTimeoutCts is not null &&
                          attemptTimeoutCts.IsCancellationRequested &&
                          !cancellationToken.IsCancellationRequested)
                {
                    lastTimeoutException = ex;
                    _logger.LogWarning(
                        ex,
                        "llama-server completion timed out for role {Role} (attempt {Attempt}/{MaxAttempts}, n_predict={MaxTokens}, timeout={TimeoutSeconds}s).",
                        request.Role,
                        attempt,
                        maxAttempts,
                        maxTokens,
                        perAttemptTimeout?.TotalSeconds);

                    if (attempt < maxAttempts)
                    {
                        attemptUserInput = ReduceInputForRetry(attemptUserInput, request.Role, attempt + 1);
                        await TryRestartServerAfterTimeoutAsync(cancellationToken);
                        continue;
                    }

                    throw ModelInferenceTimeoutException.CreateDefault(ex);
                }
                catch (OperationCanceledException ex)
                    when (safetyTimeoutCts is not null &&
                          safetyTimeoutCts.IsCancellationRequested &&
                          !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(
                        ex,
                        "LLM safety timeout triggered. Role={Role} Attempt={Attempt}/{MaxAttempts} SafetyTimeoutSec={SafetyTimeoutSec} InputChars={InputChars}",
                        request.Role,
                        attempt,
                        maxAttempts,
                        safetyTimeout?.TotalSeconds,
                        attemptUserInput.Length);

                    throw new ModelInferenceTimeoutException(
                        "Model processing exceeded safety limit. Please simplify input or switch to a smaller/faster model.",
                        ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new InvalidOperationException(
                        "Failed to call embedded llama-server. Ensure the bundled runtime is available.",
                        ex);
                }
            }

            throw ModelInferenceTimeoutException.CreateDefault(lastTimeoutException);
        }

        private string ReduceInputForRetry(string input, AgentRole role, int nextAttempt)
        {
            if (string.IsNullOrWhiteSpace(input) || input.Length <= 2500)
            {
                return input;
            }

            var targetLength = Math.Max(2500, (int)(input.Length * 0.65));
            var headLength = Math.Max(1, (int)(targetLength * 0.8));
            var tailLength = Math.Max(0, targetLength - headLength);

            var head = input[..Math.Min(headLength, input.Length)];
            var tail = tailLength == 0
                ? string.Empty
                : input[^Math.Min(tailLength, input.Length)..];

            var reduced = $"{head}\n\n[Context was reduced after timeout for retry stability.]\n\n{tail}".Trim();

            _logger.LogWarning(
                "Reducing input size for role {Role} before attempt {Attempt}: {FromChars} -> {ToChars} characters.",
                role,
                nextAttempt,
                input.Length,
                reduced.Length);

            return reduced;
        }

        private async Task<string> CompleteSingleAttemptAsync(
            ModelCompletionRequest request,
            string systemPrompt,
            string userInput,
            AgentRuntimeProfile profile,
            int maxTokens,
            ModelOutputConsoleTracer.StreamSession trace,
            CancellationToken cancellationToken)
        {
            if (_preferLegacyCompletionEndpoint)
            {
                _logger.LogDebug("Using legacy completion endpoint because fallback mode is enabled.");
                return await CompleteUsingLegacyEndpointAsync(
                    request,
                    profile,
                    maxTokens,
                    trace,
                    cancellationToken);
            }

            var chatRequest = new ChatCompletionRequest
            {
                Model = "local-model",
                Messages =
                [
                    new ChatMessage { Role = "system", Content = systemPrompt.Trim() },
                    new ChatMessage { Role = "user", Content = userInput.Trim() }
                ],
                MaxTokens = maxTokens,
                Temperature = profile.Temperature,
                TopP = profile.TopP,
                Stop = profile.AntiPrompts,
                Stream = true
            };

            using var chatResponse = await PostJsonForStreamAsync(
                "v1/chat/completions",
                chatRequest,
                cancellationToken);

            var chatBody = chatResponse.IsSuccessStatusCode
                ? string.Empty
                : await chatResponse.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogDebug(
                "llama-server chat response status: {StatusCode}, body length: {BodyLength}",
                (int)chatResponse.StatusCode,
                chatBody.Length);

            if (!chatResponse.IsSuccessStatusCode)
            {
                if (ShouldFallbackToLegacy(chatResponse.StatusCode, chatBody))
                {
                    _preferLegacyCompletionEndpoint = true;
                    _logger.LogInformation(
                        "llama-server chat completions endpoint is unavailable for current model/runtime. Falling back to legacy completion endpoint.");

                    return await CompleteUsingLegacyEndpointAsync(
                        request,
                        profile,
                        maxTokens,
                        trace,
                        cancellationToken);
                }

                ThrowForFailedStatus(chatResponse.StatusCode, chatBody);
            }

            var content = await ReadChatStreamAsync(chatResponse, trace, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            _logger.LogWarning(
                "llama-server chat endpoint returned empty content. Falling back to legacy completion endpoint for this attempt.");

            return await CompleteUsingLegacyEndpointAsync(
                request,
                profile,
                maxTokens,
                trace,
                cancellationToken);
        }

        private async Task<string> CompleteUsingLegacyEndpointAsync(
            ModelCompletionRequest completionRequest,
            AgentRuntimeProfile profile,
            int maxTokens,
            ModelOutputConsoleTracer.StreamSession trace,
            CancellationToken cancellationToken)
        {
            var prompt = _promptFormatter.BuildPrompt(completionRequest.SystemPrompt, completionRequest.UserInput);
            var payload = new CompletionRequest
            {
                Prompt = prompt,
                NPredict = maxTokens,
                Temperature = profile.Temperature,
                TopP = profile.TopP,
                Stop = profile.AntiPrompts,
                Stream = true
            };

            using var response = await PostJsonForStreamAsync("completion", payload, cancellationToken);
            var body = response.IsSuccessStatusCode
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug(
                "llama-server legacy response status: {StatusCode}, body length: {BodyLength}",
                (int)response.StatusCode,
                body.Length);

            if (!response.IsSuccessStatusCode)
            {
                ThrowForFailedStatus(response.StatusCode, body);
            }

            return await ReadLegacyCompletionStreamAsync(response, trace, cancellationToken);
        }

        private async Task TryRestartServerAfterTimeoutAsync(CancellationToken cancellationToken)
        {
            if (!_settings.LlamaCppRestartServerOnTimeout)
            {
                return;
            }

            try
            {
                _serverManager.Stop();
                await _serverManager.EnsureStartedAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "llama-server restart attempt after timeout failed.");
            }
        }

        private static void ThrowForFailedStatus(HttpStatusCode statusCode, string body)
        {
            if (IsModelCapacityMessage(body))
            {
                throw ModelCapacityException.CreateDefault();
            }

            if (IsTimeoutMessage(body))
            {
                throw ModelInferenceTimeoutException.CreateDefault();
            }

            throw new InvalidOperationException(
                $"Embedded llama-server returned {(int)statusCode}: {body}");
        }

        private static bool ShouldFallbackToLegacy(HttpStatusCode statusCode, string body)
        {
            if (statusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return true;
            }

            if (statusCode != HttpStatusCode.BadRequest)
            {
                return false;
            }

            return body.Contains("chat", StringComparison.OrdinalIgnoreCase) ||
                   body.Contains("messages", StringComparison.OrdinalIgnoreCase) ||
                   body.Contains("template", StringComparison.OrdinalIgnoreCase) ||
                   body.Contains("unsupported", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsModelCapacityMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.Contains("context", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("n_ctx", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("NoKvSlot", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("too long", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("out of memory", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTimeoutMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("timed out", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<HttpResponseMessage> PostJsonForStreamAsync(
            string endpoint,
            object payload,
            CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload)
            };

            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }

        private static async Task<string> ReadChatStreamAsync(
            HttpResponseMessage response,
            ModelOutputConsoleTracer.StreamSession trace,
            CancellationToken cancellationToken)
        {
            var builder = new StringBuilder(1024);

            await foreach (var payload in ReadSsePayloadsAsync(response, cancellationToken))
            {
                var chunk = ExtractChatChunk(payload);
                if (string.IsNullOrEmpty(chunk))
                {
                    continue;
                }

                builder.Append(chunk);
                trace.OnChunk(chunk);
            }

            return builder.ToString().Trim();
        }

        private static async Task<string> ReadLegacyCompletionStreamAsync(
            HttpResponseMessage response,
            ModelOutputConsoleTracer.StreamSession trace,
            CancellationToken cancellationToken)
        {
            var builder = new StringBuilder(1024);

            await foreach (var payload in ReadSsePayloadsAsync(response, cancellationToken))
            {
                var chunk = ExtractLegacyChunk(payload);
                if (string.IsNullOrEmpty(chunk))
                {
                    continue;
                }

                builder.Append(chunk);
                trace.OnChunk(chunk);
            }

            return builder.ToString().Trim();
        }

        private static async IAsyncEnumerable<string> ReadSsePayloadsAsync(
            HttpResponseMessage response,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    yield break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var payload = line["data:".Length..].Trim();
                if (payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    yield break;
                }

                yield return payload;
            }
        }

        private static string ExtractChatChunk(string payload)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                {
                    return string.Empty;
                }

                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var deltaContent))
                {
                    return deltaContent.GetString() ?? string.Empty;
                }

                if (choice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var messageContent))
                {
                    return messageContent.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
            }

            return string.Empty;
        }

        private static string ExtractLegacyChunk(string payload)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;

                if (root.TryGetProperty("content", out var content))
                {
                    return content.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
            }

            return string.Empty;
        }

        private sealed class ChatCompletionRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = "local-model";

            [JsonPropertyName("messages")]
            public List<ChatMessage> Messages { get; set; } = new();

            [JsonPropertyName("max_tokens")]
            public int MaxTokens { get; set; }

            [JsonPropertyName("temperature")]
            public float Temperature { get; set; }

            [JsonPropertyName("top_p")]
            public float TopP { get; set; }

            [JsonPropertyName("stop")]
            public List<string> Stop { get; set; } = new();

            [JsonPropertyName("stream")]
            public bool Stream { get; set; }
        }

        private sealed class ChatMessage
        {
            [JsonPropertyName("role")]
            public string Role { get; set; } = string.Empty;

            [JsonPropertyName("content")]
            public string Content { get; set; } = string.Empty;
        }

        private sealed class ChatCompletionResponse
        {
            [JsonPropertyName("choices")]
            public List<ChatChoice> Choices { get; set; } = new();
        }

        private sealed class ChatChoice
        {
            [JsonPropertyName("message")]
            public ChatMessage? Message { get; set; }
        }

        private sealed class CompletionRequest
        {
            [JsonPropertyName("prompt")]
            public string Prompt { get; set; } = string.Empty;

            [JsonPropertyName("n_predict")]
            public int NPredict { get; set; }

            [JsonPropertyName("temperature")]
            public float Temperature { get; set; }

            [JsonPropertyName("top_p")]
            public float TopP { get; set; }

            [JsonPropertyName("stop")]
            public List<string> Stop { get; set; } = new();

            [JsonPropertyName("stream")]
            public bool Stream { get; set; }
        }

        private sealed class CompletionResponse
        {
            [JsonPropertyName("content")]
            public string? Content { get; set; }
        }
    }
}
