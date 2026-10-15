namespace Chat.App;

// Register per conversation (a Blazor circuit in this milestone), never as a singleton.
public sealed class ChatService(IAnswerGenerator answerGenerator) : IChatService
{
    public const int MaxQuestionLength = 4_000;
    public const int MaxTurns = 20;
    private IReadOnlyList<ChatMessage> _messages = Array.Empty<ChatMessage>();
    private int _busy;

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public Task SendAsync(string question, CancellationToken cancellationToken = default) =>
        SendAsync(question, [], cancellationToken);

    public async Task SendAsync(string question, IReadOnlyList<ChatAttachment> attachments, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > MaxQuestionLength)
            throw new ArgumentException($"Enter a question between 1 and {MaxQuestionLength} characters.", nameof(question));
        ValidateAttachments(attachments);

        Enter();
        try
        {
            if (_messages.Count >= MaxTurns * 2)
                throw new InvalidOperationException("This conversation is full. Start a new conversation to continue.");

            var conversationBytes = _messages.Sum(m => m.Attachments?.Sum(a => (long)a.Data.Length) ?? 0)
                + attachments.Sum(a => (long)a.Data.Length);
            if (conversationBytes > ChatAttachment.MaxConversationBytes)
                throw new InvalidOperationException("This conversation has too many attached files. Start a new conversation to continue.");

            ChatMessage[] context = [.. _messages, new(ChatRole.User, question.Trim(), attachments.Count == 0 ? null : [.. attachments])];
            var answer = await answerGenerator.GenerateAsync(Array.AsReadOnly(context), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(answer))
                throw new InvalidOperationException("The answer provider returned an empty response. Please try again.");

            // Commit a complete turn only after success; failed or canceled turns are retryable.
            _messages = Array.AsReadOnly<ChatMessage>([.. context, new(ChatRole.Assistant, answer)]);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public void Clear()
    {
        Enter();
        try { _messages = Array.Empty<ChatMessage>(); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private static void ValidateAttachments(IReadOnlyList<ChatAttachment> attachments)
    {
        if (attachments.Count > ChatAttachment.MaxPerMessage)
            throw new ArgumentException($"Attach at most {ChatAttachment.MaxPerMessage} files per message.", nameof(attachments));
        foreach (var attachment in attachments)
        {
            if (!ChatAttachment.TryGetMediaType(attachment.FileName, out var mediaType) || mediaType != attachment.MediaType)
                throw new ArgumentException($"“{attachment.FileName}” is not a supported file type.", nameof(attachments));
            if (attachment.Data.Length == 0 || attachment.Data.Length > attachment.MaxBytes)
                throw new ArgumentException(
                    $"“{attachment.FileName}” must be between 1 byte and {attachment.MaxBytes / 1024} KB.", nameof(attachments));
            if (attachment.Kind == AttachmentKind.Text && !IsUtf8(attachment.Data))
                throw new ArgumentException($"“{attachment.FileName}” is not a UTF-8 text file.", nameof(attachments));
        }
    }

    private static bool IsUtf8(byte[] data)
    {
        try
        {
            _ = new System.Text.UTF8Encoding(false, true).GetString(data);
            return true;
        }
        catch (System.Text.DecoderFallbackException) { return false; }
    }

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Please wait for the current answer to finish.");
    }
}
