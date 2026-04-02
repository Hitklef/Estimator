namespace Estimator.Core.Models
{
    public sealed class WorkflowSession
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
        public string ProjectDescription { get; set; } = string.Empty;
        public List<ClarificationExchange> Clarifications { get; set; } = new();
        public string? PendingQuestion { get; set; }
        public int QuestionsAsked { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public ProjectEstimationResult? Result { get; set; }
    }
}
