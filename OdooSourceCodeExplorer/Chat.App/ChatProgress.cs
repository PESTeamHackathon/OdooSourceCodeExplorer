namespace Chat.App;

public enum ChatProgressStep { Searching, Investigating, Reviewing, Writing, Checking }

// Business-safe progress for the waiting UI: counts and reviewed plain-language questions only,
// never source paths, identifiers or raw model output.
public sealed record ChatProgress(ChatProgressStep Step, int Searches = 0, int SectionsRead = 0, int ReviewRounds = 0,
    IReadOnlyList<string>? OpenQuestions = null, int SectionsSkipped = 0, TokenUsage? Usage = null, int CodeLines = 0);
