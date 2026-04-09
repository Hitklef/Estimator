using System.Text;
using Estimator.Core.Agents;
using Estimator.Core.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Estimator.Core.Services
{
    public sealed class ModelOutputConsoleTracer
    {
        private readonly AiSettings _settings;

        public ModelOutputConsoleTracer(IOptions<AiSettings> options)
        {
            _settings = options.Value;
        }

        public StreamSession BeginStream(
            ILogger logger,
            string provider,
            AgentRole role,
            int attempt,
            int maxAttempts)
        {
            return new StreamSession(_settings, logger, provider, role, attempt, maxAttempts);
        }

        public void LogResponsePreview(
            ILogger logger,
            string provider,
            AgentRole role,
            string response)
        {
            if (!_settings.LogModelResponsePreview || string.IsNullOrWhiteSpace(response))
            {
                return;
            }

            var previewLength = Math.Max(80, _settings.LoggedModelPreviewCharacters);
            var preview = response.Length <= previewLength
                ? response
                : response[..previewLength] + "...";

            logger.LogInformation(
                "{Provider} final response preview. Role={Role} Preview={Preview}",
                provider,
                role,
                preview);
        }

        public sealed class StreamSession : IDisposable
        {
            private readonly AiSettings _settings;
            private readonly ILogger _logger;
            private readonly string _provider;
            private readonly AgentRole _role;
            private readonly int _attempt;
            private readonly int _maxAttempts;
            private readonly StringBuilder _buffer = new();
            private int _totalChars;

            public StreamSession(
                AiSettings settings,
                ILogger logger,
                string provider,
                AgentRole role,
                int attempt,
                int maxAttempts)
            {
                _settings = settings;
                _logger = logger;
                _provider = provider;
                _role = role;
                _attempt = attempt;
                _maxAttempts = maxAttempts;
            }

            public void OnChunk(string? chunk)
            {
                if (!_settings.EnableLiveModelConsoleOutput || string.IsNullOrEmpty(chunk))
                {
                    return;
                }

                _totalChars += chunk.Length;
                _buffer.Append(chunk);

                if (_buffer.Length >= Math.Max(40, _settings.LiveModelConsoleFlushCharacters) ||
                    chunk.Contains('\n'))
                {
                    Flush();
                }
            }

            public void Complete()
            {
                Flush();

                if (_settings.EnableLiveModelConsoleOutput)
                {
                    _logger.LogInformation(
                        "{Provider} live stream completed. Role={Role} Attempt={Attempt}/{MaxAttempts} StreamChars={StreamChars}",
                        _provider,
                        _role,
                        _attempt,
                        _maxAttempts,
                        _totalChars);
                }
            }

            public void Dispose()
            {
                Flush();
            }

            private void Flush()
            {
                if (_buffer.Length == 0)
                {
                    return;
                }

                _logger.LogInformation(
                    "{Provider} live output. Role={Role} Attempt={Attempt}/{MaxAttempts} Chunk={Chunk}",
                    _provider,
                    _role,
                    _attempt,
                    _maxAttempts,
                    _buffer.ToString());

                _buffer.Clear();
            }
        }
    }
}
