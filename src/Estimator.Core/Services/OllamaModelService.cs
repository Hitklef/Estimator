using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Estimator.Core.Agents;
using Estimator.Core.Models;
using Estimator.Core.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Estimator.Core.Services
{
    public sealed class OllamaModelService : IAgentModelGateway
    {
        private readonly AiSettings _settings;
        private readonly ILogger<OllamaModelService> _logger;
        private readonly HttpClient _httpClient;
        private readonly ModelOutputConsoleTracer _outputTracer;

        public OllamaModelService(
            IOptions<AiSettings> options,
            ILogger<OllamaModelService> logger,
            ModelOutputConsoleTracer outputTracer)
        {
            _settings = options.Value;
            _logger = logger;
            _outputTracer = outputTracer;

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_settings.ResolveOllamaBaseUrl()),
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        public async Task<string> CompleteAsync(
            ModelCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _settings.AgentInferenceTimeoutSeconds)));

            var profile = _settings.ResolveProfile(request.Role.ToString());
            using var trace = _outputTracer.BeginStream(_logger, "Ollama", request.Role, 1, 1);
            var payload = new OllamaChatRequest
            {
                Model = _settings.OllamaModelName,
                Stream = false,
                KeepAlive = string.IsNullOrWhiteSpace(_settings.OllamaKeepAlive)
                    ? null
                    : _settings.OllamaKeepAlive,
                Think = request.EnableThinking,
                Format = BuildFormatPayload(request),
                Messages =
                [
                    new OllamaMessage { Role = "system", Content = request.SystemPrompt.Trim() },
                    new OllamaMessage { Role = "user", Content = request.UserInput.Trim() }
                ],
                Options = new OllamaOptions
                {
                    Temperature = profile.Temperature,
                    TopP = profile.TopP,
                    NumPredict = profile.MaxTokens,
                    NumCtx = (int)_settings.ContextSize,
                    Stop = profile.AntiPrompts
                }
            };

            using var response = await _httpClient.PostAsJsonAsync("chat", payload, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                ThrowForFailedStatus(body);
            }

            OllamaChatResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<OllamaChatResponse>(body);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Ollama returned invalid JSON.", ex);
            }

            var content = parsed?.Message?.Content?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(content))
            {
                trace.OnChunk(content);
                trace.Complete();
                _outputTracer.LogResponsePreview(_logger, "Ollama", request.Role, content);
                return content;
            }

            _logger.LogWarning(
                "Ollama returned an empty response body for role {Role}.",
                request.Role);

            return string.Empty;
        }

        private static object? BuildFormatPayload(ModelCompletionRequest request)
        {
            if (request.ResponseFormat != ModelResponseFormat.Json)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(request.JsonSchema))
            {
                return "json";
            }

            using var document = JsonDocument.Parse(request.JsonSchema);
            return document.RootElement.Clone();
        }

        private static void ThrowForFailedStatus(string body)
        {
            if (IsModelCapacityMessage(body))
            {
                throw ModelCapacityException.CreateDefault();
            }

            if (IsTimeoutMessage(body))
            {
                throw ModelInferenceTimeoutException.CreateDefault();
            }

            throw new InvalidOperationException($"Ollama request failed: {body}");
        }

        private static bool IsModelCapacityMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.Contains("context", StringComparison.OrdinalIgnoreCase) ||
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

        private sealed class OllamaChatRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = string.Empty;

            [JsonPropertyName("messages")]
            public List<OllamaMessage> Messages { get; set; } = new();

            [JsonPropertyName("stream")]
            public bool Stream { get; set; }

            [JsonPropertyName("format")]
            public object? Format { get; set; }

            [JsonPropertyName("keep_alive")]
            public string? KeepAlive { get; set; }

            [JsonPropertyName("think")]
            public bool Think { get; set; }

            [JsonPropertyName("options")]
            public OllamaOptions Options { get; set; } = new();
        }

        private sealed class OllamaMessage
        {
            [JsonPropertyName("role")]
            public string Role { get; set; } = string.Empty;

            [JsonPropertyName("content")]
            public string Content { get; set; } = string.Empty;
        }

        private sealed class OllamaOptions
        {
            [JsonPropertyName("temperature")]
            public float Temperature { get; set; }

            [JsonPropertyName("top_p")]
            public float TopP { get; set; }

            [JsonPropertyName("num_predict")]
            public int NumPredict { get; set; }

            [JsonPropertyName("num_ctx")]
            public int NumCtx { get; set; }

            [JsonPropertyName("stop")]
            public List<string> Stop { get; set; } = new();
        }

        private sealed class OllamaChatResponse
        {
            [JsonPropertyName("message")]
            public OllamaMessage? Message { get; set; }
        }
    }
}
