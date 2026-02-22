using System.Text.Json.Serialization;

namespace Estimator.Core.Models
{
    public class ProjectTask
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
        [JsonPropertyName("tech_stack")]
        public List<string> TechStack { get; set; } = new();

        [JsonPropertyName("estimated_hours")]
        public double? EstimatedHours { get; set; }
    }
}
