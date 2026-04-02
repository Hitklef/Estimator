namespace Estimator.Core.Models
{
    public enum WorkflowStepStatus
    {
        NeedsClarification,
        Completed
    }

    public sealed class WorkflowStepResult
    {
        public WorkflowStepStatus Status { get; init; }
        public string? Question { get; init; }
        public ProjectEstimationResult? Result { get; init; }

        public static WorkflowStepResult NeedsClarification(string question) =>
            new()
            {
                Status = WorkflowStepStatus.NeedsClarification,
                Question = question
            };

        public static WorkflowStepResult Completed(ProjectEstimationResult result) =>
            new()
            {
                Status = WorkflowStepStatus.Completed,
                Result = result
            };
    }
}
