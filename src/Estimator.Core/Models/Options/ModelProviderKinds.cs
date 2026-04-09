namespace Estimator.Core.Models.Options
{
    public static class ModelProviderKinds
    {
        public const string LlamaCppServer = "LlamaCppServer";
        public const string LLamaSharp = "LLamaSharp";
        public const string Ollama = "Ollama";
    }

    public static class PromptFormatKinds
    {
        public const string Gemma = "Gemma";
        public const string Generic = "Generic";
    }
}
