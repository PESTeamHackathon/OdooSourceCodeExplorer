using Chat.App;
using Chat.App.Retrieval;
using Chat.Infrastructure;
using Chat.Infrastructure.Exploration;
using Chat.Infrastructure.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.App.Tests;

public sealed class AgenticChatTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "agentic-chat-" + Guid.NewGuid().ToString("N"));
    public AgenticChatTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "order.py"), "if inventory_enabled:\n    cancel_unfinished_deliveries()\n");
    }

    private IConfiguration Configuration(string? enabled) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["KnowledgeChat:UseAgenticExploration"] = enabled,
        ["KnowledgeChat:ExpectedApplicationVersion"] = "test",
        ["SourceRepository:Path"] = _root
    }).Build();

    [Theory]
    [InlineData(null, null)]
    [InlineData("false", "false")]
    public async Task LegacyDefaultsAndExplicitOverridesSelectIndexedAnswers(string? enabled, string? explorationEnabled)
    {
        var configuration = Configuration(enabled);
        if (explorationEnabled is not null)
        {
            configuration["KnowledgeChat:IndexedAnswersEnabled"] = "true";
            configuration["KnowledgeChat:ExplorationEnabled"] = explorationEnabled;
            configuration["KnowledgeChat:DefaultMode"] = "Indexed";
        }
        configuration["SourceRepository:Path"] = "/nonexistent-unused-source";
        var services = new ServiceCollection().AddLogging().AddCopilotChat(configuration);
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(IConversationAnswerer));
        Assert.Contains(services, s => s.ServiceType == typeof(ICodeRetriever));
        services.AddSingleton<ICodeRetriever, Retriever>();
        var generator = new Generator();
        services.AddSingleton<IAnswerGenerator>(generator);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IChatService>().SendAsync("Cancel?");
        Assert.Equal(new ChatCapabilities(true, false, ChatMode.Indexed), scope.ServiceProvider.GetRequiredService<IChatService>().Capabilities);
        Assert.Null(generator.Request!.ExplorationContext);
        Assert.Equal("rag-fixture", generator.Request.Index.IndexId);
    }

    [Fact]
    public async Task ExplicitExplorationOnlyNeedsNoRagServicesAndKeepsConversationScopesAndFollowUps()
    {
        var config = Configuration("false");
        config["KnowledgeChat:IndexedAnswersEnabled"] = "false";
        config["KnowledgeChat:ExplorationEnabled"] = "true";
        config["KnowledgeChat:DefaultMode"] = "Exploration";
        config["Retrieval:OllamaUrl"] = "invalid-unused-url";
        config["Retrieval:IndexPath"] = "missing-index";
        config["KnowledgeChat:ExpectedApplicationVersion"] = "";
        var services = new ServiceCollection().AddLogging().AddCopilotChat(config);
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(ICodeRetriever) || s.ServiceType == typeof(IKnowledgeSearch) || s.ServiceType == typeof(RetrievalOptions));
        Assert.Contains(services, s => s.ServiceType == typeof(CopilotSourceExplorer));
        var explorer = new ConversationStub();
        services.AddSingleton<IConversationAnswerer>(explorer);
        await using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IChatService>();
        var second = secondScope.ServiceProvider.GetRequiredService<IChatService>();
        Assert.Equal(new ChatCapabilities(false, true, ChatMode.Exploration), first.Capabilities);
        await first.SendAsync("Cancel a sales order?");
        await first.SendAsync("What if a delivery is already completed?");
        Assert.Equal(3, explorer.Messages!.Count);
        Assert.Equal("Cancel a sales order?", explorer.Messages[0].Content);
        Assert.Equal(ChatRole.Assistant, explorer.Messages[1].Role);
        Assert.Empty(second.Messages);
        await second.SendAsync("A separate question");
        Assert.Single(explorer.Messages!);
        first.Clear();
        Assert.Empty(first.Messages);
    }

    [Fact]
    public async Task AgenticCancellationDoesNotCommitHistoryOrTraceAndAllowsRetry()
    {
        var stub = new ConversationStub();
        var chat = new ChatService(stub);
        await chat.SendAsync("First");
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stub.Answer = async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new("unreachable", null); };
        var pending = chat.SendAsync("Follow-up", stop.Token);
        await started.Task;
        Assert.Throws<InvalidOperationException>(chat.Clear);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendAsync("Overlap"));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(2, chat.Messages.Count);
        stub.Answer = _ => Task.FromResult(new ConversationAnswer("Retry succeeded.", null));
        await chat.SendAsync("Follow-up");
        Assert.Equal(4, chat.Messages.Count);
    }

    private static ExplorationReview RuntimeOnly(string scope = "source") => new(
        ExplorationLoop.Areas.Select(a => new CoverageItem(a, "resolved", "Behavior and optional conditions read.", ["source-1"])).ToArray(),
        [new("Is inventory integration enabled in the running database?", "Cannot establish database settings from source.", false, null, "runtime")], scope);

    [Fact]
    public async Task EstablishedConditionalBehaviorWithOnlyRuntimeUnknownIsComplete()
    {
        var source = new LocalSourceTools(_root);
        var outcome = await ExplorationLoop.RunAsync(source, async (_, _) => await source.ReadAsync("order.py", 1, 2),
            (_, _) => Task.FromResult(RuntimeOnly()), null, default);
        Assert.True(outcome.Complete);
        var request = ExplorationLoop.WithCompleteness(ExplorationLoop.CreateRequest("Cancel?", source), outcome, source);
        Assert.True(request.ExplorationContext!.SourceComplete);
        Assert.False(request.ExplorationContext.RequiresRuntimeConfirmation);
        var answer = ExplorationLoop.Render(new("supported", [new("If inventory integration is enabled, unfinished deliveries are cancelled.", ["source-1"])]), request, outcome, source);
        Assert.DoesNotContain("I can verify only", answer);
        Assert.DoesNotContain("Still unresolved", answer);
        Assert.Contains("If inventory integration", answer);
        Assert.Equal(1, answer.Split(ExplorationLoop.ConfigurationCaveat).Length - 1);
    }

    [Fact]
    public async Task DatabaseSpecificQuestionRetainsRuntimeUncertainty()
    {
        var source = new LocalSourceTools(_root);
        var outcome = await ExplorationLoop.RunAsync(source, async (_, _) => await source.ReadAsync("order.py", 1, 2),
            (_, _) => Task.FromResult(RuntimeOnly("database")), null, default);
        Assert.False(outcome.Complete);
        var request = ExplorationLoop.WithCompleteness(ExplorationLoop.CreateRequest("What happens in our database?", source), outcome, source);
        Assert.True(request.ExplorationContext!.RequiresRuntimeConfirmation);
        var answer = ExplorationLoop.Render(new("supported", [new("Unfinished deliveries may be cancelled if inventory integration is enabled.", ["source-1"])]), request, outcome, source);
        Assert.Contains("cannot confirm the outcome in your running database", answer);
        Assert.Contains("I can verify only", answer);
    }

    [Fact]
    public async Task SourceGapsCannotBeDisguisedAsRuntimeUncertainty()
    {
        var source = new LocalSourceTools(_root);
        await source.ReadAsync("order.py", 1, 2);
        var invalid = RuntimeOnly() with { Coverage = RuntimeOnly().Coverage.Select(c => c.Area == "applicability" ? c with { Status = "unknown" } : c).ToArray() };
        Assert.Throws<AnswerGenerationException>(() => ExplorationLoop.Validate(invalid, source));
        var gap = new InvestigationGap("Can cancelling a delivery fail, and why?", "Source implementation unavailable.", false);
        var review = invalid with { Gaps = [gap, .. invalid.Gaps] };
        ExplorationLoop.Validate(review, source);
        Assert.False(ExplorationLoop.IsComplete(review));
        var outcome = new InvestigationOutcome(review, "paths exhausted", false);
        var request = ExplorationLoop.WithCompleteness(ExplorationLoop.CreateRequest("Cancel?", source), outcome, source);
        var answer = ExplorationLoop.Render(new("supported", [new("Cancellation is conditional.", ["source-1"])]), request, outcome, source);
        Assert.Contains(gap.Question, answer);
        Assert.Contains("I can verify only", answer);
        Assert.False(request.ExplorationContext!.SourceComplete);
    }

    private sealed class ConversationStub : IConversationAnswerer
    {
        public IReadOnlyList<ChatMessage>? Messages { get; private set; }
        public Func<CancellationToken, Task<ConversationAnswer>> Answer { get; set; } = _ => Task.FromResult(new ConversationAnswer("Conditional business answer.", null));
        public Task<ConversationAnswer> AnswerAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            Messages = messages;
            return Answer(cancellationToken);
        }
    }
    private sealed class Retriever : ICodeRetriever
    {
        public Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken cancellationToken = default) => Task.FromResult(new RetrievalResult(
            new("rag-fixture", "source", "commit", "test", false, 1),
            [new(new("rag-1", "python", "order.py", null, null, "method", null, null, null, 1, 1, "", "pass"), 1, 1, 1)]));
    }
    private sealed class Generator : IAnswerGenerator
    {
        public AnswerRequest? Request { get; private set; }
        public Task<AnswerDraft> GenerateAsync(AnswerRequest request, CancellationToken cancellationToken = default)
        { Request = request; return Task.FromResult(new AnswerDraft("supported", [new("Orders can be cancelled.", ["rag-1"])])); }
    }
    public void Dispose() => Directory.Delete(_root, true);
}
