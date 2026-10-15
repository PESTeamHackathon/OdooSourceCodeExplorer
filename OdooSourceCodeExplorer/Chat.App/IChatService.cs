namespace Chat.App;

public interface IChatService
{
    IReadOnlyList<ChatMessage> Messages { get; }
    Task SendAsync(string question, CancellationToken cancellationToken = default);
    void Clear();
}
