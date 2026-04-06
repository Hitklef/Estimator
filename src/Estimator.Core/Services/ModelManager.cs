using System.IO.Compression;
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
        private readonly LlamaCppServerManager? _llamaCppServerManager;

        public ModelManager(
            IOptions<AiSettings> options,
            ILogger<ModelManager> logger,
            LlamaCppServerManager? llamaCppServerManager = null)
        {
            _settings = options.Value;
            _logger = logger;
            _llamaCppServerManager = llamaCppServerManager;
        }

        public async Task EnsureModelDownloadedAsync(CancellationToken cancellationToken = default)
        {
            await EnsureLocalModelAvailableAsync(cancellationToken);

            if (_settings.UseLlamaCppServer)
            {
                await EnsureLlamaCppRuntimeAvailableAsync(cancellationToken);

                if (_llamaCppServerManager is null)
                {
                    throw new InvalidOperationException("LlamaCppServerManager is not registered.");
                }

                await _llamaCppServerManager.EnsureStartedAsync(cancellationToken);
            }
        }

        private async Task EnsureLocalModelAvailableAsync(CancellationToken cancellationToken)
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

        private async Task EnsureLlamaCppRuntimeAvailableAsync(CancellationToken cancellationToken)
        {
            var configuredExecutablePath = _settings.ResolveLlamaCppServerExecutablePath();
            if (File.Exists(configuredExecutablePath))
            {
                _logger.LogInformation("Embedded llama.cpp server found: {Path}", configuredExecutablePath);
                return;
            }

            var runtimeRoot = Path.GetDirectoryName(configuredExecutablePath);
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                throw new InvalidOperationException("Invalid LlamaCppServerExecutablePath configuration.");
            }

            if (!Directory.Exists(runtimeRoot))
            {
                Directory.CreateDirectory(runtimeRoot);
            }

            var discovered = Directory
                .GetFiles(runtimeRoot, "llama-server.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(discovered))
            {
                _logger.LogInformation("Embedded llama.cpp server found at: {Path}", discovered);
                return;
            }

            if (!_settings.LlamaCppServerAutoDownloadRuntime)
            {
                throw new InvalidOperationException(
                    $"Embedded llama.cpp server is missing: {configuredExecutablePath}. " +
                    "Enable LlamaCppServerAutoDownloadRuntime or bundle runtime files with deployment.");
            }

            if (string.IsNullOrWhiteSpace(_settings.LlamaCppServerRuntimeUrl))
            {
                throw new InvalidOperationException("LlamaCppServerRuntimeUrl must be configured for auto-download.");
            }

            var archivePath = Path.Combine(runtimeRoot, "llama-runtime.zip");
            var extractPath = Path.Combine(runtimeRoot, "_extract");

            if (Directory.Exists(extractPath))
            {
                Directory.Delete(extractPath, recursive: true);
            }

            _logger.LogWarning(
                "Embedded llama.cpp runtime is missing. Downloading from {Url}",
                _settings.LlamaCppServerRuntimeUrl);

            await DownloadFileAsync(_settings.LlamaCppServerRuntimeUrl, archivePath, cancellationToken);

            try
            {
                ZipFile.ExtractToDirectory(archivePath, extractPath, overwriteFiles: true);
                CopyDirectory(extractPath, runtimeRoot);
            }
            finally
            {
                CleanupPartialFile(archivePath);
                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, recursive: true);
                }
            }

            discovered = Directory
                .GetFiles(runtimeRoot, "llama-server.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(discovered))
            {
                throw new InvalidOperationException(
                    $"Downloaded runtime did not contain llama-server.exe. Runtime URL: {_settings.LlamaCppServerRuntimeUrl}");
            }

            _logger.LogInformation("Embedded llama.cpp runtime ready: {Path}", discovered);
        }

        private async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
        {
            HttpClient.Timeout = TimeSpan.FromMinutes(Math.Max(5, _settings.DownloadTimeoutMinutes));
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);

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
                    _logger.LogInformation(
                        "Runtime download progress: {Progress}% ({ReadMb}MB/{TotalMb}MB)",
                        progress,
                        totalRead / 1024 / 1024,
                        totalBytes / 1024 / 1024);
                    lastLoggedProgress = progress;
                }
            }
        }

        private static void CopyDirectory(string sourcePath, string destinationPath)
        {
            foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourcePath, directory);
                var targetDir = Path.Combine(destinationPath, relative);
                Directory.CreateDirectory(targetDir);
            }

            foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourcePath, file);
                var targetFile = Path.Combine(destinationPath, relative);
                var targetDir = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(file, targetFile, overwrite: true);
            }
        }

    }
}
