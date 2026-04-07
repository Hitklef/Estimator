using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Estimator.Core.Agents
{
    public enum AgentRole
    {
        Decomposer,
        Estimator,
        Validator
    }

    public interface IAgent
    {
        AgentRole Role { get; }
        Task<string> ExecuteAsync(string userInput, CancellationToken cancellationToken = default);
    }

    public interface IAgentModelGateway
    {
        Task<string> CompleteAsync(
            AgentRole role,
            string systemPrompt,
            string userInput,
            CancellationToken cancellationToken = default);
    }

    public abstract class BaseAgent : IAgent
    {
        private readonly IAgentModelGateway _modelGateway;
        private readonly ILogger _logger;

        protected abstract string SystemPrompt { get; }
        public abstract AgentRole Role { get; }

        protected BaseAgent(IAgentModelGateway modelGateway, ILogger logger)
        {
            _modelGateway = modelGateway;
            _logger = logger;
        }

        public async Task<string> ExecuteAsync(string userInput, CancellationToken cancellationToken = default)
        {
            var safeUserInput = userInput ?? string.Empty;
            var stopwatch = Stopwatch.StartNew();
            var inputLength = safeUserInput.Length;
            _logger.LogInformation("Agent {Role} started processing. InputChars={InputChars}", Role, inputLength);

            var response = await _modelGateway.CompleteAsync(Role, SystemPrompt, safeUserInput, cancellationToken);

            stopwatch.Stop();
            _logger.LogInformation(
                "Agent {Role} completed. OutputChars={OutputChars} DurationMs={DurationMs}",
                Role,
                response.Length,
                stopwatch.ElapsedMilliseconds);

            _logger.LogDebug("Agent {Role} raw response length: {Length}", Role, response.Length);
            return response;
        }
    }
}
