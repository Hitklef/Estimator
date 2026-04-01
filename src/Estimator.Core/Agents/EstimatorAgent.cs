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
            You are Agent 2 (Estimator).
            Input: roadmap tasks from Agent 1.
            Output: the same tasks with realistic production-grade hour estimates.

            Rules:
            1. Estimate each task for production delivery.
            2. Use multiples of 4 hours by default.
            3. Use 1-2 hour values only for highly specific edge-case micro tasks.
            4. Avoid optimistic estimates; include implementation, testing, debugging, and review effort.
            5. Return only valid JSON.

            Output schema:
            [
              {
                "id": 1,
                "title": "Set up development environment",
                "description": "Install SDKs and initialize the solution structure.",
                "tech_stack": ["C#", ".NET", "Azure"],
                "estimated_hours": 16
              }
            ]
            """;
    }
}
