namespace Chat.App;

public interface IChatService
{
    IReadOnlyList<ChatMessage> Messages { get; }
    Task SendAsync(string question, CancellationToken cancellationToken = default) => SendAsync(question, [], cancellationToken);
    Task SendAsync(string question, IReadOnlyList<ChatAttachment> attachments, CancellationToken cancellationToken = default);
    void Clear();
}
