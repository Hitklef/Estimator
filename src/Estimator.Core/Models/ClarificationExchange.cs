using System.Text.Json.Serialization;

namespace Estimator.Core.Models
{
    public sealed class ClarificationExchange
    {
        [JsonPropertyName("question")]
        public string Question { get; set; } = string.Empty;

        [JsonPropertyName("answer")]
        public string Answer { get; set; } = string.Empty;
    }
}
