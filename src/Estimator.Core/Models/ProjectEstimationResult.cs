using System.Text.Json.Serialization;

namespace Estimator.Core.Models
{
    public sealed class ProjectEstimationResult
    {
        [JsonPropertyName("project_summary")]
        public string ProjectSummary { get; init; } = string.Empty;

        [JsonPropertyName("benchmark_profile")]
        public string BenchmarkProfile { get; init; } = string.Empty;

        [JsonPropertyName("tech_stack")]
        public List<string> TechStack { get; init; } = new();

        [JsonPropertyName("tasks")]
        public List<ProjectTask> Tasks { get; init; } = new();

        [JsonIgnore]
        public List<CategoryEstimateSummary> CategoryBreakdown { get; init; } = new();

        [JsonPropertyName("total_hours")]
        public int TotalHours =>
            (int)Math.Round(Tasks.Sum(task => task.EstimatedHours ?? 0), MidpointRounding.AwayFromZero);

        [JsonPropertyName("validation_iterations")]
        public int ValidationIterations { get; init; }

        [JsonPropertyName("validation_notes")]
        public List<string> ValidationNotes { get; init; } = new();
    }

    public sealed class CategoryEstimateSummary
    {
        [JsonPropertyName("category_key")]
        public string CategoryKey { get; init; } = string.Empty;

        [JsonPropertyName("category_name")]
        public string CategoryName { get; init; } = string.Empty;

        [JsonPropertyName("hours")]
        public int Hours { get; init; }

        [JsonPropertyName("minimum_expected_hours")]
        public int MinimumExpectedHours { get; init; }

        [JsonPropertyName("maximum_expected_hours")]
        public int MaximumExpectedHours { get; init; }

        [JsonPropertyName("task_count")]
        public int TaskCount { get; init; }
    }
}
