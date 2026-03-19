namespace Estimator.Core.Models
{
    public sealed class ModelCapacityException : Exception
    {
        public const string ErrorCode = "MODEL_CONTEXT_LIMIT";
        public const string DefaultMessage =
            "The uploaded document is too large or complex for a single AI pass. Please upload a shorter brief, remove repeated/irrelevant sections, or split it into smaller parts.";

        public ModelCapacityException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }

        public static ModelCapacityException CreateDefault(Exception? innerException = null) =>
            new(DefaultMessage, innerException);
    }
}
