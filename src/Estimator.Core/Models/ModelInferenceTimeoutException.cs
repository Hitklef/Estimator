namespace Estimator.Core.Models
{
    public sealed class ModelInferenceTimeoutException : Exception
    {
        public const string ErrorCode = "MODEL_INFERENCE_TIMEOUT";
        public const string DefaultMessage =
            "The AI model took too long to respond. Please retry, reduce input size, or increase inference timeout settings.";

        public ModelInferenceTimeoutException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }

        public static ModelInferenceTimeoutException CreateDefault(Exception? innerException = null) =>
            new(DefaultMessage, innerException);
    }
}
