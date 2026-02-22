using Estimator.Core.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Estimator.Core.Services
{
    public class ModelManager
    {
        private readonly AiSettings _settings;
        private readonly ILogger<ModelManager> _logger;

        public ModelManager(IOptions<AiSettings> options, ILogger<ModelManager> logger)
        {
            _settings = options.Value;
            _logger = logger;
        }

        public async Task EnsureModelDownloadedAsync()
        {
            var filePath = _settings.LocalModelPath;
            var directory = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                _logger.LogInformation("Creating directory for models: {Directory}", directory);
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(filePath))
            {
                _logger.LogInformation("Model file already exists: {Path}", filePath);
                return;
            }

            _logger.LogWarning("Model file not found at {Path}. Starting download from {Url}...", filePath, _settings.ModelUrl);

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMinutes(30);

                using var response = await client.GetAsync(_settings.ModelUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                long totalRead = 0;
                int read;
                int lastReportedProgress = -1;

                while ((read = await contentStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    totalRead += read;

                    if (totalBytes != -1)
                    {
                        var progress = (int)((double)totalRead / totalBytes * 100);

                        if (progress % 10 == 0 && progress != lastReportedProgress)
                        {
                            _logger.LogInformation("Download progress: {Progress}% ({(Read / 1024 / 1024):F0}MB)", progress, totalRead);
                            lastReportedProgress = progress;
                        }
                        Console.Write($"\r[AI] Downloading: {progress}% ({(totalRead / 1024 / 1024):F0}MB / {(totalBytes / 1024 / 1024):F0}MB)");
                    }
                }

                Console.WriteLine(); 
                _logger.LogInformation("Model download completed: {Path}", filePath);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Critical error during model download: {Message}", ex.Message);

                if (File.Exists(filePath)) 
                { 
                    File.Delete(filePath); 
                }
                    
                throw;
            }
        }
    }
}
