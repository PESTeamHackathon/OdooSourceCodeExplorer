using Chat.App;
using Chat.Infrastructure.Exploration;

namespace Chat.App.Tests;

public sealed class ExplorationLoopTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "exploration-loop-" + Guid.NewGuid().ToString("N"));
    private const string Gap = "What happens to unfinished deliveries, and can completed deliveries be affected?";

    public ExplorationLoopTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "order.py"), "def cancel():\n    delivery.cancel()\n");
        File.WriteAllText(Path.Combine(_root, "delivery.py"), "def cancel():\n    if state != 'done':\n        state = 'cancelled'\n");
    }

    private static ExplorationReview Review(LocalSourceTools source, bool complete, bool canInvestigate = true) => new(
        ExplorationLoop.Areas.Select(a => new CoverageItem(a, complete ? "resolved" : "unknown", "Coverage assessed from source.",
            source.Evidence.Select(e => e.Evidence.Id).ToArray())).ToArray(),
        complete ? [] : [new(Gap, "Read the operation to verify its conditions.", canInvestigate, canInvestigate ? new("read", "", "order.py", 1, 2) : null)]);

    [Fact]
    public async Task MissingDownstreamEvidenceTriggersContinuationAndAllReadsReachClaimValidation()
    {
        var budget = new ExplorationBudget();
        var source = new LocalSourceTools(_root, budget: budget);
        var turns = 0;
        var reviews = 0;
        var diagnostics = new List<string>();
        var outcome = await ExplorationLoop.RunAsync(source, async (ledger, ct) =>
        {
            turns++;
            if (ledger is null) await source.ReadAsync("order.py", 1, 2);
            else
            {
                Assert.Equal(Gap, Assert.Single(ledger.Gaps).Question);
                await source.SearchAsync("def cancel", "delivery.py");
                await source.ReadAsync("delivery.py", 1, 3);
            }
        }, (previous, ct) =>
        {
            reviews++;
            Assert.Equal(reviews, source.Evidence.Count);
            return Task.FromResult(Review(source, reviews == 2));
        }, diagnostics.Add, default);
        Assert.True(outcome.Complete);
        Assert.Equal(2, turns);
        Assert.Contains(diagnostics, d => d.Contains(Gap));
        var request = ExplorationLoop.CreateRequest("What happens?", source);
        Assert.Equal(2, request.Evidence.Count);
        var draft = new AnswerDraft("supported", [new("Unfinished deliveries are cancelled.", ["source-2"])]);
        Assert.Contains("Unfinished deliveries are cancelled.", ExplorationLoop.Render(draft, request, outcome, source));
        Assert.Equal(BusinessAnswerPolicy.Insufficient,
            BusinessAnswerPolicy.Render(draft with { Claims = [new("Invented effect.", ["source-99"])] }, request));
        Assert.Equal(7, budget.Operations); // Two investigation turns, two reviews and three source operations.
    }

    [Fact]
    public async Task SufficientCoverageStopsImmediatelyWithoutMinimumFilesOrCalls()
    {
        var source = new LocalSourceTools(_root);
        var turns = 0;
        var outcome = await ExplorationLoop.RunAsync(source, async (_, _) =>
        {
            turns++;
            await source.ReadAsync("delivery.py", 1, 3);
        }, (_, _) => Task.FromResult(Review(source, true)), null, default);
        Assert.True(outcome.Complete);
        Assert.Equal(1, turns);
        Assert.Equal("relevant coverage established", outcome.StopReason);
    }

    [Fact]
    public async Task RepetitionDoesNotRegisterEvidenceOrSustainContinuation()
    {
        var source = new LocalSourceTools(_root);
        var turns = 0;
        var outcome = await ExplorationLoop.RunAsync(source, async (_, _) =>
        {
            turns++;
            await source.SearchAsync("cancel");
            await source.ReadAsync("order.py", 1, 2);
        }, (_, _) => Task.FromResult(Review(source, false)), null, default);
        Assert.False(outcome.Complete);
        Assert.Equal(2, turns);
        Assert.Single(source.Evidence);
        Assert.Equal(2, source.UsefulOperations);
        Assert.Contains("no new", outcome.StopReason);
    }

    [Fact]
    public async Task ExhaustedPathsRetainSpecificBusinessGapsAndCannotBecomeSupported()
    {
        var source = new LocalSourceTools(_root);
        var outcome = await ExplorationLoop.RunAsync(source, async (_, _) => await source.ReadAsync("order.py", 1, 2),
            (_, _) => Task.FromResult(Review(source, false, false)), null, default);
        var answer = ExplorationLoop.Render(new("supported", [new("The order can be cancelled.", ["source-1"])]),
            ExplorationLoop.CreateRequest("Cancel?", source), outcome, source);
        Assert.Contains("useful source investigation paths exhausted", outcome.StopReason);
        Assert.Contains(Gap, answer);
        Assert.Contains("I can verify only", answer);
        Assert.Contains(ExplorationLoop.ConfigurationCaveat, answer);
    }

    [Fact]
    public async Task ContinuationsAndReviewsUseOneBudgetAndReserveSynthesis()
    {
        var budget = new ExplorationBudget(maxOperations: 9);
        var source = new LocalSourceTools(_root, budget: budget);
        var turns = 0;
        var outcome = await ExplorationLoop.RunAsync(source, async (_, _) =>
        {
            await source.SearchAsync("new-term-" + turns++);
            await source.ReadAsync("order.py", 1, 2);
        }, (_, _) => Task.FromResult(Review(source, false)), null, default);
        Assert.False(outcome.Complete);
        Assert.Equal("shared operation limit", outcome.StopReason);
        Assert.InRange(budget.Operations, 1, 8);
        Assert.True(budget.TryUse()); // Final tools-disabled synthesis still has a slot.
        Assert.InRange(budget.Operations, 1, 9);
        Assert.Single(source.Evidence);
    }

    [Fact]
    public async Task SourceCharacterLimitSurvivesReviewRounds()
    {
        var budget = new ExplorationBudget(maxReadCharacters: 45);
        var source = new LocalSourceTools(_root, budget: budget);
        var outcome = await ExplorationLoop.RunAsync(source, async (previous, _) =>
        {
            await source.ReadAsync(previous is null ? "order.py" : "delivery.py", 1, 2);
        }, (_, _) => Task.FromResult(Review(source, false)), null, default);
        Assert.False(outcome.Complete);
        Assert.True(budget.ReadLimitReached);
        Assert.Single(source.Evidence);
        Assert.Equal("shared source character limit", outcome.StopReason);
    }

    [Fact]
    public async Task ReviewsCannotSustainUnboundedNewSearches()
    {
        var source = new LocalSourceTools(_root);
        var turns = 0;
        var outcome = await ExplorationLoop.RunAsync(source, async (_, _) =>
        {
            await source.ReadAsync("order.py", 1, 2);
            await source.SearchAsync("different-" + turns++);
        }, (_, _) => Task.FromResult(Review(source, false)), null, default);
        Assert.Equal(ExplorationLoop.MaxReviews, turns);
        Assert.Equal("review round limit", outcome.StopReason);
    }

    [Fact]
    public async Task CancellationDuringReviewPreventsContinuationAndPublication()
    {
        using var stop = new CancellationTokenSource();
        var source = new LocalSourceTools(_root, stop.Token);
        var turns = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExplorationLoop.RunAsync(source, async (_, _) =>
        {
            turns++;
            await source.ReadAsync("order.py", 1, 2);
        }, (_, _) =>
        {
            stop.Cancel();
            return Task.FromResult(Review(source, false));
        }, null, stop.Token));
        Assert.Equal(1, turns);
    }

    [Fact]
    public async Task CoverageReviewRejectsInventedEvidenceAndUnreportedUnknowns()
    {
        var source = new LocalSourceTools(_root);
        await source.ReadAsync("order.py", 1, 2);
        var valid = Review(source, true);
        var invalid = valid with { Coverage = valid.Coverage.Select(c => c with { EvidenceIds = ["source-99"] }).ToArray() };
        Assert.Throws<AnswerGenerationException>(() => ExplorationLoop.Validate(invalid, source));
        Assert.Throws<AnswerGenerationException>(() => ExplorationLoop.Validate(Review(source, false) with { Gaps = [] }, source));
        Assert.Throws<AnswerGenerationException>(() => ExplorationLoop.Validate(valid with { Coverage = [] }, source));
    }

    [Fact]
    public async Task CancellationDuringContinuationDoesNotRequestAnotherReview()
    {
        using var stop = new CancellationTokenSource();
        var source = new LocalSourceTools(_root, stop.Token);
        var turns = 0;
        var reviews = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExplorationLoop.RunAsync(source, async (previous, ct) =>
        {
            Assert.Equal(stop.Token, ct);
            if (++turns == 2) stop.Cancel();
            await source.ReadAsync(previous is null ? "order.py" : "delivery.py", 1, 2);
        }, (_, ct) =>
        {
            reviews++;
            return Task.FromResult(Review(source, false));
        }, null, stop.Token));
        Assert.Equal(2, turns);
        Assert.Equal(1, reviews);
        Assert.Single(source.Evidence);
    }

    [Theory]
    [InlineData("Does addons/other/rules.xml impose restrictions?")]
    [InlineData("What happens?\nInjected diagnostic")]
    [InlineData("Does `action_cancel` run?")]
    public async Task GapPresentationDoesNotExposeImplementationOrControlCharacters(string question)
    {
        var source = new LocalSourceTools(_root);
        await source.ReadAsync("order.py", 1, 2);
        var safe = ExplorationLoop.SafeQuestion(question, source);
        Assert.DoesNotContain("rules.xml", safe);
        Assert.DoesNotContain('\n', safe);
        Assert.DoesNotContain("action_cancel", safe);
    }

    [Fact]
    public async Task StalledInvestigatorExecutesConcreteGapPlanAndReviewsNewEvidence()
    {
        var source = new LocalSourceTools(_root);
        var reviews = 0;
        var turns = 0;
        var outcome = await ExplorationLoop.RunAsync(source, async (_, _) =>
        {
            if (++turns == 1) await source.ReadAsync("order.py", 1, 2);
            // Later model turns stop without calling tools; the controller must follow the concrete path.
        }, (_, _) =>
        {
            reviews++;
            var complete = source.Evidence.Count == 2;
            return Task.FromResult(Review(source, complete) with { Gaps = complete ? [] :
                [new(Gap, "Read the downstream implementation.", true, new("read", "", "delivery.py", 1, 3))] });
        }, null, default);
        Assert.True(outcome.Complete);
        Assert.Equal(3, reviews);
        Assert.Equal(2, source.Evidence.Count);
        Assert.Equal("delivery.py", source.Evidence[1].Evidence.RelativePath);
    }

    [Fact]
    public async Task TargetedSearchResultsRemainDiscoveryHintsUntilRead()
    {
        var source = new LocalSourceTools(_root);
        var turns = 0;
        var outcome = await ExplorationLoop.RunAsync(source, async (_, _) =>
        {
            if (++turns == 1) await source.ReadAsync("order.py", 1, 2);
            if (turns == 3)
            {
                Assert.Contains(source.Discoveries.SelectMany(d => d.Hits), h => h.Path == "delivery.py");
                Assert.Single(source.Evidence);
                await source.ReadAsync("delivery.py", 1, 3);
            }
        }, (_, _) => Task.FromResult(Review(source, source.Evidence.Count == 2) with
        {
            Gaps = source.Evidence.Count == 2 ? [] :
                [new(Gap, "Search for downstream operations.", true, new("search", "def cancel", "delivery.py", 1, 120))]
        }), null, default);
        Assert.True(outcome.Complete);
        Assert.Equal(2, source.Evidence.Count);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
