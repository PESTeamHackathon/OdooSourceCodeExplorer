namespace Chat.App;

// Stateless answering for either mode; contains no provider or filesystem types.
public interface IConversationAnswerer
{
    Task<ConversationAnswer> AnswerAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);
}

public sealed record ConversationAnswer(string Text, ChatEvidenceTrace? Trace);
