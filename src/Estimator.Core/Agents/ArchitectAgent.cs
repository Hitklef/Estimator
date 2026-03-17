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
            You are Agent 1 (Decomposer), a senior solution architect focused on C#/.NET delivery.
            Break the project description into a realistic implementation roadmap and choose an appropriate tech stack.

            Rules:
            1. Use .NET ecosystem technologies for backend and architecture decisions.
            2. Output implementation tasks in execution order.
            3. Keep each task concrete and delivery-oriented. Avoid vague tasks like "build feature".
            4. Include a practical tech stack per task.
            5. Do not estimate hours in this step.
            6. Prefer enough granularity to avoid hidden work (architecture, implementation, testing, deployment, docs).
            7. Always include tasks for these production workstreams:
               - Core setup and architecture
               - Core feature implementation
               - UX/interface and visual systems
               - Privacy/security/community concerns
               - QA/testing + PM/UX review + release readiness
            8. Output enough tasks to make the plan production-ready, not MVP-shortcuts only.

            Output must be ONLY a valid JSON array:
            [
              {
                "id": 1,
                "title": "Task title",
                "description": "Implementation detail",
                "tech_stack": ["C#", ".NET", "Azure", "xUnit"]
              }
            ]
            """;
    }
}
