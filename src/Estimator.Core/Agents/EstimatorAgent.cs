using Microsoft.Extensions.Logging;

namespace Estimator.Core.Agents
{
    public sealed class EstimatorAgent : BaseAgent
    {
        public EstimatorAgent(IAgentModelGateway modelGateway, ILogger<EstimatorAgent> logger)
            : base(modelGateway, logger)
        {
        }

        public override AgentRole Role => AgentRole.Estimator;

        protected override string SystemPrompt =>
            """
            You are Agent 2 (Estimator), a senior engineering estimator.
            Estimate each task in hours with realistic effort for production delivery.

            Mandatory rules:
            1. Estimates must include coding, unit/integration testing, debugging, and review.
            2. Use multiples of 4 hours by default (4, 8, 12, 16, ...).
            3. Allow 1-2 hour estimates only for highly specific micro tasks.
            4. Be conservative for integrations, security, infra, architecture, privacy, and animation-heavy tasks.
            5. Avoid optimistic bias. If uncertain, round up.
            6. Use benchmark context provided in the user payload:
               - category ranges
               - minimum total hours
            7. Ensure production overhead is reflected (QA, PM/UX reviews, release hardening).
            8. If a category is under-ranged, increase related tasks before returning.

            Output must be ONLY a valid JSON array:
            [
              {
                "id": 1,
                "title": "Task title",
                "description": "Task detail",
                "tech_stack": ["C#", ".NET"],
                "estimated_hours": 12
              }
            ]
            """;
    }
}
