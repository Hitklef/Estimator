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

        protected override string? JsonSchema =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "status": {
                  "type": "string",
                  "enum": ["VALID", "REJECTED"]
                },
                "target_agent": {
                  "type": "string",
                  "enum": ["Estimator"]
                },
                "reason": {
                  "type": "string"
                },
                "invalid_task_ids": {
                  "type": "array",
                  "items": { "type": "integer", "minimum": 1 }
                }
              },
              "required": ["status", "target_agent", "reason", "invalid_task_ids"]
            }
            """;

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
            6. Return JSON only. No markdown, no prose, no code fences.
            7. Reject only material estimate issues that meaningfully affect delivery planning.

            Response format:
            - Always return ONLY one JSON object.
            - Valid response:
              {
                "status": "VALID",
                "target_agent": "Estimator",
                "reason": "",
                "invalid_task_ids": []
              }
            - Rejected response:
              {
                "status": "REJECTED",
                "target_agent": "Estimator",
                "reason": "specific actionable reason",
                "invalid_task_ids": [2, 5]
              }
            """;
    }
}
