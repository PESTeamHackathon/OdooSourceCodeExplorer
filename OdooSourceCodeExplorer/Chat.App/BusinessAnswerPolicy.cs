using System.Text.RegularExpressions;

namespace Chat.App;

public static class BusinessAnswerPolicy
{
    public const string Insufficient = "I don't have enough verified information about this application to answer that reliably. Please make the question more specific; I won't assume missing business rules.";
    public const string Conflicting = "The available information is inconsistent, so I can't reliably confirm this behavior. Please ask the application owner to clarify it.";

    public static string Render(AnswerDraft draft, AnswerRequest request)
    {
        if (draft.Status == "insufficient") return Insufficient;
        if (draft.Status == "conflicting") return Conflicting;
        if (draft.Status is not ("supported" or "partial") || draft.Claims is null || draft.Claims.Count is < 1 or > 12)
            throw new AnswerGenerationException("The answer could not be verified. Please try a more specific question.");
        var ids = request.Evidence.Select(h => h.Evidence.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var claim in draft.Claims)
        {
            if (string.IsNullOrWhiteSpace(claim.Text)) throw new InvalidOperationException("The answer provider returned an empty response. Please try again.");
            if (claim.Text.Length > 3000 || claim.EvidenceIds is null || claim.EvidenceIds.Count == 0 || claim.EvidenceIds.Any(id => !ids.Contains(id)) ||
                ContainsImplementationDetails(claim.Text, request))
                return Insufficient;
        }
        var answer = draft.Claims.Count == 1 ? draft.Claims[0].Text : string.Join("\n\n", draft.Claims.Select((c, i) => $"{i + 1}. {c.Text}"));
        if (draft.Status == "partial")
            answer += "\n\nI can verify only the behavior described above; the available information is not enough to confirm the rest of the process.";
        return answer;
    }

    private static bool ContainsImplementationDetails(string text, AnswerRequest request)
    {
        if (text.Contains('`') || Regex.IsMatch(text, @"(?i)(?:\b[\w./\\-]+\.(?:py|cs)(?::\d+)?\b|\b\w+_\w+\b|\b\w+\s*\(\))")) return true;
        foreach (var hit in request.Evidence)
        {
            var c = hit.Evidence;
            foreach (var name in new[] { c.Id, c.RelativePath, c.QualifiedName, c.ClassName, c.Symbol })
                if (!string.IsNullOrEmpty(name) && Regex.IsMatch(text, @"(?<!\w)" + Regex.Escape(name) + @"(?!\w)", RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }
}
