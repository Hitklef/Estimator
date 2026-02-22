using Estimator.Core.Agents;
using Estimator.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Estimator.Core.Orchestrator
{
    public class AgentOrchestrator
    {
        private readonly ArchitectAgent _architect;
        private readonly EstimatorAgent _estimator;
        private readonly ReviewerAgent _reviewer;
        private readonly ILogger<AgentOrchestrator> _logger;
        private const int MaxRetries = 3;

        public AgentOrchestrator(
            ArchitectAgent architect,
            EstimatorAgent estimator,
            ReviewerAgent reviewer,
            ILogger<AgentOrchestrator> logger)
        {
            _architect = architect;
            _estimator = estimator;
            _reviewer = reviewer;
            _logger = logger;
        }

        public async Task<string> RunWorkflowAsync(string projectDescription)
        {
            var rawArchitect = await _architect.ExecuteAsync(projectDescription);
            var allTasks = JsonSerializer.Deserialize<List<ProjectTask>>(CleanJson(rawArchitect));

            string currentRequestForEstimator = JsonSerializer.Serialize(allTasks);

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                var rawEstimated = await _estimator.ExecuteAsync(currentRequestForEstimator);
                var cleanEstimated = CleanJson(rawEstimated);

                allTasks = JsonSerializer.Deserialize<List<ProjectTask>>(cleanEstimated);

                var reviewResponse = await _reviewer.ExecuteAsync(cleanEstimated);
                if (reviewResponse.Contains("VALID", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonSerializer.Serialize(allTasks);
                }

                _logger.LogWarning("Review rejected attempt {Attempt}.", attempt);
                currentRequestForEstimator = $"Reviewer feedback: {reviewResponse}. \n" +
                                             $"Please adjust 'estimated_hours' in this JSON: {cleanEstimated}";
            }

            return JsonSerializer.Serialize(allTasks);
        }

        private string CleanJson(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "[]";
            }

            var cleaned = input.Replace("```json", "").Replace("```", "").Trim();

            int start = cleaned.IndexOf('[');
            int end = cleaned.LastIndexOf(']');

            if (start != -1 && end != -1 && end > start)
            {
                return cleaned.Substring(start, (end - start) + 1);
            }

            return cleaned;
        }
    }
}
