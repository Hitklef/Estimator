namespace Estimator.Core.Models.Options
{
    public sealed class AiSettings
    {
        public const string SectionName = "AiSettings";

        public string ModelUrl { get; set; } = string.Empty;
        public string ModelFileName { get; set; } = string.Empty;
        public string ModelsDirectory { get; set; } = "Models";
        public string ModelProvider { get; set; } = ModelProviderKinds.LLamaSharp;
        public string PromptFormat { get; set; } = PromptFormatKinds.Gemma;
        public bool MergeSystemPromptIntoUserMessage { get; set; }
        public string LlamaCppServerExecutablePath { get; set; } = "Runtime/llama-server.exe";
        public string LlamaCppServerHost { get; set; } = "127.0.0.1";
        public int LlamaCppServerPort { get; set; } = 8089;
        public string LlamaCppServerAdditionalArgs { get; set; } = string.Empty;
        public bool LlamaCppServerAutoDownloadRuntime { get; set; } = true;
        public bool LlamaCppUseFlashAttention { get; set; } = true;
        public bool LlamaCppDisableReasoning { get; set; } = true;
        public bool LlamaCppDisableWebUi { get; set; } = true;
        public string LlamaCppCacheTypeK { get; set; } = "q8_0";
        public string LlamaCppCacheTypeV { get; set; } = "q8_0";
        public int LlamaCppThreads { get; set; }
        public int LlamaCppThreadsBatch { get; set; }
        public int LlamaCppBatchSize { get; set; } = 1024;
        public int LlamaCppUBatchSize { get; set; } = 512;
        public int LlamaCppPerAttemptTimeoutSeconds { get; set; } = 0;
        public int LlamaCppMaxInferenceAttempts { get; set; } = 1;
        public bool LlamaCppRestartServerOnTimeout { get; set; } = true;
        public int LlamaCppSafetyTimeoutSeconds { get; set; } = 900;
        public string LlamaCppServerRuntimeUrl { get; set; } =
            "https://github.com/ggml-org/llama.cpp/releases/download/b8672/llama-b8672-bin-win-cuda-12.4-x64.zip";
        public string OllamaBaseUrl { get; set; } = "http://127.0.0.1:11434/api/";
        public string OllamaModelName { get; set; } = "gemma4:26b";
        public bool OllamaAutoPullModel { get; set; }
        public string OllamaKeepAlive { get; set; } = "10m";

        public uint ContextSize { get; set; } = 4096;
        public int GpuLayerCount { get; set; } = 20;
        public int DownloadTimeoutMinutes { get; set; } = 120;
        public int AgentInferenceTimeoutSeconds { get; set; } = 300;
        public int WorkflowTimeoutSeconds { get; set; } = 0;
        public int ProjectContextMaxCharacters { get; set; } = 12000;
        public int MaxDecomposerCallsPerSession { get; set; } = 5;
        public int MaxValidationCycles { get; set; } = 3;
        public int MaxClarificationRounds { get; set; } = 3;
        public int SessionTtlMinutes { get; set; } = 180;
        public bool EnableLiveModelConsoleOutput { get; set; }
        public bool LogModelResponsePreview { get; set; } = true;
        public int LoggedModelPreviewCharacters { get; set; } = 500;
        public int LiveModelConsoleFlushCharacters { get; set; } = 120;

        public Dictionary<string, AgentRuntimeProfile> AgentRuntimeProfiles { get; set; } = CreateDefaultProfiles();

        public string LocalModelPath => Path.Combine(AppContext.BaseDirectory, ModelsDirectory, ModelFileName);
        public bool UseLlamaCppServer => ModelProvider.Equals(ModelProviderKinds.LlamaCppServer, StringComparison.OrdinalIgnoreCase);
        public bool UseLLamaSharp => ModelProvider.Equals(ModelProviderKinds.LLamaSharp, StringComparison.OrdinalIgnoreCase);
        public bool UseOllama => ModelProvider.Equals(ModelProviderKinds.Ollama, StringComparison.OrdinalIgnoreCase);

        public string ResolveLlamaCppServerExecutablePath()
        {
            if (Path.IsPathRooted(LlamaCppServerExecutablePath))
            {
                return LlamaCppServerExecutablePath;
            }

            return Path.Combine(AppContext.BaseDirectory, LlamaCppServerExecutablePath);
        }

        public string ResolveOllamaBaseUrl()
        {
            var normalized = string.IsNullOrWhiteSpace(OllamaBaseUrl)
                ? "http://127.0.0.1:11434/api/"
                : OllamaBaseUrl.Trim();

            return normalized.EndsWith("/", StringComparison.Ordinal)
                ? normalized
                : normalized + "/";
        }

        public AgentRuntimeProfile ResolveProfile(string role)
        {
            if (AgentRuntimeProfiles.TryGetValue(role, out var profile))
            {
                return profile;
            }

            if (AgentRuntimeProfiles.TryGetValue("Default", out var fallback))
            {
                return fallback;
            }

            return new AgentRuntimeProfile();
        }

        private static Dictionary<string, AgentRuntimeProfile> CreateDefaultProfiles() =>
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Default"] = new AgentRuntimeProfile { MaxTokens = 640, Temperature = 0.15f, TopP = 0.9f },
                ["Decomposer"] = new AgentRuntimeProfile { MaxTokens = 384, Temperature = 0.1f, TopP = 0.85f },
                ["Estimator"] = new AgentRuntimeProfile { MaxTokens = 896, Temperature = 0.1f, TopP = 0.85f },
                ["Validator"] = new AgentRuntimeProfile { MaxTokens = 192, Temperature = 0.05f, TopP = 0.8f }
            };
    }

    public sealed class AgentRuntimeProfile
    {
        public int MaxTokens { get; set; } = 1024;
        public float Temperature { get; set; } = 0.2f;
        public float TopP { get; set; } = 0.9f;
        public List<string> AntiPrompts { get; set; } = new() { "<end_of_turn>", "<start_of_turn>user" };
    }
}
