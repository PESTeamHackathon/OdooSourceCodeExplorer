using Chat.App.Retrieval;

namespace Chat.App;

// Stateless indexed answering; conversation ownership stays with ChatService.
public sealed class IndexedConversationAnswerer(IAnswerGenerator _answerGenerator, ICodeRetriever _retriever, KnowledgeChatOptions _options) : IConversationAnswerer
{
    public Task<ConversationAnswer> AnswerAsync(IReadOnlyList<ChatMessage> context, CancellationToken cancellationToken = default) =>
        AnswerAsync(context, false, cancellationToken);

    public Task<ConversationAnswer> AnswerAsync(IReadOnlyList<ChatMessage> context, bool pureMode, CancellationToken cancellationToken = default) =>
        AnswerAsync(context, pureMode, null, cancellationToken);

    public async Task<ConversationAnswer> AnswerAsync(IReadOnlyList<ChatMessage> context, bool pureMode, IProgress<ChatProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new(ChatProgressStep.Searching));
        var question = context[^1].Content;
        RetrievalResult retrieved;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(_options.RetrievalTimeoutSeconds));
            try
            {
                if (string.IsNullOrWhiteSpace(_options.ExpectedApplicationVersion))
                    throw new InvalidOperationException("An expected application version is required for knowledge chat.");
                retrieved = await _retriever.RetrieveAsync(new(BuildRetrievalQuestion(context, question), _options.RetrievalLimit,
                    ExpectedApplicationVersion: _options.ExpectedApplicationVersion), deadline.Token);
                if (retrieved.Index.ApplicationVersion != _options.ExpectedApplicationVersion)
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
            if (size + usedCharacters > _options.MaxEvidenceCharacters) continue;
            selected.Add(hit);
            usedCharacters += size;
        }
        var request = new AnswerRequest(context, retrieved.Index, selected.AsReadOnly(), selected.Count != retrieved.Hits.Count, PureMode: pureMode);
        progress?.Report(new(ChatProgressStep.Writing, SectionsRead: selected.Count));
        // Pure mode asks Copilot even without evidence; normal mode never lets it answer unsupported.
        var draft = selected.Count == 0 && !pureMode ? new AnswerDraft("insufficient", []) : await _answerGenerator.GenerateAsync(request, cancellationToken);
        var answer = BusinessAnswerPolicy.Render(draft, request);
        var accepted = answer != BusinessAnswerPolicy.Insufficient && answer != BusinessAnswerPolicy.Conflicting;
        var trace = new ChatEvidenceTrace(retrieved.Index, selected.Select(h => new EvidenceReference(h.Evidence.Id, h.Evidence.RelativePath, h.Evidence.StartLine, h.Evidence.EndLine)).ToArray(),
            accepted ? (draft.Claims ?? []).SelectMany(c => c.EvidenceIds ?? []).Distinct().ToArray() : [],
            accepted ? draft.Status : answer == BusinessAnswerPolicy.Conflicting ? "conflicting" : "insufficient", request.EvidenceOmitted);
        return new(answer, trace);
    }

    private static string BuildRetrievalQuestion(IReadOnlyList<ChatMessage> messages, string question)
    {
        var previous = messages.Take(messages.Count - 1).Where(m => m.Role == ChatRole.User).TakeLast(3)
            .Select(m => m.Content[..Math.Min(500, m.Content.Length)]);
        var context = string.Join("\n", previous);
        var remaining = ChatService.MaxQuestionLength - question.Length - 60;
        if (context.Length == 0 || remaining <= 0) return question;
        return "Earlier user questions:\n" + context[..Math.Min(context.Length, remaining)] + "\nCurrent question:\n" + question;
    }

}
