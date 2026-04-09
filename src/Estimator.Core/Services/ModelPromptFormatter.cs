using Estimator.Core.Models.Options;
using Microsoft.Extensions.Options;

namespace Estimator.Core.Services
{
    public sealed class ModelPromptFormatter
    {
        private readonly AiSettings _settings;

        public ModelPromptFormatter(IOptions<AiSettings> options)
        {
            _settings = options.Value;
        }

        public string BuildPrompt(string systemPrompt, string userInput)
        {
            var normalizedSystem = Normalize(systemPrompt);
            var normalizedUser = Normalize(userInput);

            if (_settings.PromptFormat.Equals(PromptFormatKinds.Gemma, StringComparison.OrdinalIgnoreCase))
            {
                return BuildGemmaPrompt(normalizedSystem, normalizedUser);
            }

            return BuildGenericPrompt(normalizedSystem, normalizedUser);
        }

        private string BuildGemmaPrompt(string systemPrompt, string userInput)
        {
            if (_settings.MergeSystemPromptIntoUserMessage)
            {
                var combined = string.IsNullOrWhiteSpace(systemPrompt)
                    ? userInput
                    : $"{systemPrompt}\n\n{userInput}".Trim();

                return
                    $"<start_of_turn>user\n{combined}<end_of_turn>\n" +
                    "<start_of_turn>model\n";
            }

            if (string.IsNullOrWhiteSpace(systemPrompt))
            {
                return
                    $"<start_of_turn>user\n{userInput}<end_of_turn>\n" +
                    "<start_of_turn>model\n";
            }

            return
                $"<start_of_turn>system\n{systemPrompt}<end_of_turn>\n" +
                $"<start_of_turn>user\n{userInput}<end_of_turn>\n" +
                "<start_of_turn>model\n";
        }

        private static string BuildGenericPrompt(string systemPrompt, string userInput)
        {
            if (string.IsNullOrWhiteSpace(systemPrompt))
            {
                return $"User:\n{userInput}\n\nAssistant:\n";
            }

            return
                $"System:\n{systemPrompt}\n\n" +
                $"User:\n{userInput}\n\n" +
                "Assistant:\n";
        }

        private static string Normalize(string value) => value?.Trim() ?? string.Empty;
    }
}
