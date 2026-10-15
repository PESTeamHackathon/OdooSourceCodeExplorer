namespace Chat.App;

public interface IAnswerGenerator
{
    Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);
}
