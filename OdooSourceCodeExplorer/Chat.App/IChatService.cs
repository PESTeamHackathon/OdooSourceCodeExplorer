namespace Chat.App;

public interface IChatService
{
    ChatCapabilities Capabilities { get; }
    IReadOnlyList<ChatMessage> Messages { get; }
    Task SendAsync(string question, CancellationToken cancellationToken = default) => SendAsync(question, [], cancellationToken);
    Task SendAsync(string question, IReadOnlyList<ChatAttachment> attachments, CancellationToken cancellationToken = default);
    Task SendAsync(string question, IReadOnlyList<ChatAttachment> attachments, ChatMode mode, CancellationToken cancellationToken = default);
    Task SendAsync(string question, IReadOnlyList<ChatAttachment> attachments, ChatMode mode, bool pureMode, CancellationToken cancellationToken = default);
    void Clear();
}
