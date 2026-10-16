using System.Text.RegularExpressions;
using Chat.App;

namespace Chat.Infrastructure.Exploration;

// The reviewer records coverage, not additional source evidence. Only LocalSourceTools can register evidence.
public sealed record CoverageItem(string Area, string Status, string Finding, string[] EvidenceIds);
public sealed record InvestigationAction(string Kind, string Text, string Path, int StartLine, int LineCount);
public sealed record InvestigationGap(string Question, string NextStep, bool CanInvestigate, InvestigationAction? Action = null, string Kind = "source");
public sealed record ExplorationReview(CoverageItem[] Coverage, InvestigationGap[] Gaps, string QuestionScope = "source");
public sealed record InvestigationOutcome(ExplorationReview? Review, string StopReason, bool Complete);

public static class ExplorationLoop
{
    public static readonly string[] Areas = ["entry", "blockers", "extensions", "downstream", "alternatives", "applicability"];
    public const int MaxReviews = 6;

    public static async Task<InvestigationOutcome> RunAsync(LocalSourceTools source,
        Func<ExplorationReview?, CancellationToken, Task> investigate,
        Func<ExplorationReview?, CancellationToken, Task<ExplorationReview>> review,
        Action<string>? progress, CancellationToken ct)
    {
        var budget = source.Budget;
        ExplorationReview? ledger = null;
        var before = -1;
        for (var round = 0; round < MaxReviews; round++)
        {
            ct.ThrowIfCancellationRequested();
            if (!budget.TryUse(reserve: 2)) return Stop("shared operation limit", false);
            await investigate(ledger, ct);
            ct.ThrowIfCancellationRequested();
            if (!budget.TryUse(reserve: 1)) return Stop("shared operation limit", false);
            ledger = await review(ledger, ct);
            Validate(ledger, source);
            ct.ThrowIfCancellationRequested();
            var complete = IsComplete(ledger);
            if (complete) return Stop("relevant coverage established", true);
            foreach (var gap in ledger.Gaps)
                // Only reviewed business-language questions go to diagnostics; never source/finding/next-step text.
                progress?.Invoke($"Material gap: {SafeQuestion(gap.Question, source)}");
            if (budget.ReadLimitReached) return Stop("shared source character limit", false);
            if (round == MaxReviews - 1) return Stop("review round limit", false);
            if (source.UsefulOperations == before)
            {
                // A model can stop without following its review. Try concrete remaining paths ourselves,
                // through the same bounded tools, before accepting a stalled investigation.
                foreach (var gap in ledger.Gaps.Where(g => g.Kind == "source" && g.CanInvestigate && g.Action is not null))
                {
                    if (budget.Remaining <= 3) break;
                    var action = gap.Action!;
                    if (!source.CanAddInformation(action)) continue;
                    progress?.Invoke($"Targeting unresolved gap: {SafeQuestion(gap.Question, source)}");
                    if (action.Kind == "search") await source.SearchAsync(action.Text, action.Path);
                    else await source.ReadAsync(action.Path, action.StartLine, action.LineCount);
                    if (source.UsefulOperations > before) break;
                }
                if (budget.ReadLimitReached) return Stop("shared source character limit", false);
                if (budget.Remaining <= 3) return Stop("shared operation limit", false);
                if (source.UsefulOperations == before) return Stop("continuation and targeted paths added no new information", false);
            }
            if (budget.Remaining <= 3) return Stop("shared operation limit", false);
            if (!ledger.Gaps.Any(g => g.Kind == "source" && g.CanInvestigate)) return Stop("useful source investigation paths exhausted or runtime confirmation required", false);
            before = source.UsefulOperations;
        }
        return Stop("review round limit", false);

        InvestigationOutcome Stop(string reason, bool complete)
        {
            progress?.Invoke($"Investigation stopped: {reason}.");
            return new(ledger, reason, complete);
        }
    }

    public static void Validate(ExplorationReview review, LocalSourceTools source)
    {
        var ids = source.Evidence.Select(e => e.Evidence.Id).ToHashSet(StringComparer.Ordinal);
        if (review.Coverage is null || review.Gaps is null || review.Coverage.Length != Areas.Length ||
            !Areas.All(a => review.Coverage.Count(c => c is not null && c.Area == a) == 1)) Fail("coverage areas missing or repeated");
        if (review.QuestionScope is not ("source" or "database")) Fail("invalid question scope");
        if (review.Gaps!.Length > 12) Fail("too many gaps");
        foreach (var item in review.Coverage!)
        {
            if (item.Status is not ("resolved" or "unknown")) Fail("invalid coverage status");
            if (string.IsNullOrWhiteSpace(item.Finding) || item.Finding.Length > 2000) Fail("invalid finding length");
            if (item.EvidenceIds is null || item.EvidenceIds.Any(id => !ids.Contains(id))) Fail("unknown evidence reference");
            if (item.Status == "resolved" && item.EvidenceIds!.Length == 0) Fail("resolved coverage lacks read evidence");
        }
        if (review.Gaps.Any(g => g is null || g.Kind is not ("source" or "runtime") ||
            (g.Kind == "runtime" && (g.CanInvestigate || g.Action is not null)) || string.IsNullOrWhiteSpace(g.Question) || g.Question.Length > 600 ||
            string.IsNullOrWhiteSpace(g.NextStep) || g.NextStep.Length > 2000 ||
            (g.CanInvestigate && (g.Action is null || g.Action.Kind is not ("search" or "read") ||
                g.Action.Path is null || g.Action.Path.Length > 500 ||
                (g.Action.Kind == "search" && (string.IsNullOrWhiteSpace(g.Action.Text) || g.Action.Text.Length > 200)) ||
                (g.Action.Kind == "read" && (string.IsNullOrWhiteSpace(g.Action.Path) || g.Action.StartLine < 1 || g.Action.LineCount is < 1 or > 200)))))) Fail("invalid gap details");
        if (review.Coverage.Any(c => c.Status == "unknown") && !review.Gaps.Any(g => g.Kind == "source")) Fail("unknown source coverage lacks source gaps");

        static void Fail(string reason) => throw new AnswerGenerationException($"The exploration coverage review could not be verified ({reason}). No answer was published.");
    }

    public static string SafeQuestion(string question, LocalSourceTools source)
    {
        if (source.Evidence.Count == 0) return "The requested workflow could not be established from readable source.";
        if (question.Any(char.IsControl) || Regex.IsMatch(question, @"(?i)\b[\w./\\-]+\.(?:xml|csv)\b"))
            return "Some conditions or effects of this process could not be established.";
        var request = CreateRequest("", source);
        // Reuse the exact business-language boundary, without treating the review as evidence for application facts.
        return BusinessAnswerPolicy.Render(new("supported", [new(question, [request.Evidence[0].Evidence.Id])]), request);
    }

    public static bool SourceComplete(ExplorationReview review) => review.Coverage.All(c => c.Status == "resolved") &&
        !review.Gaps.Any(g => g.Kind == "source");

    public static bool IsComplete(ExplorationReview review) => SourceComplete(review) &&
        !(review.QuestionScope == "database" && review.Gaps.Any(g => g.Kind == "runtime"));

    public static AnswerRequest CreateRequest(string question, LocalSourceTools source) => CreateRequest([new(ChatRole.User, question)], source);

    public static AnswerRequest CreateRequest(IReadOnlyList<ChatMessage> messages, LocalSourceTools source) => new(
        messages, new("local-exploration", "none", "unverified-live-checkout", null, true, source.Evidence.Count), source.Evidence, false);

    public static AnswerRequest WithCompleteness(AnswerRequest request, InvestigationOutcome outcome, LocalSourceTools source) => request with
    {
        ExplorationContext = new(outcome.Review is not null && SourceComplete(outcome.Review),
            outcome.Review?.QuestionScope == "database",
            outcome.Review?.Gaps.Where(g => g.Kind == "source").Select(g => SafeQuestion(g.Question, source)).ToArray() ?? [],
            outcome.Review?.Gaps.Where(g => g.Kind == "runtime").Select(g => SafeQuestion(g.Question, source)).ToArray() ?? [])
    };

    public const string ConfigurationCaveat = "Enabled modules and settings were not checked.";

    public static string Render(AnswerDraft draft, AnswerRequest request, InvestigationOutcome outcome, LocalSourceTools source)
    {
        if (!outcome.Complete && draft.Status == "supported") draft = draft with { Status = "partial" };
        var answer = BusinessAnswerPolicy.Render(draft, request);
        var gaps = outcome.Review?.Gaps.Where(g => g.Kind == "source")
            .Select(g => SafeQuestion(g.Question, source)).Distinct().ToArray() ?? [];
        if (gaps.Length > 0)
            answer += "\n\nStill unresolved:\n" + string.Join("\n", gaps.Select(g => "- " + g));
        if (outcome.Review?.QuestionScope == "database" && outcome.Review.Gaps.Any(g => g.Kind == "runtime"))
            answer += "\n\nI cannot confirm the outcome in your running database without knowing its enabled modules and settings.";
        if (!answer.Contains(ConfigurationCaveat, StringComparison.Ordinal)) answer += "\n\n" + ConfigurationCaveat;
        return answer;
    }
}
