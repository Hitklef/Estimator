using System.Diagnostics;
using Estimator.Core.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Estimator.Core.Services
{
    public sealed class LlamaCppServerManager
    {
        private readonly AiSettings _settings;
        private readonly ILogger<LlamaCppServerManager> _logger;
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private readonly HttpClient _healthClient = new();

        private Process? _serverProcess;
        private string _lastStdErr = string.Empty;

        public LlamaCppServerManager(IOptions<AiSettings> options, ILogger<LlamaCppServerManager> logger)
        {
            _settings = options.Value;
            _logger = logger;

            _healthClient.Timeout = TimeSpan.FromSeconds(5);
        }

        public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
        {
            if (!_settings.UseLlamaCppServer)
            {
                return;
            }

            if (await IsHealthyAsync(cancellationToken))
            {
                _logger.LogDebug("Embedded llama-server is already healthy.");
                return;
            }

            await _startLock.WaitAsync(cancellationToken);
            try
            {
                if (await IsHealthyAsync(cancellationToken))
                {
                    return;
                }

                Stop();

                var executablePath = ResolveExecutablePath();
                if (!File.Exists(executablePath))
                {
                    throw new InvalidOperationException(
                        $"Embedded llama.cpp server is missing: {executablePath}. " +
                        "Bundle 'llama-server.exe' with the deployed app.");
                }

                if (!File.Exists(_settings.LocalModelPath))
                {
                    throw new InvalidOperationException(
                        $"Model file not found: {_settings.LocalModelPath}");
                }

                _logger.LogInformation(
                    "Starting embedded llama-server. Executable={ExecutablePath} Model={ModelPath} Host={Host} Port={Port} ContextSize={ContextSize} RequestedGpuLayers={GpuLayers}",
                    executablePath,
                    _settings.LocalModelPath,
                    _settings.LlamaCppServerHost,
                    _settings.LlamaCppServerPort,
                    _settings.ContextSize,
                    _settings.GpuLayerCount);

                Exception? lastException = null;
                foreach (var gpuLayers in BuildGpuLayerCandidates(_settings.GpuLayerCount))
                {
                    Stop();
                    _lastStdErr = string.Empty;

                    var args = BuildArguments(_settings.LocalModelPath, gpuLayers);
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        Arguments = args,
                        WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                    process.OutputDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            _logger.LogInformation("llama-server stdout: {Message}", e.Data);
                        }
                    };
                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            _lastStdErr = e.Data;
                            _logger.LogWarning("llama-server stderr: {Message}", e.Data);
                        }
                    };

                    if (!process.Start())
                    {
                        throw new InvalidOperationException("Failed to start embedded llama-server process.");
                    }

                    _logger.LogInformation(
                        "llama-server process started. PID={Pid} n-gpu-layers={GpuLayers}",
                        process.Id,
                        gpuLayers);

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    _serverProcess = process;

                    var started = await WaitUntilHealthyAsync(process, cancellationToken);
                    if (started)
                    {
                        _logger.LogInformation(
                            "Embedded llama-server started at http://{Host}:{Port} (n-gpu-layers={GpuLayers})",
                            _settings.LlamaCppServerHost,
                            _settings.LlamaCppServerPort,
                            gpuLayers);
                        return;
                    }

                    lastException = new InvalidOperationException(
                        "Embedded llama-server did not become ready in time. " +
                        (string.IsNullOrWhiteSpace(_lastStdErr) ? string.Empty : $"Last stderr: {_lastStdErr}"));

                    if (!IsGpuMemoryStartupIssue(_lastStdErr))
                    {
                        break;
                    }

                    _logger.LogWarning(
                        "llama-server startup failed with n-gpu-layers={GpuLayers}. Retrying with fewer GPU layers.",
                        gpuLayers);
                }

                Stop();
                throw lastException ?? new InvalidOperationException("Embedded llama-server startup failed.");
            }
            finally
            {
                _startLock.Release();
            }
        }

        public void Stop()
        {
            if (_serverProcess is null)
            {
                return;
            }

            try
            {
                if (!_serverProcess.HasExited)
                {
                    _logger.LogInformation("Stopping embedded llama-server. PID={Pid}", _serverProcess.Id);
                    _serverProcess.Kill(entireProcessTree: true);
                    _serverProcess.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop embedded llama-server cleanly.");
            }
            finally
            {
                _serverProcess.Dispose();
                _serverProcess = null;
            }
        }

        private string BuildArguments(string modelPath, int gpuLayers)
        {
            var args =
                $"--model \"{modelPath}\" " +
                $"--ctx-size {_settings.ContextSize} " +
                $"--n-gpu-layers {gpuLayers} " +
                $"--host {_settings.LlamaCppServerHost} " +
                $"--port {_settings.LlamaCppServerPort}";

            if (!string.IsNullOrWhiteSpace(_settings.LlamaCppServerAdditionalArgs))
            {
                args += " " + _settings.LlamaCppServerAdditionalArgs.Trim();
            }

            return args;
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

        private static bool IsGpuMemoryStartupIssue(string stderr)
        {
            if (string.IsNullOrWhiteSpace(stderr))
            {
                return false;
            }

            return stderr.Contains("cuda", StringComparison.OrdinalIgnoreCase) &&
                   (stderr.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
                    stderr.Contains("not enough memory", StringComparison.OrdinalIgnoreCase) ||
                    stderr.Contains("alloc", StringComparison.OrdinalIgnoreCase));
        }

        private string ResolveExecutablePath()
        {
            var configuredPath = _settings.ResolveLlamaCppServerExecutablePath();
            if (File.Exists(configuredPath))
            {
                return configuredPath;
            }

            var runtimeRoot = Path.GetDirectoryName(configuredPath);
            if (string.IsNullOrWhiteSpace(runtimeRoot) || !Directory.Exists(runtimeRoot))
            {
                return configuredPath;
            }

            var discovered = Directory
                .GetFiles(runtimeRoot, "llama-server.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            return discovered ?? configuredPath;
        }

        private async Task<bool> WaitUntilHealthyAsync(Process process, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    return false;
                }

                if (await IsHealthyAsync(cancellationToken))
                {
                    return true;
                }

                await Task.Delay(500, cancellationToken);
            }

            return false;
        }

        private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
        {
            var baseUrl = $"http://{_settings.LlamaCppServerHost}:{_settings.LlamaCppServerPort}";
            var urls = new[]
            {
                $"{baseUrl}/health",
                $"{baseUrl}/v1/models",
                $"{baseUrl}/props"
            };

            foreach (var url in urls)
            {
                try
                {
                    using var response = await _healthClient.GetAsync(url, cancellationToken);
                    _logger.LogDebug("llama-server health probe {Url} -> {StatusCode}", url, (int)response.StatusCode);
                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }
                }
                catch
                {
                    _logger.LogDebug("llama-server health probe failed for {Url}", url);
                }
            }

            return false;
        }
    }
}
