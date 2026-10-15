namespace Chat.App;

// Carries a safe user-facing message, independent of any provider's exception types.
public sealed class AnswerGenerationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
