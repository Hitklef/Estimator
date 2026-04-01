namespace Estimator.Core.Models.Options
{
    public sealed class AiSettings
    {
        public const string SectionName = "AiSettings";

        public string ModelUrl { get; set; } = string.Empty;
        public string ModelFileName { get; set; } = string.Empty;
        public string ModelsDirectory { get; set; } = "Models";

        public uint ContextSize { get; set; } = 4096;
        public int GpuLayerCount { get; set; } = 20;
        public int DownloadTimeoutMinutes { get; set; } = 30;
        public int AgentInferenceTimeoutSeconds { get; set; } = 120;
        public int MaxValidationCycles { get; set; } = 3;

        public Dictionary<string, AgentRuntimeProfile> AgentRuntimeProfiles { get; set; } = CreateDefaultProfiles();

        public string LocalModelPath => Path.Combine(AppContext.BaseDirectory, ModelsDirectory, ModelFileName);

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
                ["Default"] = new AgentRuntimeProfile(),
                ["Decomposer"] = new AgentRuntimeProfile { MaxTokens = 2048, Temperature = 0.2f, TopP = 0.9f },
                ["Estimator"] = new AgentRuntimeProfile { MaxTokens = 2048, Temperature = 0.15f, TopP = 0.85f },
                ["Validator"] = new AgentRuntimeProfile { MaxTokens = 1024, Temperature = 0.1f, TopP = 0.8f }
            };
    }

    public sealed class AgentRuntimeProfile
    {
        public int MaxTokens { get; set; } = 1024;
        public float Temperature { get; set; } = 0.2f;
        public float TopP { get; set; } = 0.9f;
        public List<string> AntiPrompts { get; set; } = new() { "User:" };
    }
}
