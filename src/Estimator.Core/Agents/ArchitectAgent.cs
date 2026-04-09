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

        protected override string? JsonSchema =>
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "status": {
                  "type": "string",
                  "enum": ["NEEDS_CLARIFICATION", "READY"]
                },
                "question": {
                  "type": "string"
                },
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
                      }
                    },
                    "required": ["id", "title", "description", "tech_stack"]
                  }
                }
              },
              "required": ["status"]
            }
            """;

        protected override string SystemPrompt =>
            """
            You are Agent 1 (Decomposer), a senior .NET solution architect for software estimation.
            You receive:
            - project description
            - clarification history
            - clarification budget and mode

            Your job:
            1. Decide if information is enough to build a quality production roadmap.
            2. If critical information is missing, ask exactly one high-impact clarification question.
            3. If information is sufficient, return a complete task roadmap.

            Rules:
            - Use clear task titles, concise delivery-oriented descriptions, and sequential task ids starting from 1.
            - Prefer 5 to 10 substantial tasks. Merge tiny implementation details into larger delivery tasks.
            - Keep tasks implementation-oriented and practical for delivery.
            - Include analysis, architecture, testing, deployment, and infrastructure tasks only when they are genuinely needed.
            - Use .NET/C# ecosystem choices where relevant.
            - Do not estimate hours.
            - Never ask more than one question in one response.
            - Ask a clarification question only if the estimate would be materially wrong without it.
            - Do not repeat previously asked questions from clarification history.
            - If mode says finalization or no more questions allowed, you MUST return tasks.
            - Keep descriptions compact and avoid long explanations.
            - Return JSON only. No markdown, no prose, no code fences.
            """;
    }
}
