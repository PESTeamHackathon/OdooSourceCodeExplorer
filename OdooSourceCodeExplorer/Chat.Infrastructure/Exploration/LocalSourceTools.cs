using System.Text;
using System.Text.Json;
using Chat.App;
using Chat.App.Indexing;
using Chat.App.Retrieval;

namespace Chat.Infrastructure.Exploration;

// Per investigation. These are the only filesystem operations exposed to the model.
// No target code, Git hooks, subprocesses, or repository instructions are executed.
public sealed class LocalSourceTools
{
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private readonly string _root;
    private readonly string[] _files;
    private readonly List<RetrievedChunk> _evidence = [];
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationToken _stop;
    private readonly Action<string>? _progress;
    private readonly ExplorationBudget _budget;
    private readonly HashSet<string> _searches = new(StringComparer.Ordinal);
    private readonly HashSet<string> _attempts = new(StringComparer.Ordinal);
    private readonly List<string> _activity = [];
    private readonly List<SourceSearchResult> _discoveries = [];
    public IReadOnlyList<SourceSearchResult> Discoveries => _discoveries.ToArray();
    public ExplorationBudget Budget => _budget;
    public int UsefulOperations { get; private set; }
    public IReadOnlyList<string> Activity => _activity.ToArray();

    public int SearchCalls { get; private set; }
    public int ReadCalls { get; private set; }
    public IReadOnlyList<RetrievedChunk> Evidence => _evidence.ToArray();

    public LocalSourceTools(string repositoryPath, CancellationToken stop = default, Action<string>? progress = null, ExplorationBudget? budget = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Path.IsPathFullyQualified(repositoryPath))
            throw new ArgumentException("SourceRepository:Path must be an absolute Odoo repository path.");
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        if (!Directory.Exists(_root) || _root == Path.GetPathRoot(_root))
            throw new ArgumentException("SourceRepository:Path must select an existing repository directory, not a filesystem root.");
        EnsureNoLinks(_root);
        _budget = budget ?? new();
        _stop = stop;
        _progress = progress;
        _files = Discover(_root).Order(StringComparer.Ordinal).ToArray();
        if (_files.Length == 0) throw new ArgumentException("No readable Python, XML or CSV source files were found.");
    }

    public async Task<SourceSearchResult> SearchAsync(string text, string pathPrefix = "")
    {
        await _gate.WaitAsync(_stop);
        try
        {
            if (!EnterCall("search_source")) return new([], false, "Shared investigation budget exhausted. Stop tool calls.");
            SearchCalls++;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 200)
                return new([], false, "Search text must contain 1–200 characters. Use a literal identifier or phrase, not a regex.");
            if (pathPrefix.Length != 0) Resolve(pathPrefix);
            var key = JsonSerializer.Serialize(new[] { text.ToUpperInvariant(), pathPrefix });
            if (!_searches.Add(key)) return new([], false, "Duplicate search suppressed. Use previous results or change the term/scope.");
            UsefulOperations++;
            Record($"Search term={JsonSerializer.Serialize(text)} scope={JsonSerializer.Serialize(pathPrefix.Length == 0 ? "<all>" : pathPrefix)}");
            var hits = new List<SourceSearchHit>();
            foreach (var relative in _files.Where(p => pathPrefix.Length == 0 ||
                         p.Equals(pathPrefix, StringComparison.Ordinal) || p.StartsWith(pathPrefix.TrimEnd('/') + "/", StringComparison.Ordinal)))
            {
                _stop.ThrowIfCancellationRequested();
                var lines = await TryReadLinesAsync(relative);
                if (lines is null) continue;
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                    if (hits.Count == 30)
                    {
                        Record("Search result: 30 matches; more matches exist. Narrow the scope or term.");
                        var truncated = new SourceSearchResult(hits, true, "More matches exist. Narrow pathPrefix or search text.");
                        _discoveries.Add(truncated);
                        return truncated;
                    }
                    var snippet = lines[i].Trim();
                    hits.Add(new(relative, i + 1, snippet[..Math.Min(400, snippet.Length)]));
                }
            }
            Record($"Search result: {hits.Count} matches; complete within scope.");
            var result = new SourceSearchResult(hits, false, "Search snippets are discovery only. Use read_source to obtain citable evidence.");
            _discoveries.Add(result);
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new([], false, "Search rejected or source unavailable. Use a repository-relative path without links or traversal.");
        }
        finally { _gate.Release(); }
    }

    public async Task<SourceReadResult> ReadAsync(string path, int startLine = 1, int lineCount = 120)
    {
        await _gate.WaitAsync(_stop);
        try
        {
            if (!EnterCall("read_source")) return new(null, "", 0, 0, "", null, "Shared investigation budget exhausted. Stop tool calls.");
            ReadCalls++;
            Resolve(path);
            if (!_files.Contains(path, StringComparer.Ordinal))
                return new(null, path, 0, 0, "", null, "Only catalogued Python, XML and CSV source files can be read.");
            if (startLine < 1 || lineCount is < 1 or > 200)
                return new(null, path, 0, 0, "", null, "startLine must be positive and lineCount must be 1–200.");
            if (!_attempts.Add($"{path}:{startLine}:{lineCount}") || IsCovered(path, startLine, (long)startLine + lineCount - 1))
                return new(null, path, 0, 0, "", null, "Duplicate/covered read suppressed. Previous evidence remains available.");
            var lines = await TryReadLinesAsync(path);
            if (lines is null || startLine > lines.Length)
                return new(null, path, 0, 0, "", null, "File unavailable, oversized, non-text, or requested line outside the file.");
            var selected = new List<string>();
            var size = 0;
            for (var i = startLine - 1; i < Math.Min(lines.Length, startLine - 1 + lineCount); i++)
            {
                if (size + lines[i].Length + 1 > 24_000) break;
                selected.Add(lines[i]);
                size += lines[i].Length + 1;
            }
            if (selected.Count > 0 && IsCovered(path, startLine, startLine + selected.Count - 1L))
                return new(null, path, 0, 0, "", null, "Selected lines already registered as evidence. Read a new range.");
            if (selected.Count == 0 || !_budget.TryRead(size))
                return new(null, path, 0, 0, "", null, "Read budget reached or source line too large. Answer partially using verified evidence.");
            var end = startLine + selected.Count - 1;
            var content = string.Join('\n', selected);
            var id = $"source-{_evidence.Count + 1}";
            UsefulOperations++;
            Record($"Read {JsonSerializer.Serialize(path)}:{startLine}-{end} evidence={id}");
            var language = Path.GetExtension(path).TrimStart('.');
            _evidence.Add(new(new CodeChunk(id, language, path, null, null, "source excerpt", null, null, null,
                startLine, end, "", content), 0, null, null));
            return new(id, path, startLine, end, content, end < lines.Length ? end + 1 : null,
                "Only these lines were read. Read surrounding definitions and downstream operations when needed.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new(null, "", 0, 0, "", null, "Read rejected or source unavailable. Use a repository-relative path without links or traversal.");
        }
        finally { _gate.Release(); }
    }

    public bool CanAddInformation(InvestigationAction action) => action.Kind switch
    {
        "search" => !_searches.Contains(JsonSerializer.Serialize(new[] { action.Text.ToUpperInvariant(), action.Path })),
        "read" => !_attempts.Contains($"{action.Path}:{action.StartLine}:{action.LineCount}") &&
            !IsCovered(action.Path, action.StartLine, (long)action.StartLine + action.LineCount - 1),
        _ => false
    };

    private bool IsCovered(string path, int start, long end)
    {
        long next = start;
        foreach (var chunk in _evidence.Select(e => e.Evidence).Where(e => e.RelativePath == path).OrderBy(e => e.StartLine))
        {
            if (chunk.StartLine > next) break;
            next = Math.Max(next, (long)chunk.EndLine + 1);
            if (next > end) return true;
        }
        return false;
    }

    private void Record(string activity)
    {
        _activity.Add(activity);
        _progress?.Invoke(activity);
    }

    private bool EnterCall(string name)
    {
        _stop.ThrowIfCancellationRequested();
        // Reserve one review and final synthesis, even if the model keeps calling tools.
        if (!_budget.TryUse(reserve: 2)) return false;
        _progress?.Invoke($"Investigation operation {_budget.Operations}: {name}");
        return true;
    }

    private string Resolve(string relative)
    {
        // Require canonical relative paths; never accept absolute paths, parent steps or backslashes.
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') ||
            relative.Split('/').Any(p => p is ".." or "." or ""))
            throw new ArgumentException("Use a repository-relative path.");
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Path outside repository.");
        EnsureNoLinks(full);
        return full;
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Symbolic links are not allowed.");
    }

    private IEnumerable<string> Discover(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            _stop.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || Path.GetFileName(entry).StartsWith('.')) continue;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (Path.GetFileName(entry) is "node_modules" or "__pycache__" or "venv") continue;
                foreach (var file in Discover(entry)) yield return file;
            }
            else if (Path.GetExtension(entry) is ".py" or ".xml" or ".csv")
                yield return Path.GetRelativePath(_root, entry).Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    private async Task<string[]?> TryReadLinesAsync(string relative)
    {
        var full = Resolve(relative); // Recheck links on each access, including after discovery.
        if (new FileInfo(full).Length > MaxFileBytes) return null;
        var bytes = await File.ReadAllBytesAsync(full, _stop);
        if (bytes.Length > MaxFileBytes || bytes.Contains((byte)0)) return null;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }
}

public sealed record SourceSearchHit(string Path, int Line, string Snippet);
public sealed record SourceSearchResult(IReadOnlyList<SourceSearchHit> Hits, bool MoreMatches, string Note);
public sealed record SourceReadResult(string? EvidenceId, string Path, int StartLine, int EndLine,
    string Content, int? NextLine, string Note);
