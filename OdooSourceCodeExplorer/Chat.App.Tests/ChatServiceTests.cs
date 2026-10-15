using Chat.App;

namespace Chat.App.Tests;

public sealed class ChatServiceTests
{
    [Fact]
    public async Task FollowUpIncludesPreviousExchange()
    {
        var provider = new StubGenerator();
        var chat = CreateChat(provider);
        await chat.SendAsync("  What is a credit memo?  ");
        await chat.SendAsync("Summarize that.");

        Assert.Equal(3, provider.LastMessages!.Count);
        Assert.Equal(new ChatMessage(ChatRole.User, "What is a credit memo?"), provider.LastMessages[0]);
        Assert.Equal(ChatRole.Assistant, provider.LastMessages[1].Role);
        Assert.Equal("Summarize that.", provider.LastMessages[2].Content);
        Assert.Equal(4, chat.Messages.Count);
    }

    [Fact]
    public async Task FailedTurnDoesNotCorruptHistoryAndCanBeRetried()
    {
        var provider = new StubGenerator();
        var chat = CreateChat(provider);
        await chat.SendAsync("First question");
        provider.Answer = (_, _) => throw new AnswerGenerationException("Unavailable");
        await Assert.ThrowsAsync<AnswerGenerationException>(() => chat.SendAsync("Retry me"));
        Assert.Equal(2, chat.Messages.Count);
        provider.Answer = (_, _) => Task.FromResult("Recovered");
        await chat.SendAsync("Retry me");
        Assert.Equal(4, chat.Messages.Count);
    }

    [Fact]
    public async Task CancellationPreservesHistory()
    {
        var provider = new StubGenerator { Answer = (_, ct) => Task.FromCanceled<string>(ct) };
        var chat = CreateChat(provider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat.SendAsync("Question", cancellation.Token));
        Assert.Empty(chat.Messages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyQuestionsNeverCallProvider(string question)
    {
        var provider = new StubGenerator();
        await Assert.ThrowsAsync<ArgumentException>(() => CreateChat(provider).SendAsync(question));
        Assert.Null(provider.LastMessages);
    }

    [Fact]
    public async Task OversizedQuestionNeverCallsProvider()
    {
        var provider = new StubGenerator();
        await Assert.ThrowsAsync<ArgumentException>(() => CreateChat(provider).SendAsync(new string('a', 4001)));
        Assert.Null(provider.LastMessages);
    }

    [Fact]
    public async Task EmptyProviderAnswerDoesNotCommitTurn()
    {
        var chat = CreateChat(new StubGenerator { Answer = (_, _) => Task.FromResult(" ") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendAsync("Question"));
        Assert.Empty(chat.Messages);
    }

    [Fact]
    public async Task ConversationsAreIsolatedAndCanBeCleared()
    {
        var provider = new StubGenerator();
        var first = CreateChat(provider);
        var second = CreateChat(provider);
        await first.SendAsync("Private question");
        Assert.Empty(second.Messages);
        first.Clear();
        await first.SendAsync("New conversation");
        Assert.Single(provider.LastMessages!);
    }

    [Fact]
    public async Task ConversationLimitRequiresExplicitReset()
    {
        var chat = CreateChat(new StubGenerator());
        for (var i = 0; i < ChatService.MaxTurns; i++) await chat.SendAsync("Question");
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendAsync("Another question"));
        Assert.Equal(ChatService.MaxTurns * 2, chat.Messages.Count);
        chat.Clear();
        await chat.SendAsync("New conversation");
        Assert.Equal(2, chat.Messages.Count);
    }

    [Fact]
    public async Task ConcurrentTurnsAndResetAreRejectedWhileAnswerIsPending()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = CreateChat(new StubGenerator { Answer = (_, _) => pending.Task });
        var first = chat.SendAsync("First");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendAsync("Second"));
            Assert.Throws<InvalidOperationException>(chat.Clear);
        }
        finally { pending.SetResult("Answer"); }
        await first;
        Assert.Equal(2, chat.Messages.Count);
    }

    [Fact]
    public async Task AttachmentsReachProviderAndStayInHistory()
    {
        var provider = new StubGenerator();
        var chat = CreateChat(provider);
        var image = new ChatAttachment("screen.png", "image/png", [1, 2, 3]);
        await chat.SendAsync("What is shown?", [image]);
        await chat.SendAsync("And in more detail?");

        Assert.Equal([image], provider.LastMessages![0].Attachments!);
        Assert.Null(provider.LastMessages[2].Attachments);
    }

    [Theory]
    [InlineData("report.pdf", "text/plain", 10)]
    [InlineData("notes.txt", "image/png", 10)]
    [InlineData("empty.txt", "text/plain", 0)]
    [InlineData("big.txt", "text/plain", ChatAttachment.MaxTextBytes + 1)]
    [InlineData("big.png", "image/png", ChatAttachment.MaxImageBytes + 1)]
    public async Task InvalidAttachmentsNeverCallProvider(string fileName, string mediaType, int size)
    {
        var provider = new StubGenerator();
        var attachment = new ChatAttachment(fileName, mediaType, new byte[size]);
        await Assert.ThrowsAsync<ArgumentException>(() => CreateChat(provider).SendAsync("Question", [attachment]));
        Assert.Null(provider.LastMessages);
    }

    [Fact]
    public async Task NonUtf8TextAttachmentIsRejected()
    {
        var provider = new StubGenerator();
        var attachment = new ChatAttachment("data.csv", "text/plain", [0xC3, 0x28]);
        await Assert.ThrowsAsync<ArgumentException>(() => CreateChat(provider).SendAsync("Question", [attachment]));
        Assert.Null(provider.LastMessages);
    }

    [Fact]
    public async Task TooManyAttachmentsAreRejected()
    {
        var provider = new StubGenerator();
        var files = Enumerable.Range(0, ChatAttachment.MaxPerMessage + 1)
            .Select(i => new ChatAttachment($"f{i}.txt", "text/plain", [65])).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => CreateChat(provider).SendAsync("Question", files));
        Assert.Null(provider.LastMessages);
    }

    private static ChatService CreateChat(IAnswerGenerator provider) => new(provider, new StubRetriever(),
        new KnowledgeChatOptions { ExpectedApplicationVersion = "test" });

    private sealed class StubRetriever : Chat.App.Retrieval.ICodeRetriever
    {
        public Task<Chat.App.Retrieval.RetrievalResult> RetrieveAsync(Chat.App.Retrieval.RetrievalQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new Chat.App.Retrieval.RetrievalResult(new("fixture", "source", "commit", "test", false, 1),
                [new(new("fixture-id", "python", "models/fixture.py", "sale", "addons/sale", "method", "do_action", "SaleOrder.do_action", "SaleOrder", 1, 2, "class SaleOrder:", "pass"), 0.1, 1, 1)]));
    }

    private sealed class StubGenerator : IAnswerGenerator
    {
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }
        public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>> Answer { get; set; }
            = (_, _) => Task.FromResult("General business information.");

        public async Task<AnswerDraft> GenerateAsync(AnswerRequest request, CancellationToken cancellationToken = default)
        {
            LastMessages = request.Messages;
            return new AnswerDraft("supported", [new(await Answer(request.Messages, cancellationToken), ["fixture-id"])]);
        }
    }
}
