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

        protected override string? JsonSchema =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "tasks": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "id": { "type": "integer", "minimum": 1 },
                      "title": { "type": "string" },
                      "description": { "type": "string" },
                      "tech_stack": {
                        "type": "array",
                        "items": { "type": "string" }
                      },
                      "estimated_hours": { "type": "number", "minimum": 0 }
                    },
                    "required": ["id", "title", "description", "tech_stack", "estimated_hours"]
                  }
                }
              },
              "required": ["tasks"]
            }
            """;

        protected override string SystemPrompt =>
            """
            You are Agent 2 (Estimator), a senior engineering estimator.
            You receive:
            - project description
            - clarification history from Agent 1
            - roadmap tasks
            - optional validator feedback for rework

            Your job:
            - Return the task list with realistic production-grade hour estimates.

            Rules:
            1. Preserve task meaning and keep titles/descriptions understandable.
            2. Use realistic hours for implementation, testing, debugging, and review.
            3. Estimates should be multiples of 4 hours by default.
            4. Use 1-2 hours only for truly specific edge-case micro tasks.
            5. If validator feedback is present, fix underestimation and listed invalid tasks first.
            6. Avoid optimistic bias.
            7. Preserve task ids and tech stack entries.
            8. Keep descriptions compact; do not rewrite them into long prose.
            9. Keep estimates realistic but concise enough for fast structured output.
            10. Return JSON only. No markdown, no prose, no code fences.

            Output format:
            Return ONLY this JSON object:
            {
              "tasks": [
                {
                  "id": 1,
                  "title": "Set up development environment",
                  "description": "Install required SDKs and initialize solution structure.",
                  "tech_stack": ["C#", ".NET", "Azure"],
                  "estimated_hours": 16
                }
              ]
            }
            """;
    }
}
