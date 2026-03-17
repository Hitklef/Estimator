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
        public int AgentInferenceTimeoutSeconds { get; set; } = 60;
        public int MaxValidationCycles { get; set; } = 2;

        public Dictionary<string, AgentRuntimeProfile> AgentRuntimeProfiles { get; set; } = CreateDefaultProfiles();
        public EstimationPolicySettings EstimationPolicy { get; set; } = new();
        public List<BenchmarkProfileSettings> BenchmarkProfiles { get; set; } = CreateDefaultBenchmarkProfiles();

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

        public BenchmarkProfileSettings ResolveBenchmarkProfile(string projectDescription)
        {
            var profiles = BenchmarkProfiles.Count == 0 ? CreateDefaultBenchmarkProfiles() : BenchmarkProfiles;
            var normalizedDescription = projectDescription.ToLowerInvariant();

            BenchmarkProfileSettings? bestProfile = null;
            var bestScore = int.MinValue;

            foreach (var profile in profiles)
            {
                var score = profile.TriggerKeywords.Count(keyword =>
                    normalizedDescription.Contains(keyword.ToLowerInvariant(), StringComparison.Ordinal));

                if (score >= profile.MinimumKeywordMatches && score > bestScore)
                {
                    bestProfile = profile;
                    bestScore = score;
                }
            }

            if (bestProfile is not null)
            {
                return bestProfile;
            }

            return profiles.FirstOrDefault(profile => profile.IsDefault)
                ?? profiles[0];
        }

        private static Dictionary<string, AgentRuntimeProfile> CreateDefaultProfiles() =>
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Default"] = new AgentRuntimeProfile(),
                ["Decomposer"] = new AgentRuntimeProfile { MaxTokens = 2048, Temperature = 0.2f, TopP = 0.88f },
                ["Estimator"] = new AgentRuntimeProfile { MaxTokens = 2048, Temperature = 0.15f, TopP = 0.82f },
                ["Validator"] = new AgentRuntimeProfile { MaxTokens = 512, Temperature = 0.08f, TopP = 0.78f }
            };

        private static List<BenchmarkProfileSettings> CreateDefaultBenchmarkProfiles() =>
        [
            new BenchmarkProfileSettings
            {
                Name = "GenericProductionApp",
                IsDefault = true,
                MinimumKeywordMatches = 0,
                TriggerKeywords = [],
                MinimumTotalHours = 240,
                MaximumTotalHours = 1200,
                MinimumTaskCount = 10,
                QualityOverheadRatio = 0.2,
                Workstreams =
                [
                    new WorkstreamRangeSettings
                    {
                        Key = "CoreSetupArchitecture",
                        Name = "Core setup and architecture",
                        MinHours = 48,
                        MaxHours = 120,
                        Required = true,
                        Keywords = ["architecture", "setup", "infrastructure", "foundation", "domain", "api design", "database design"]
                    },
                    new WorkstreamRangeSettings
                    {
                        Key = "CoreFeatures",
                        Name = "Core features",
                        MinHours = 80,
                        MaxHours = 360,
                        Required = true,
                        Keywords = ["feature", "onboarding", "flow", "business logic", "check-in", "exercise", "workflow", "endpoint"]
                    },
                    new WorkstreamRangeSettings
                    {
                        Key = "ExperienceAndInterface",
                        Name = "Experience and interface",
                        MinHours = 40,
                        MaxHours = 200,
                        Required = true,
                        Keywords = ["ui", "ux", "frontend", "screen", "animation", "visual", "react", "blazor", "maui"]
                    },
                    new WorkstreamRangeSettings
                    {
                        Key = "CommunityPrivacySecurity",
                        Name = "Community privacy and security",
                        MinHours = 32,
                        MaxHours = 220,
                        Required = true,
                        Keywords = ["privacy", "anonym", "security", "compliance", "community", "authorization", "encryption"]
                    },
                    new WorkstreamRangeSettings
                    {
                        Key = "QualityTestingPmUx",
                        Name = "QA, testing, PM and UX reviews",
                        MinHours = 40,
                        MaxHours = 220,
                        Required = true,
                        Keywords = ["qa", "test", "testing", "review", "ux research", "product", "iteration", "validation", "uat", "release", "monitoring"]
                    }
                ]
            },
            new BenchmarkProfileSettings
            {
                Name = "PrivacyFirstWellnessApp",
                IsDefault = false,
                MinimumKeywordMatches = 3,
                TriggerKeywords = ["trauma", "mental", "wellness", "healing", "check-in", "tree growth", "anonymized", "privacy", "community", "reflection"],
                MinimumTotalHours = 660,
                MaximumTotalHours = 850,
                MinimumTaskCount = 18,
                QualityOverheadRatio = 0.22,
                Workstreams =
                [
                    new WorkstreamRangeSettings
                    {
                        Key = "CoreSetupArchitecture",
                        Name = "Core setup and architecture",
                        MinHours = 80,
                        MaxHours = 120,
                        Required = true,
                        Keywords = ["architecture", "setup", "infrastructure", "foundational", "domain model", "api contract", "database schema", "ci/cd"]
                    },
                    new WorkstreamRangeSettings
                    {
                        Key = "CoreFeatures",
                        Name = "Core features",
                        MinHours = 200,
                        MaxHours = 260,
                        Required = true,
                        Keywords = ["onboarding", "check-in", "exercise", "emotional detection", "sentiment", "prompt", "business workflow", "intention"]
                    },
                    new WorkstreamRangeSettings
                    {
                        Key = "TreeGrowthSystem",
                        Name = "Tree growth system",
                        MinHours = 160,
                        MaxHours = 200,
                        Required = true,
                        Keywords = ["tree", "growth", "animation", "streak", "visual progression", "gamification", "rendering"]
                    },
                    new WorkstreamRangeSettings
                    {
                        Key = "CommunityPrivacySecurity",
                        Name = "Community view and privacy",
                        MinHours = 120,
                        MaxHours = 150,
                        Required = true,
                        Keywords = ["community", "forest", "anonymized", "privacy", "security", "data deletion", "safety"]
                    },
                    new WorkstreamRangeSettings
                    {
                        Key = "QualityTestingPmUx",
                        Name = "QA, testing and PM/UX reviews",
                        MinHours = 100,
                        MaxHours = 120,
                        Required = true,
                        Keywords = ["qa", "test", "testing", "integration test", "ux review", "product review", "sprint review", "uat", "regression", "release verification"]
                    }
                ]
            }
        ];
    }

    public sealed class AgentRuntimeProfile
    {
        public int MaxTokens { get; set; } = 2048;
        public float Temperature { get; set; } = 0.2f;
        public float TopP { get; set; } = 0.9f;
        public List<string> AntiPrompts { get; set; } = new() { "User:" };
    }

    public sealed class EstimationPolicySettings
    {
        public int MinimumHoursPerStandardTask { get; set; } = 8;
        public int MediumComplexityTaskFloorHours { get; set; } = 16;
        public int HighComplexityTaskFloorHours { get; set; } = 24;
        public int CriticalComplexityTaskFloorHours { get; set; } = 40;
        public int MinimumProjectHours { get; set; } = 160;
        public int LargeScopeTaskCountThreshold { get; set; } = 12;
        public int MinimumTasksPerWorkstream { get; set; } = 2;
    }

    public sealed class BenchmarkProfileSettings
    {
        public string Name { get; set; } = "GenericProductionApp";
        public bool IsDefault { get; set; }
        public int MinimumKeywordMatches { get; set; } = 0;
        public int MinimumTotalHours { get; set; } = 240;
        public int MaximumTotalHours { get; set; } = 1200;
        public int MinimumTaskCount { get; set; } = 10;
        public double QualityOverheadRatio { get; set; } = 0.2;
        public List<string> TriggerKeywords { get; set; } = new();
        public List<WorkstreamRangeSettings> Workstreams { get; set; } = new();
    }

    public sealed class WorkstreamRangeSettings
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int MinHours { get; set; }
        public int MaxHours { get; set; }
        public bool Required { get; set; } = true;
        public List<string> Keywords { get; set; } = new();
    }
}
