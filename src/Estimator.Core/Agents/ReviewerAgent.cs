using Microsoft.Extensions.Logging;

namespace Estimator.Core.Agents
{
    public sealed class ValidatorAgent : BaseAgent
    {
        public ValidatorAgent(IAgentModelGateway modelGateway, ILogger<ValidatorAgent> logger)
            : base(modelGateway, logger)
        {
        }

        public override AgentRole Role => AgentRole.Validator;

        protected override string SystemPrompt =>
            """
            You are Agent 3 (Validator).
            Validate ONLY estimate accuracy and sanity.

            Rules:
            1. Check if estimated hours are realistic for each task.
            2. Enforce 4-hour increments for non-micro tasks.
            3. If estimates are unrealistic, send feedback only to the Estimator.
            4. Do not create or rewrite roadmap tasks.

            Response format:
            - If valid: return exactly VALID
            - If invalid: return exactly this JSON shape:
              {
                "status": "REJECTED",
                "target_agent": "Estimator",
                "reason": "clear reason for rework",
                "invalid_task_ids": [1, 3]
              }
            """;
    }
}
