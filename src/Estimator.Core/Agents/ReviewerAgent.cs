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
            You are Agent 3 (Validator). Validate ONLY estimation accuracy and sanity.
            Do not ask for new roadmap tasks. Do not evaluate decomposition completeness.

            Validation rules:
            1. Verify estimated hours are realistic for task complexity.
            2. Reject optimistic estimates that ignore delivery overhead.
            3. Enforce that non-micro tasks use 4-hour increments.
            4. Compare category totals against benchmark ranges included in payload.
            5. Return specific invalid task IDs when possible.
            6. Always route corrections to Estimator.

            Response format:
            - If valid: return exactly VALID
            - If invalid: return JSON object only:
              {
                "status": "REJECTED",
                "target_agent": "Estimator",
                "reason": "Specific actionable feedback",
                "invalid_task_ids": [2, 5, 8]
              }
            """;
    }
}
