using System.Text.Json.Serialization;

namespace Estimator.Core.Models
{
    public sealed class ProjectEstimationResult
    {
        [JsonPropertyName("project_summary")]
        public string ProjectSummary { get; init; } = string.Empty;

        [JsonPropertyName("tech_stack")]
        public List<string> TechStack { get; init; } = new();

        [JsonPropertyName("tasks")]
        public List<ProjectTask> Tasks { get; init; } = new();

        [JsonPropertyName("total_hours")]
        public int TotalHours =>
            (int)Math.Round(Tasks.Sum(task => task.EstimatedHours ?? 0), MidpointRounding.AwayFromZero);

        [JsonPropertyName("validation_iterations")]
        public int ValidationIterations { get; init; }

        [JsonPropertyName("validation_notes")]
        public List<string> ValidationNotes { get; init; } = new();
    }
}
