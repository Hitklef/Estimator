using System.Net.Http.Json;
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
        private readonly HttpClient _httpClient;

        public LlamaCppHttpModelService(
            IOptions<AiSettings> options,
            LlamaCppServerManager serverManager,
            ILogger<LlamaCppHttpModelService> logger)
        {
            _settings = options.Value;
            _serverManager = serverManager;
            _logger = logger;

            var baseUrl = $"http://{_settings.LlamaCppServerHost}:{_settings.LlamaCppServerPort}/";
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        public async Task<string> CompleteAsync(
            AgentRole role,
            string systemPrompt,
            string userInput,
            CancellationToken cancellationToken = default)
        {
            await _serverManager.EnsureStartedAsync(cancellationToken);

            var profile = _settings.ResolveProfile(role.ToString());
            var prompt = BuildPrompt(systemPrompt, userInput);
            var maxAttempts = Math.Max(1, _settings.LlamaCppMaxInferenceAttempts);
            var perAttemptTimeout = TimeSpan.FromSeconds(Math.Max(30, _settings.LlamaCppPerAttemptTimeoutSeconds));
            Exception? lastTimeoutException = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var maxTokens = attempt switch
                {
                    1 => profile.MaxTokens,
                    2 => Math.Max(256, profile.MaxTokens / 2),
                    _ => Math.Max(128, profile.MaxTokens / (attempt + 1))
                };

                var request = new CompletionRequest
                {
                    Prompt = prompt,
                    NPredict = maxTokens,
                    Temperature = profile.Temperature,
                    TopP = profile.TopP,
                    Stop = profile.AntiPrompts
                };

                HttpResponseMessage response;
                using var attemptTimeoutCts = new CancellationTokenSource(perAttemptTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    attemptTimeoutCts.Token);

                try
                {
                    response = await _httpClient.PostAsJsonAsync("completion", request, linkedCts.Token);
                }
                catch (OperationCanceledException ex)
                    when (attemptTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    lastTimeoutException = ex;
                    _logger.LogWarning(
                        ex,
                        "llama-server completion timed out for role {Role} (attempt {Attempt}/{MaxAttempts}, n_predict={MaxTokens}, timeout={TimeoutSeconds}s).",
                        role,
                        attempt,
                        maxAttempts,
                        maxTokens,
                        perAttemptTimeout.TotalSeconds);

                    if (attempt < maxAttempts)
                    {
                        await TryRestartServerAfterTimeoutAsync(cancellationToken);
                        continue;
                    }

                    throw ModelInferenceTimeoutException.CreateDefault(ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new InvalidOperationException(
                        "Failed to call embedded llama-server. Ensure the bundled runtime is available.",
                        ex);
                }

                var body = await response.Content.ReadAsStringAsync(linkedCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    if (IsModelCapacityMessage(body))
                    {
                        throw ModelCapacityException.CreateDefault();
                    }

                    if (IsTimeoutMessage(body))
                    {
                        _logger.LogWarning(
                            "llama-server returned timeout-like response for role {Role} (attempt {Attempt}/{MaxAttempts}).",
                            role,
                            attempt,
                            maxAttempts);

                        if (attempt < maxAttempts)
                        {
                            await TryRestartServerAfterTimeoutAsync(cancellationToken);
                            continue;
                        }

                        throw ModelInferenceTimeoutException.CreateDefault();
                    }

                    throw new InvalidOperationException(
                        $"Embedded llama-server returned {(int)response.StatusCode}: {body}");
                }

                CompletionResponse? parsed;
                try
                {
                    parsed = System.Text.Json.JsonSerializer.Deserialize<CompletionResponse>(body);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Embedded llama-server response is invalid JSON.", ex);
                }

                var content = parsed?.Content?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return content;
                }

                _logger.LogWarning(
                    "llama-server returned empty content for role {Role} (attempt {Attempt}/{MaxAttempts}, n_predict={MaxTokens}).",
                    role,
                    attempt,
                    maxAttempts,
                    maxTokens);
            }

            throw ModelInferenceTimeoutException.CreateDefault(lastTimeoutException);
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

        private static bool IsModelCapacityMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.Contains("context", StringComparison.OrdinalIgnoreCase) ||
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

        private static string BuildPrompt(string systemPrompt, string userInput)
        {
            var system = systemPrompt?.Trim() ?? string.Empty;
            var input = userInput?.Trim() ?? string.Empty;
            return $"<bos><|turn>system\n{system}<turn|>\n<|turn>user\n{input}<turn|>\n<|turn>model\n";
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
        }

        private sealed class CompletionResponse
        {
            [JsonPropertyName("content")]
            public string? Content { get; set; }
        }
    }
}
