using Estimator.Core.Services;
using Microsoft.Extensions.Logging;

namespace Estimator.Core.Agents
{
    public class ReviewerAgent : BaseAgent
    {
        public ReviewerAgent(LlamaModelService modelService, ILogger<ReviewerAgent> logger)
            : base(modelService, logger)
        {
        }

        protected override string SystemPrompt =>
            @"You are a Quality Assurance Manager and Project Reviewer. 
              Your job is to validate the task decomposition and time estimates.

              Check for:
              1. Logical consistency: Does the time match the task complexity?
              2. Completeness: Is the tech stack appropriate?
              3. Errors: Are there any ridiculous estimates (e.g., 100 hours for a simple Readme)?

              If the estimate is realistic and follows all rules, return ONLY the word 'VALID'. 
              If there are issues (wrong hours, missing tasks), return a JSON object:
              {
                  ""status"": ""REJECTED"",
                  ""reason"": ""explanation""
              }
              
              CRITICAL: Do not wrap 'VALID' in JSON or quotes.";
    }
}
