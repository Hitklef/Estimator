namespace Estimator.Core.Models.Options
{
    public class AiSettings
    {
        public const string SectionName = "AiSettings";

        public string ModelUrl { get; set; } = string.Empty;
        public string ModelFileName { get; set; } = string.Empty;
        public string ModelsDirectory { get; set; } = "Models";


        public uint ContextSize { get; set; } = 4096;
        public int GpuLayerCount { get; set; } = 20;


        public string LocalModelPath => Path.Combine(AppContext.BaseDirectory, ModelsDirectory, ModelFileName);
    }
}
