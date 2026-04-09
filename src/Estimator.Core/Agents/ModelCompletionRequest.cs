namespace Estimator.Core.Agents
{
    public enum ModelResponseFormat
    {
        Text,
        Json
    }

    public sealed class ModelCompletionRequest
    {
        public AgentRole Role { get; init; }
        public string SystemPrompt { get; init; } = string.Empty;
        public string UserInput { get; init; } = string.Empty;
        public ModelResponseFormat ResponseFormat { get; init; } = ModelResponseFormat.Text;
        public string? JsonSchema { get; init; }
        public bool EnableThinking { get; init; }
    }
}
