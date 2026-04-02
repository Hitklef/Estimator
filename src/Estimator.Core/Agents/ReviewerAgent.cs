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
            You receive project description, clarification history, and estimated tasks.

            Rules:
            1. Check whether each estimate is realistic for the task complexity and provided clarifications.
            2. Enforce 4-hour increments for non-micro tasks.
            3. Reject optimistic estimates.
            4. Do NOT request roadmap changes from Agent 1.
            5. Rework target must always be Estimator.

            Response format:
            - If valid, return exactly: VALID
            - If invalid, return ONLY this JSON object:
              {
                "status": "REJECTED",
                "target_agent": "Estimator",
                "reason": "specific actionable reason",
                "invalid_task_ids": [2, 5]
              }
            """;
    }
}
