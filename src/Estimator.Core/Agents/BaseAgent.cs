using Estimator.Core.Services;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;

namespace Estimator.Core.Agents
{
    public abstract class BaseAgent
    {
        protected readonly LlamaModelService _modelService;
        protected readonly ILogger _logger;

        protected abstract string SystemPrompt { get; }

        protected BaseAgent(LlamaModelService modelService, ILogger logger)
        {
            _modelService = modelService;
            _logger = logger;
        }

        public async Task<string> ExecuteAsync(string userInput)
        {
            _logger.LogInformation("Agent {AgentType} is processing request...", GetType().Name);

            var executor = _modelService.GetExecutor();

            var inferenceParams = new InferenceParams
            {
                MaxTokens = 2048,
                AntiPrompts = new[] { "User:" },
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.7f,
                    TopP = 0.9f
                }
            };

            string fullPrompt = $"<|im_start|>system\n{SystemPrompt}<|im_end|>\n<|im_start|>user\n{userInput}<|im_end|>\n<|im_start|>assistant\n";

            string response = string.Empty;

            await foreach (var text in executor.InferAsync(fullPrompt, inferenceParams))
            {
                response += text;
                Console.Write(text);
            }
            Console.WriteLine();

            _logger.LogDebug("Agent {AgentType} response: {Response}", GetType().Name, response);
            return response.Trim();
        }
    }
}
