using Estimator.Core.Models.Options;
using LLama;
using LLama.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Estimator.Core.Services
{
    public class LlamaModelService : IDisposable
    {
        private readonly AiSettings _settings;
        private readonly ILogger<LlamaModelService> _logger;
        private readonly LLamaWeights _weights;
        private readonly StatelessExecutor _executor;
        private bool _disposed;

        public LlamaModelService(IOptions<AiSettings> options, ILogger<LlamaModelService> logger)
        {
            _settings = options.Value;
            _logger = logger;

            _logger.LogInformation("Initializing LLM Engine using model: {ModelPath}", _settings.LocalModelPath);

            try
            {
                var parameters = new ModelParams(_settings.LocalModelPath)
                {
                    ContextSize = _settings.ContextSize,
                    GpuLayerCount = _settings.GpuLayerCount,
                    MainGpu = 0,
                    UseMemoryLock = false,
                    UseMemorymap = true
                };

                _weights = LLamaWeights.LoadFromFile(parameters);

                _executor = new StatelessExecutor(_weights, parameters);

                _logger.LogInformation("LLM Engine successfully loaded into memory.");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to initialize LLM Engine: {Message}", ex.Message);
                throw;
            }
        }

        public StatelessExecutor GetExecutor()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _executor;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _logger.LogInformation("Shutting down LLM Engine and releasing resources...");
            _weights?.Dispose();
            _disposed = true;

            GC.SuppressFinalize(this);
        }
    }
}
