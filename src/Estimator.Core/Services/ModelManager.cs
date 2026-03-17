using Estimator.Core.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Estimator.Core.Services
{
    public sealed class ModelManager
    {
        private static readonly HttpClient HttpClient = new();
        private readonly AiSettings _settings;
        private readonly ILogger<ModelManager> _logger;

        public ModelManager(IOptions<AiSettings> options, ILogger<ModelManager> logger)
        {
            _settings = options.Value;
            _logger = logger;
        }

        public async Task EnsureModelDownloadedAsync(CancellationToken cancellationToken = default)
        {
            var filePath = _settings.LocalModelPath;
            var directory = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                _logger.LogInformation("Creating model directory: {Directory}", directory);
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(filePath))
            {
                _logger.LogInformation("Model found: {Path}", filePath);
                return;
            }

            if (string.IsNullOrWhiteSpace(_settings.ModelUrl))
            {
                throw new InvalidOperationException("ModelUrl is required when local model file is missing.");
            }

            _logger.LogWarning("Model not found at {Path}. Downloading from {Url}.", filePath, _settings.ModelUrl);

            HttpClient.Timeout = TimeSpan.FromMinutes(Math.Max(5, _settings.DownloadTimeoutMinutes));
            try
            {
                using var response = await HttpClient.GetAsync(
                    _settings.ModelUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);

                var buffer = new byte[131072];
                long totalRead = 0;
                var lastLoggedProgress = -1;
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalRead += bytesRead;

                    if (totalBytes <= 0)
                    {
                        continue;
                    }

                    var progress = (int)((double)totalRead / totalBytes * 100);
                    if (progress >= lastLoggedProgress + 10)
                    {
                        _logger.LogInformation("Model download progress: {Progress}% ({ReadMb}MB/{TotalMb}MB)", progress, totalRead / 1024 / 1024, totalBytes / 1024 / 1024);
                        lastLoggedProgress = progress;
                    }
                }

                _logger.LogInformation("Model download completed: {Path}", filePath);
            }
            catch (OperationCanceledException)
            {
                CleanupPartialFile(filePath);
                throw;
            }
            catch (Exception ex)
            {
                CleanupPartialFile(filePath);
                _logger.LogCritical(ex, "Model download failed.");
                throw;
            }
        }

        private static void CleanupPartialFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
