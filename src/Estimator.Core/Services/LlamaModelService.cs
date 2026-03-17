using System.Text;
using Estimator.Core.Agents;
using Estimator.Core.Models.Options;
using LLama;
using LLama.Common;
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
        private readonly StatelessExecutor _executor;
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
                var prompt = BuildPrompt(systemPrompt, userInput);
                var inference = CreateInferenceParams(profile);
                var builder = new StringBuilder(2048);

                await foreach (var text in _executor.InferAsync(prompt, inference).WithCancellation(timeoutCts.Token))
                {
                    builder.Append(text);
                }

                return builder.ToString().Trim();
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

        private static InferenceParams CreateInferenceParams(AgentRuntimeProfile profile) =>
            new()
            {
                MaxTokens = profile.MaxTokens,
                AntiPrompts = profile.AntiPrompts.ToArray(),
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = profile.Temperature,
                    TopP = profile.TopP
                }
            };

        private static string BuildPrompt(string systemPrompt, string userInput) =>
            $"<|im_start|>system\n{systemPrompt}<|im_end|>\n<|im_start|>user\n{userInput}<|im_end|>\n<|im_start|>assistant\n";

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
