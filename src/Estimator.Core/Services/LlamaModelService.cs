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
        private readonly StatelessExecutor _executor;
        private readonly ModelPromptFormatter _promptFormatter;
        private readonly ModelOutputConsoleTracer _outputTracer;
        private readonly SemaphoreSlim _inferenceLock = new(1, 1);
        private bool _disposed;

        public LlamaModelService(
            IOptions<AiSettings> options,
            ILogger<LlamaModelService> logger,
            ModelPromptFormatter promptFormatter,
            ModelOutputConsoleTracer outputTracer)
        {
            _settings = options.Value;
            _logger = logger;
            _promptFormatter = promptFormatter;
            _outputTracer = outputTracer;

            _logger.LogInformation("Initializing LLM engine. Model: {ModelPath}", _settings.LocalModelPath);

            Exception? lastRetryableError = null;
            foreach (var gpuLayerCandidate in BuildGpuLayerCandidates(_settings.GpuLayerCount))
            {
                var candidateParams = new ModelParams(_settings.LocalModelPath)
                {
                    ContextSize = _settings.ContextSize,
                    GpuLayerCount = gpuLayerCandidate,
                    MainGpu = 0,
                    UseMemoryLock = false,
                    UseMemorymap = true
                };

                try
                {
                    _weights = LLamaWeights.LoadFromFile(candidateParams);
                    _modelParams = candidateParams;
                    _executor = new StatelessExecutor(_weights, _modelParams);

                    _logger.LogInformation(
                        "LLM engine loaded successfully. ContextSize={ContextSize}, GpuLayerCount={GpuLayers}",
                        _settings.ContextSize,
                        gpuLayerCandidate);

                    return;
                }
                catch (Exception ex) when (ShouldRetryWithFewerGpuLayers(ex) && gpuLayerCandidate > 0)
                {
                    lastRetryableError = ex;
                    _logger.LogWarning(
                        ex,
                        "Model load failed with GpuLayerCount={GpuLayers}. Retrying with fewer GPU layers.",
                        gpuLayerCandidate);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Failed to initialize LLM engine.");
                    throw;
                }
            }

            _logger.LogCritical(lastRetryableError, "Failed to initialize LLM engine after GPU-layer fallback attempts.");
            throw new InvalidOperationException("Failed to initialize Gemma 4 model with current GPU/CPU memory settings.", lastRetryableError);
        }

        public async Task<string> CompleteAsync(
            ModelCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _settings.AgentInferenceTimeoutSeconds)));

            await _inferenceLock.WaitAsync(timeoutCts.Token);
            try
            {
                var profile = _settings.ResolveProfile(request.Role.ToString());
                var prompt = _promptFormatter.BuildPrompt(request.SystemPrompt, request.UserInput);
                var inference = CreateInferenceParams(profile);

                using var trace = _outputTracer.BeginStream(
                    _logger,
                    "LLamaSharp",
                    request.Role,
                    1,
                    1);

                var result = await InferInternalAsync(prompt, inference, trace, timeoutCts.Token);
                trace.Complete();
                _outputTracer.LogResponsePreview(_logger, "LLamaSharp", request.Role, result);
                return result;
            }
            catch (LLamaDecodeError ex) when (IsNoKvSlot(ex))
            {
                throw ModelCapacityException.CreateDefault(ex);
            }
            catch (Exception ex) when (IsNoKvSlotMessage(ex.Message))
            {
                throw ModelCapacityException.CreateDefault(ex);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Inference timed out for role {Role} after {TimeoutSeconds}s.",
                    request.Role,
                    _settings.AgentInferenceTimeoutSeconds);
                return string.Empty;
            }
            finally
            {
                _inferenceLock.Release();
            }
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

        private async Task<string> InferInternalAsync(
            string prompt,
            InferenceParams inference,
            ModelOutputConsoleTracer.StreamSession trace,
            CancellationToken cancellationToken)
        {
            var builder = new StringBuilder(2048);
            await foreach (var text in _executor.InferAsync(prompt, inference).WithCancellation(cancellationToken))
            {
                builder.Append(text);
                trace.OnChunk(text);
            }

            return builder.ToString().Trim();
        }

        private static IEnumerable<int> BuildGpuLayerCandidates(int requestedLayers)
        {
            var layers = Math.Max(0, requestedLayers);
            yield return layers;

            for (var candidate = layers - 4; candidate > 0; candidate -= 4)
            {
                yield return candidate;
            }

            if (layers != 0)
            {
                yield return 0;
            }
        }

        private static bool ShouldRetryWithFewerGpuLayers(Exception exception)
        {
            var message = exception.ToString();
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.Contains("cuda", StringComparison.OrdinalIgnoreCase) &&
                   (message.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("not enough memory", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("alloc", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsNoKvSlot(LLamaDecodeError exception) =>
            exception.Message.Contains("NoKvSlot", StringComparison.OrdinalIgnoreCase);

        private static bool IsNoKvSlotMessage(string? message) =>
            !string.IsNullOrWhiteSpace(message) &&
            message.Contains("NoKvSlot", StringComparison.OrdinalIgnoreCase);

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
