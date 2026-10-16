using Chat.App.Indexing;
using Chat.App.Retrieval;

namespace Chat.App.Tests;

public sealed class CodeAwareChatTests
{
    private static readonly KnowledgeIndexInfo Index = new("generation", "source", "commit", "v1", false, 1);
    private static readonly RetrievedChunk Hit = new(new CodeChunk("evidence-1", "python", "addons/sale/models/sale_order.py", "sale", "addons/sale",
        "method", "action_cancel", "SaleOrder.action_cancel", "SaleOrder", 1, 3, "class SaleOrder:", "if order.locked: raise UserError('Unlock first')"), 0.1, 1, 1);

    [Fact]
    public async Task FollowUpsRetrieveFreshEvidenceUsingUserContextAndKeepSourcesOutOfTranscript()
    {
        var retrieval = new FakeRetriever();
        var generator = new FakeGenerator();
        var chat = Create(retrieval, generator);
        await chat.SendAsync("Can I cancel a locked sales order?");
        await chat.SendAsync("What if I unlock it first?");
        Assert.Equal(2, retrieval.Calls);
        Assert.Contains("Can I cancel a locked sales order?", retrieval.Query!.Question);
        Assert.Contains("What if I unlock it first?", retrieval.Query.Question);
        Assert.DoesNotContain("Unlock the order before cancelling", retrieval.Query.Question);
        Assert.Equal(3, generator.Request!.Messages.Count);
        Assert.Equal(Hit, Assert.Single(generator.Request.Evidence));
        Assert.Equal("v1", retrieval.Query.ExpectedApplicationVersion);
        Assert.All(chat.Messages, m => Assert.DoesNotContain("evidence-1", m.Content));
        Assert.All(chat.Messages, m => Assert.DoesNotContain("sale_order.py", m.Content));
        Assert.Equal("commit", chat.LastTrace!.Index.GitCommit);
        Assert.Equal("evidence-1", Assert.Single(chat.LastTrace.UsedEvidenceIds));
        chat.Clear();
        Assert.Null(chat.LastTrace);
        await chat.SendAsync("A new topic");
        Assert.DoesNotContain("locked", retrieval.Query!.Question);
    }

    [Fact]
    public async Task PureModeReachesGeneratorAndReturnsRawTextUnchecked()
    {
        const string raw = "Call `action_cancel` on `sale.order`; see addons/sale/models/sale_order.py.";
        var generator = new FakeGenerator { Draft = new(AnswerDraft.Pure, [new(raw, [])]) };
        var chat = Create(new FakeRetriever(), generator);
        await chat.SendAsync("How do I cancel?", [], ChatMode.Indexed, pureMode: true);
        Assert.True(generator.Request!.PureMode);
        Assert.Equal(raw, chat.Messages[^1].Content);
    }

    [Fact]
    public async Task PureModeAsksCopilotEvenWithoutEvidence()
    {
        var generator = new FakeGenerator { Draft = new(AnswerDraft.Pure, [new("General answer.", [])]) };
        var chat = Create(new FakeRetriever { Hits = [] }, generator);
        await chat.SendAsync("Anything?", [], ChatMode.Indexed, pureMode: true);
        Assert.NotNull(generator.Request);
        Assert.Equal("General answer.", chat.Messages[^1].Content);
    }

    [Fact]
    public async Task NormalModeNeverPublishesAPureDraft()
    {
        var generator = new FakeGenerator { Draft = new(AnswerDraft.Pure, [new("Unchecked text.", [])]) };
        var chat = Create(new FakeRetriever(), generator);
        await Assert.ThrowsAsync<AnswerGenerationException>(() => chat.SendAsync("How do I cancel?"));
        Assert.False(generator.Request!.PureMode);
        Assert.Empty(chat.Messages);
    }

    [Fact]
    public async Task NoEvidenceSkipsCopilotAndAcknowledgesInsufficiency()
    {
        var generator = new FakeGenerator();
        var chat = Create(new FakeRetriever { Hits = [] }, generator);
        await chat.SendAsync("Explain a missing workflow");
        Assert.Null(generator.Request);
        Assert.Equal(BusinessAnswerPolicy.Insufficient, chat.Messages[^1].Content);
        Assert.Equal("insufficient", chat.LastTrace!.Status);
    }

    [Fact]
    public async Task EvidenceBudgetDropsWholeChunksAndFlagsOmissions()
    {
        var large = Hit with { Evidence = Hit.Evidence with { Id = "large", Content = new string('x', 4000) } };
        var generator = new FakeGenerator();
        var chat = Create(new FakeRetriever { Hits = [large, Hit] }, generator, new() { ExpectedApplicationVersion = "v1", MaxEvidenceCharacters = 1000 });
        await chat.SendAsync("Explain cancellation");
        Assert.Equal(Hit, Assert.Single(generator.Request!.Evidence));
        Assert.True(generator.Request.EvidenceOmitted);
    }

    [Theory]
    [InlineData("Action: action_cancel", "evidence-1")]
    [InlineData("Look at addons/sale/models/sale_order.py", "evidence-1")]
    [InlineData("SaleOrder handles this.", "evidence-1")]
    [InlineData("Use `code`.", "evidence-1")]
    [InlineData("A claim with an invented source", "invented")]
    public async Task UnsafeOrUncitedClaimsNeverReachBusinessTranscript(string text, string id)
    {
        var chat = Create(new FakeRetriever(), new FakeGenerator { Draft = new("supported", [new(text, [id])]) });
        await chat.SendAsync("Question");
        Assert.Equal(BusinessAnswerPolicy.Insufficient, chat.Messages[^1].Content);
        Assert.Equal("insufficient", chat.LastTrace!.Status);
        Assert.Empty(chat.LastTrace.UsedEvidenceIds);
    }

    [Theory]
    [InlineData("insufficient")]
    [InlineData("conflicting")]
    public async Task UnsupportedStatusDoesNotPublishModelSpeculation(string status)
    {
        var chat = Create(new FakeRetriever(), new FakeGenerator { Draft = new(status, [new("Unsupported speculation", ["evidence-1"])]) });
        await chat.SendAsync("Question");
        Assert.DoesNotContain("speculation", chat.Messages[^1].Content);
        Assert.Equal(status, chat.LastTrace!.Status);
    }

    [Fact]
    public async Task RetrievalFailureIsSafeAndDoesNotCommitOrCallCopilot()
    {
        var generator = new FakeGenerator();
        var chat = Create(new FakeRetriever { Failure = new IOException("private/path/secret.py") }, generator);
        var error = await Assert.ThrowsAsync<AnswerGenerationException>(() => chat.SendAsync("Question"));
        Assert.DoesNotContain("secret.py", error.Message);
        Assert.Empty(chat.Messages);
        Assert.Null(generator.Request);
    }

    [Fact]
    public async Task WrongVersionIsRejectedBeforeCopilotEvenIfRetrieverDidNotEnforceIt()
    {
        var generator = new FakeGenerator();
        var chat = Create(new FakeRetriever { Info = Index with { ApplicationVersion = "other" } }, generator);
        await Assert.ThrowsAsync<AnswerGenerationException>(() => chat.SendAsync("Question"));
        Assert.Null(generator.Request);
    }

    [Fact]
    public async Task CancellationDuringRetrievalLeavesNoTurn()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var chat = Create(new FakeRetriever(), new FakeGenerator());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat.SendAsync("Question", cancellation.Token));
        Assert.Empty(chat.Messages);
    }

    [Fact]
    public async Task PartialAnswerSeparatesKnownBehaviorFromUnverifiedRemainder()
    {
        var chat = Create(new FakeRetriever(), new FakeGenerator { Draft = new("partial", [new("Unlock the order before cancelling.", ["evidence-1"])]) });
        await chat.SendAsync("Question");
        Assert.Contains("Unlock the order", chat.Messages[^1].Content);
        Assert.Contains("not enough to confirm the rest", chat.Messages[^1].Content);
    }

    private static ChatService Create(ICodeRetriever retrieval, IAnswerGenerator generator, KnowledgeChatOptions? options = null)
        => new(generator, retrieval, options ?? new() { ExpectedApplicationVersion = "v1" });
    private sealed class FakeGenerator : IAnswerGenerator
    {
        public AnswerRequest? Request { get; private set; }
        public AnswerDraft Draft { get; init; } = new("supported", [new("Unlock the order before cancelling.", ["evidence-1"])]);
        public Task<AnswerDraft> GenerateAsync(AnswerRequest request, CancellationToken cancellationToken = default)
        { Request = request; return Task.FromResult(Draft); }
    }
    private sealed class FakeRetriever : ICodeRetriever
    {
        public RetrievalQuery? Query { get; private set; }
        public int Calls { get; private set; }
        public KnowledgeIndexInfo Info { get; init; } = Index;
        public IReadOnlyList<RetrievedChunk> Hits { get; init; } = [Hit];
        public Exception? Failure { get; init; }
        public Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Query = query; Calls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new RetrievalResult(Info, Hits));
        }
    }
}
