using System.Text.Json.Serialization;

namespace Estimator.Core.Models
{
    public sealed class ValidatorFeedback
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "REJECTED";

        [JsonPropertyName("target_agent")]
        public string TargetAgent { get; set; } = "Estimator";

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;

        [JsonPropertyName("invalid_task_ids")]
        public List<int> InvalidTaskIds { get; set; } = new();

        [JsonIgnore]
        public bool IsValid => string.Equals(Status, "VALID", StringComparison.OrdinalIgnoreCase);
    }
}
