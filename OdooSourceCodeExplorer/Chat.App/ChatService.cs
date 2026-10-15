namespace Chat.App;

// Register per conversation (a Blazor circuit in this milestone), never as a singleton.
public sealed class ChatService(IAnswerGenerator answerGenerator) : IChatService
{
    public const int MaxQuestionLength = 4_000;
    public const int MaxTurns = 20;
    private IReadOnlyList<ChatMessage> _messages = Array.Empty<ChatMessage>();
    private int _busy;

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public async Task SendAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > MaxQuestionLength)
            throw new ArgumentException($"Enter a question between 1 and {MaxQuestionLength} characters.", nameof(question));

        Enter();
        try
        {
            if (_messages.Count >= MaxTurns * 2)
                throw new InvalidOperationException("This conversation is full. Start a new conversation to continue.");

            ChatMessage[] context = [.. _messages, new(ChatRole.User, question.Trim())];
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

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Please wait for the current answer to finish.");
    }
}
