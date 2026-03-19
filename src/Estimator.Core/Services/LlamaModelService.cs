using System.Text;
using Estimator.Core.Agents;
using Estimator.Core.Models;
using Estimator.Core.Models.Options;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Estimator.Core.Services
{
    public sealed class LlamaModelService : IAgentModelGateway, IDisposable
    {
        private readonly AiSettings _settings;
        private readonly ILogger<LlamaModelService> _logger;
        private readonly LLamaWeights _weights;
        private readonly ModelParams _modelParams;
        private StatelessExecutor _executor;
        private readonly SemaphoreSlim _inferenceLock = new(1, 1);
        private bool _disposed;

        public LlamaModelService(IOptions<AiSettings> options, ILogger<LlamaModelService> logger)
        {
            _settings = options.Value;
            _logger = logger;

            _logger.LogInformation("Initializing LLM engine. Model: {ModelPath}", _settings.LocalModelPath);

            _modelParams = new ModelParams(_settings.LocalModelPath)
            {
                ContextSize = _settings.ContextSize,
                GpuLayerCount = _settings.GpuLayerCount,
                MainGpu = 0,
                UseMemoryLock = false,
                UseMemorymap = true
            };

            try
            {
                _weights = LLamaWeights.LoadFromFile(_modelParams);
                _executor = new StatelessExecutor(_weights, _modelParams);
                _logger.LogInformation("LLM engine loaded successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to initialize LLM engine.");
                throw;
            }
        }

        public async Task<string> CompleteAsync(
            AgentRole role,
            string systemPrompt,
            string userInput,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _settings.AgentInferenceTimeoutSeconds)));

            await _inferenceLock.WaitAsync(timeoutCts.Token);
            try
            {
                var profile = ResolveProfile(role);
                var prompt = BuildPrompt(systemPrompt, PrepareInput(userInput, _settings.MaxPromptCharacters));
                var maxTokens = ResolveMaxTokens(prompt, profile.MaxTokens);
                if (maxTokens <= 0)
                {
                    throw ModelCapacityException.CreateDefault();
                }

                try
                {
                    return await InferInternalAsync(prompt, CreateInferenceParams(profile, maxTokens), timeoutCts.Token);
                }
                catch (LLamaDecodeError ex) when (IsNoKvSlot(ex))
                {
                    _logger.LogWarning(ex, "Encountered NoKvSlot for role {Role}. Retrying with a fresh executor and tighter prompt budget.", role);
                    RecreateExecutor();

                    var retryPrompt = BuildPrompt(systemPrompt, PrepareInput(userInput, _settings.RetryMaxPromptCharacters));
                    var retryMaxTokens = ResolveMaxTokens(retryPrompt, Math.Min(profile.MaxTokens, 512));
                    if (retryMaxTokens <= 0)
                    {
                        throw ModelCapacityException.CreateDefault(ex);
                    }

                    try
                    {
                        return await InferInternalAsync(retryPrompt, CreateInferenceParams(profile, retryMaxTokens), timeoutCts.Token);
                    }
                    catch (LLamaDecodeError retryEx) when (IsNoKvSlot(retryEx))
                    {
                        throw ModelCapacityException.CreateDefault(retryEx);
                    }
                }
                catch (Exception ex) when (IsNoKvSlotMessage(ex.Message))
                {
                    throw ModelCapacityException.CreateDefault(ex);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Inference timed out for role {Role} after {TimeoutSeconds}s. Returning empty response for fallback handling.",
                    role,
                    _settings.AgentInferenceTimeoutSeconds);
                return string.Empty;
            }
            finally
            {
                _inferenceLock.Release();
            }
        }

        private AgentRuntimeProfile ResolveProfile(AgentRole role)
        {
            return _settings.ResolveProfile(role.ToString());
        }

        private static InferenceParams CreateInferenceParams(AgentRuntimeProfile profile, int maxTokens) =>
            new()
            {
                MaxTokens = maxTokens,
                AntiPrompts = profile.AntiPrompts.ToArray(),
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = profile.Temperature,
                    TopP = profile.TopP
                }
            };

        private static string BuildPrompt(string systemPrompt, string userInput) =>
            $"<|im_start|>system\n{systemPrompt}<|im_end|>\n<|im_start|>user\n{userInput}<|im_end|>\n<|im_start|>assistant\n";

        private async Task<string> InferInternalAsync(string prompt, InferenceParams inference, CancellationToken cancellationToken)
        {
            var builder = new StringBuilder(2048);
            await foreach (var text in _executor.InferAsync(prompt, inference).WithCancellation(cancellationToken))
            {
                builder.Append(text);
            }

            return builder.ToString().Trim();
        }

        private int ResolveMaxTokens(string prompt, int requestedMaxTokens)
        {
            var contextSize = Math.Max(512, (int)_settings.ContextSize);
            var safetyMargin = Math.Max(64, _settings.ContextSafetyMarginTokens);
            var minimumGenerationTokens = Math.Max(64, _settings.MinimumGenerationTokens);
            var estimatedPromptTokens = EstimateTokens(prompt);
            var availableForGeneration = contextSize - estimatedPromptTokens - safetyMargin;
            if (availableForGeneration < minimumGenerationTokens)
            {
                return 0;
            }

            return Math.Max(64, Math.Min(requestedMaxTokens, availableForGeneration));
        }

        private static int EstimateTokens(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return 0;
            }

            return (int)Math.Ceiling(content.Length / 4d);
        }

        private static string PrepareInput(string input, int maxCharacters)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var normalized = input
                .Replace("\0", string.Empty, StringComparison.Ordinal)
                .Trim();

            maxCharacters = Math.Max(1000, maxCharacters);
            if (normalized.Length <= maxCharacters)
            {
                return normalized;
            }

            const string marker = "\n\n[... input truncated for model context ...]\n\n";
            if (maxCharacters <= marker.Length + 200)
            {
                return normalized[..maxCharacters];
            }

            var headLength = (int)(maxCharacters * 0.75);
            var tailLength = Math.Max(80, maxCharacters - headLength - marker.Length);
            var head = normalized[..Math.Min(headLength, normalized.Length)];
            var tailStart = Math.Max(0, normalized.Length - tailLength);
            var tail = normalized[tailStart..];
            return $"{head}{marker}{tail}";
        }

        private static bool IsNoKvSlot(LLamaDecodeError exception) =>
            exception.Message.Contains("NoKvSlot", StringComparison.OrdinalIgnoreCase);

        private static bool IsNoKvSlotMessage(string? message) =>
            !string.IsNullOrWhiteSpace(message) &&
            message.Contains("NoKvSlot", StringComparison.OrdinalIgnoreCase);

        private void RecreateExecutor()
        {
            _executor = new StatelessExecutor(_weights, _modelParams);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _logger.LogInformation("Releasing LLM resources.");
            _weights.Dispose();
            _inferenceLock.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
