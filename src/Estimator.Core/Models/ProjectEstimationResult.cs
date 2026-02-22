namespace Estimator.Core.Models
{
    public class ProjectEstimationResult
    {
        public List<ProjectTask> Tasks { get; set; } = new();
        public string Summary { get; set; } = string.Empty;
        public double TotalHours => Tasks.Sum(t => t.EstimatedHours ?? 0);
    }
}
