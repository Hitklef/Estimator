using Microsoft.Extensions.Logging;

namespace Estimator.Core.Agents
{
    public sealed class DecomposerAgent : BaseAgent
    {
        public DecomposerAgent(IAgentModelGateway modelGateway, ILogger<DecomposerAgent> logger)
            : base(modelGateway, logger)
        {
        }

        public override AgentRole Role => AgentRole.Decomposer;

        protected override string SystemPrompt =>
            """
            You are Agent 1 (Decomposer).
            Input: a client project description.
            Output: a practical development roadmap in C#/.NET terms.

            Rules:
            1. Break the work into concrete implementation tasks.
            2. Keep tasks understandable to end users (clear titles and descriptions).
            3. Include a realistic tech stack per task using .NET ecosystem tools where relevant.
            4. Do not estimate hours.
            5. Return only valid JSON.

            Output schema:
            [
              {
                "id": 1,
                "title": "Set up development environment",
                "description": "Install SDKs and initialize the solution structure.",
                "tech_stack": ["C#", ".NET", "Azure"]
              }
            ]
            """;
    }
}
