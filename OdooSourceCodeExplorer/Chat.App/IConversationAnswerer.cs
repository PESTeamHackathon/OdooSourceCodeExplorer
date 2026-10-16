namespace Chat.App;

// Stateless answering for either mode; contains no provider or filesystem types.
public interface IConversationAnswerer
{
    Task<ConversationAnswer> AnswerAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);
    // Pure mode sends no answer instructions to the model; answerers that cannot honor it must not ignore it.
    Task<ConversationAnswer> AnswerAsync(IReadOnlyList<ChatMessage> messages, bool pureMode, CancellationToken cancellationToken = default) =>
        pureMode ? throw new NotSupportedException("Pure mode is not supported by this answerer.") : AnswerAsync(messages, cancellationToken);
}

public sealed record ConversationAnswer(string Text, ChatEvidenceTrace? Trace);
