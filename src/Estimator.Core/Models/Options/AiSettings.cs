namespace Estimator.Core.Models.Options
{
    public sealed class AiSettings
    {
        public const string SectionName = "AiSettings";

        public string ModelUrl { get; set; } = string.Empty;
        public string ModelFileName { get; set; } = string.Empty;
        public string ModelsDirectory { get; set; } = "Models";
        public string ModelProvider { get; set; } = "LLamaSharp";
        public string LlamaCppServerExecutablePath { get; set; } = "Runtime/llama-server.exe";
        public string LlamaCppServerHost { get; set; } = "127.0.0.1";
        public int LlamaCppServerPort { get; set; } = 8089;
        public string LlamaCppServerAdditionalArgs { get; set; } = string.Empty;
        public bool LlamaCppServerAutoDownloadRuntime { get; set; } = true;
        public int LlamaCppPerAttemptTimeoutSeconds { get; set; } = 120;
        public int LlamaCppMaxInferenceAttempts { get; set; } = 2;
        public bool LlamaCppRestartServerOnTimeout { get; set; } = true;
        public string LlamaCppServerRuntimeUrl { get; set; } =
            "https://github.com/ggml-org/llama.cpp/releases/download/b8672/llama-b8672-bin-win-cuda-12.4-x64.zip";

        public uint ContextSize { get; set; } = 4096;
        public int GpuLayerCount { get; set; } = 20;
        public int DownloadTimeoutMinutes { get; set; } = 120;
        public int AgentInferenceTimeoutSeconds { get; set; } = 300;
        public int DecomposerProjectContextMaxCharacters { get; set; } = 12000;
        public int ProjectContextMaxCharacters
        {
            get => DecomposerProjectContextMaxCharacters;
            set => DecomposerProjectContextMaxCharacters = value;
        }
        public int MaxValidationCycles { get; set; } = 3;
        public int MaxClarificationRounds { get; set; } = 3;
        public int SessionTtlMinutes { get; set; } = 180;

        public Dictionary<string, AgentRuntimeProfile> AgentRuntimeProfiles { get; set; } = CreateDefaultProfiles();

        public string LocalModelPath => Path.Combine(AppContext.BaseDirectory, ModelsDirectory, ModelFileName);
        public bool UseLlamaCppServer => ModelProvider.Equals("LlamaCppServer", StringComparison.OrdinalIgnoreCase);

        public string ResolveLlamaCppServerExecutablePath()
        {
            if (Path.IsPathRooted(LlamaCppServerExecutablePath))
            {
                return LlamaCppServerExecutablePath;
            }

            return Path.Combine(AppContext.BaseDirectory, LlamaCppServerExecutablePath);
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
                ["Default"] = new AgentRuntimeProfile { MaxTokens = 768, Temperature = 0.2f, TopP = 0.9f },
                ["Decomposer"] = new AgentRuntimeProfile { MaxTokens = 1024, Temperature = 0.2f, TopP = 0.9f },
                ["Estimator"] = new AgentRuntimeProfile { MaxTokens = 1280, Temperature = 0.15f, TopP = 0.85f },
                ["Validator"] = new AgentRuntimeProfile { MaxTokens = 640, Temperature = 0.1f, TopP = 0.8f }
            };
    }

    public sealed class AgentRuntimeProfile
    {
        public int MaxTokens { get; set; } = 1024;
        public float Temperature { get; set; } = 0.2f;
        public float TopP { get; set; } = 0.9f;
        public List<string> AntiPrompts { get; set; } = new() { "<turn|>", "<|turn>user" };
    }
}
