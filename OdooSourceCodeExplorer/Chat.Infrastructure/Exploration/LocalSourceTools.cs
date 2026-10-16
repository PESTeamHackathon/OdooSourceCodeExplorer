using System.Text;
using Chat.App;
using Chat.App.Indexing;
using Chat.App.Retrieval;

namespace Chat.Infrastructure.Exploration;

// Per investigation. These are the only filesystem operations exposed to the model.
// No target code, Git hooks, subprocesses, or repository instructions are executed.
public sealed class LocalSourceTools
{
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxCalls = 40;
    private const int MaxReadCharacters = 180_000;
    private readonly string _root;
    private readonly string[] _files;
    private readonly List<RetrievedChunk> _evidence = [];
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationToken _stop;
    private readonly Action<string>? _progress;
    private int _calls;
    private int _characters;

    public int SearchCalls { get; private set; }
    public int ReadCalls { get; private set; }
    public IReadOnlyList<RetrievedChunk> Evidence => _evidence.ToArray();

    public LocalSourceTools(string repositoryPath, CancellationToken stop = default, Action<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Path.IsPathFullyQualified(repositoryPath))
            throw new ArgumentException("SourceRepository:Path must be an absolute Odoo repository path.");
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        if (!Directory.Exists(_root) || _root == Path.GetPathRoot(_root))
            throw new ArgumentException("SourceRepository:Path must select an existing repository directory, not a filesystem root.");
        EnsureNoLinks(_root);
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
            EnterCall("search_source");
            SearchCalls++;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 200)
                return new([], false, "Search text must contain 1–200 characters. Use a literal identifier or phrase, not a regex.");
            if (pathPrefix.Length != 0) Resolve(pathPrefix);
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
                    if (hits.Count == 30) return new(hits, true, "More matches exist. Narrow pathPrefix or search text.");
                    var snippet = lines[i].Trim();
                    hits.Add(new(relative, i + 1, snippet[..Math.Min(400, snippet.Length)]));
                }
            }
            return new(hits, false, "Search snippets are discovery only. Use read_source to obtain citable evidence.");
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
            EnterCall("read_source");
            ReadCalls++;
            Resolve(path);
            if (!_files.Contains(path, StringComparer.Ordinal))
                return new(null, path, 0, 0, "", null, "Only catalogued Python, XML and CSV source files can be read.");
            if (startLine < 1 || lineCount is < 1 or > 200)
                return new(null, path, 0, 0, "", null, "startLine must be positive and lineCount must be 1–200.");
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
            if (selected.Count == 0 || _characters + size > MaxReadCharacters)
                return new(null, path, 0, 0, "", null, "Read budget reached or source line too large. Answer partially using verified evidence.");
            _characters += size;
            var end = startLine + selected.Count - 1;
            var content = string.Join('\n', selected);
            var id = $"source-{_evidence.Count + 1}";
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

    private void EnterCall(string name)
    {
        _stop.ThrowIfCancellationRequested();
        if (++_calls > MaxCalls) throw new InvalidOperationException("Investigation tool limit reached.");
        _progress?.Invoke($"Tool {_calls}/{MaxCalls}: {name}");
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
