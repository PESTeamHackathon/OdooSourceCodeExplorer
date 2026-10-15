using Chat.App.Retrieval;

namespace Chat.App;

// Register per conversation (a Blazor circuit in this milestone), never as a singleton.
public sealed class ChatService(IAnswerGenerator answerGenerator, ICodeRetriever retriever, KnowledgeChatOptions options) : IChatService
{
    public const int MaxQuestionLength = 4_000;
    public const int MaxTurns = 20;
    private IReadOnlyList<ChatMessage> _messages = Array.Empty<ChatMessage>();
    private int _busy;

    public IReadOnlyList<ChatMessage> Messages => _messages;
    // Internal developer diagnostics; excluded from the business-facing service contract.
    public ChatEvidenceTrace? LastTrace { get; private set; }

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
            RetrievalResult retrieved;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(options.RetrievalTimeoutSeconds));
                try
                {
                    if (string.IsNullOrWhiteSpace(options.ExpectedApplicationVersion))
                        throw new InvalidOperationException("An expected application version is required for knowledge chat.");
                    retrieved = await retriever.RetrieveAsync(new(BuildRetrievalQuestion(question), options.RetrievalLimit,
                        ExpectedApplicationVersion: options.ExpectedApplicationVersion), deadline.Token);
                    if (retrieved.Index.ApplicationVersion != options.ExpectedApplicationVersion)
                        throw new InvalidOperationException("Knowledge version mismatch.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    throw new AnswerGenerationException("Application knowledge is unavailable or incompatible. Ask the developer to check the selected knowledge version and local search services, then retry.", ex);
                }
            }
            var selected = new List<RetrievedChunk>();
            var usedCharacters = 0;
            foreach (var hit in retrieved.Hits)
            {
                var size = hit.Evidence.Content.Length + hit.Evidence.Context.Length;
                if (size + usedCharacters > options.MaxEvidenceCharacters) continue;
                selected.Add(hit);
                usedCharacters += size;
            }
            var request = new AnswerRequest(Array.AsReadOnly(context), retrieved.Index, selected.AsReadOnly(), selected.Count != retrieved.Hits.Count);
            request = request with { Concise = System.Text.RegularExpressions.Regex.IsMatch(question, @"(?i)\b(summary|summari[sz]e|brief|one sentence)\b") };
            var draft = selected.Count == 0 ? new AnswerDraft("insufficient", []) : await answerGenerator.GenerateAsync(request, cancellationToken);
            var answer = BusinessAnswerPolicy.Render(draft, request);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(answer))
                throw new InvalidOperationException("The answer provider returned an empty response. Please try again.");

            // Commit a complete turn only after success; failed or canceled turns are retryable.
            _messages = Array.AsReadOnly<ChatMessage>([.. context, new(ChatRole.Assistant, answer)]);
            var accepted = answer != BusinessAnswerPolicy.Insufficient && answer != BusinessAnswerPolicy.Conflicting;
            LastTrace = new(retrieved.Index, selected.Select(h => new EvidenceReference(h.Evidence.Id, h.Evidence.RelativePath, h.Evidence.StartLine, h.Evidence.EndLine)).ToArray(),
                accepted ? (draft.Claims ?? []).SelectMany(c => c.EvidenceIds ?? []).Distinct().ToArray() : [],
                accepted ? draft.Status : answer == BusinessAnswerPolicy.Conflicting ? "conflicting" : "insufficient", request.EvidenceOmitted);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public void Clear()
    {
        Enter();
        try { _messages = Array.Empty<ChatMessage>(); LastTrace = null; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private string BuildRetrievalQuestion(string question)
    {
        // A self-contained new question must not be drowned out by an older topic.
        // Resolve short follow-ups from the latest substantive user question, never an assistant claim.
        if (!IsFollowUp(question)) return question;
        var users = _messages.Where(m => m.Role == ChatRole.User).Select(m => m.Content).ToArray();
        var anchor = users.LastOrDefault(q => !IsFollowUp(q)) ?? users.LastOrDefault();
        var context = string.Join("\n", new[] { anchor, users.LastOrDefault() }.OfType<string>().Distinct()
            .Select(q => q[..Math.Min(500, q.Length)]));
        var remaining = MaxQuestionLength - question.Length - 60;
        if (context.Length == 0 || remaining <= 0) return question;
        return "Earlier user questions:\n" + context[..Math.Min(context.Length, remaining)] + "\nCurrent question:\n" + question;
    }

    private static bool IsFollowUp(string question) => System.Text.RegularExpressions.Regex.IsMatch(question,
        @"(?i)(^\s*(and\b|what if\b|what about\b|how about\b|summari[sz]e\b|in that case\b)|\b(it|that|those|them)\b)");

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
