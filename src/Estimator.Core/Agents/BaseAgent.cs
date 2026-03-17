using Microsoft.Extensions.Logging;

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
            _logger.LogInformation("Agent {Role} started processing.", Role);

            var response = await _modelGateway.CompleteAsync(Role, SystemPrompt, userInput, cancellationToken);

            _logger.LogDebug("Agent {Role} raw response length: {Length}", Role, response.Length);
            return response;
        }
    }
}
