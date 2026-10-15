using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chat.App.Indexing;

namespace Chat.Indexer;

public sealed class RepositoryIndexer(IEnumerable<ISourceChunker> chunkers)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<string> BuildAsync(SourceRepositoryOptions repository, IndexOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository.Path)) throw new ArgumentException("SourceRepository:Path is required.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository.Path));
        if (!Directory.Exists(root)) throw new ArgumentException("Source repository directory does not exist.");
        EnsureNoLinks(root);
        var gitRoot = (await GitRepository.RunAsync(root, cancellationToken, "rev-parse", "--show-toplevel")).Trim();
        if (!SamePath(root, gitRoot)) throw new ArgumentException("SourceRepository:Path must be the Git checkout root.");
        if (options.MaxFileBytes <= 0 || options.MaxChunkCharacters <= 0 || options.IncludePaths.Length == 0)
            throw new ArgumentException("IncludePaths must be nonempty and size limits must be positive.");
        if (string.IsNullOrWhiteSpace(options.OutputPath)) throw new ArgumentException("Indexing:OutputPath is required.");
        var output = Path.GetFullPath(options.OutputPath);
        EnsureNoLinks(output);
        var chatbotRoot = FindGitRoot(AppContext.BaseDirectory);
        if (chatbotRoot is not null && (Within(root, chatbotRoot) || Within(chatbotRoot, root)))
            throw new ArgumentException("The source checkout and chatbot repository must be separate.");
        if (Within(output, root) || chatbotRoot is not null && Within(output, chatbotRoot))
            throw new ArgumentException("Index output must be outside both source and chatbot repositories.");
        var includes = options.IncludePaths.Select(p =>
        {
            if (string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p)) throw new ArgumentException("IncludePaths must be relative paths inside the source checkout.");
            var full = Path.GetFullPath(Path.Combine(root, p));
            if (!Within(full, root)) throw new ArgumentException("IncludePaths cannot escape the source checkout.");
            EnsureNoLinks(full);
            if (!File.Exists(full) && !Directory.Exists(full)) throw new ArgumentException($"Include path does not exist: {p}");
            return Path.GetRelativePath(root, full).Replace('\\', '/');
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        var adapters = chunkers.ToArray();
        var commit = (await GitRepository.RunAsync(root, cancellationToken, "rev-parse", "HEAD")).Trim();
        var status = await GitRepository.RunAsync(root, cancellationToken, "status", "--porcelain=v1", "--untracked-files=normal");
        var tracked = await GitRepository.RunAsync(root, cancellationToken, "ls-files", "--cached", "-z");
        var paths = tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)
            .Where(p => includes.Any(i => i == "." || p == i || p.StartsWith(i + "/", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal).ToArray();
        // A typo or an unsupported-only selection must not produce a seemingly useful empty index.
        if (!paths.Any(p => adapters.Any(a => a.CanHandle(p))))
            throw new InvalidOperationException("Selection contains no tracked files supported by a chunker (currently .py).");

        Directory.CreateDirectory(output);
        var id = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{commit[..12]}-{Guid.NewGuid():N}";
        var staging = Path.Combine(output, ".building-" + id);
        var destination = Path.Combine(output, id);
        Directory.CreateDirectory(staging);
        var files = new List<IndexedFile>();
        var skipped = new List<SkippedFile>();
        var moduleCache = new Dictionary<string, (string? Name, string? Path)>(StringComparer.Ordinal);
        var chunkCount = 0;
        var oversized = 0;
        try
        {
            var chunkPath = Path.Combine(staging, "chunks.jsonl");
            await using (var writer = new StreamWriter(chunkPath, false, new UTF8Encoding(false)))
            {
                foreach (var relative in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var adapter = adapters.FirstOrDefault(a => a.CanHandle(relative));
                    if (adapter is null) { skipped.Add(new(relative, "Unsupported file type")); continue; }
                    var path = Path.GetFullPath(Path.Combine(root, relative));
                    if (!Within(path, root)) throw new InvalidOperationException("Git returned an unsafe path.");
                    try { EnsureNoLinks(path); }
                    catch (ArgumentException) { skipped.Add(new(relative, "Symbolic link or linked ancestor")); continue; }
                    if (!File.Exists(path)) { skipped.Add(new(relative, "Missing working-tree file")); continue; }
                    if (new FileInfo(path).Length > options.MaxFileBytes) { skipped.Add(new(relative, "Exceeds MaxFileBytes")); continue; }
                    var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                    if (bytes.LongLength > options.MaxFileBytes) throw new IOException($"File grew beyond size limit: {relative}");
                    string content;
                    try { content = StrictUtf8.GetString(bytes).TrimStart('\uFEFF'); }
                    catch (DecoderFallbackException) { skipped.Add(new(relative, "Not valid UTF-8")); continue; }
                    if (content.Contains('\0')) { skipped.Add(new(relative, "Contains NUL characters")); continue; }
                    var module = ModuleFor(path);
                    var chunks = adapter.Chunk(new SourceDocument(relative, content, module.Name, module.Path));
                    foreach (var chunk in chunks)
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(chunk, Json).AsMemory(), cancellationToken);
                        chunkCount++;
                        if (chunk.Content.Length > options.MaxChunkCharacters) oversized++;
                    }
                    files.Add(new(relative, Hash(bytes), chunks.Count));
                }
            }
            if (chunkCount == 0) throw new InvalidOperationException("No source chunks were generated. Check skipped files and selection.");
            // Fail rather than stamp a changing checkout with misleading commit metadata.
            foreach (var file in files)
            {
                var path = Path.Combine(root, file.RelativePath);
                EnsureNoLinks(path);
                if (Hash(await File.ReadAllBytesAsync(path, cancellationToken)) != file.Sha256)
                    throw new IOException($"Source changed during indexing: {file.RelativePath}. Run indexing again.");
            }
            if (commit != (await GitRepository.RunAsync(root, cancellationToken, "rev-parse", "HEAD")).Trim() ||
                status != await GitRepository.RunAsync(root, cancellationToken, "status", "--porcelain=v1", "--untracked-files=normal"))
                throw new IOException("Git checkout changed during indexing. Run indexing again.");

            var manifest = new IndexManifest(1, "python-lexical-v1", id, DateTimeOffset.UtcNow,
                root, commit, status.Length > 0, repository.ApplicationVersion,
                includes, options.MaxFileBytes, options.MaxChunkCharacters, chunkCount, oversized,
                await HashFileAsync(chunkPath, cancellationToken), files, skipped,
                ["Structural lexical analysis only; no resolved calls, dependencies or verified business workflows.",
                 "Only tracked UTF-8 Python files are indexed; XML/CSV/JS, untracked files and symlinks are excluded.",
                 "Large methods are retained whole; MaxChunkCharacters is a reporting threshold, not a truncation limit.",
                 "Working-tree content is indexed. A dirty index is not an exact snapshot of its Git commit."]);
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(Json) { WriteIndented = true }), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return destination;
        }
        catch
        {
            // Only this run's private, unpublished directory is removed.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }

        (string? Name, string? Path) ModuleFor(string path)
        {
            var directory = Path.GetDirectoryName(path)!;
            if (moduleCache.TryGetValue(directory, out var cached)) return cached;
            for (var current = directory; Within(current, root); current = Path.GetDirectoryName(current)!)
            {
                var manifest = Path.Combine(current, "__manifest__.py");
                if (File.Exists(manifest))
                {
                    EnsureNoLinks(manifest);
                    return moduleCache[directory] = (Path.GetFileName(current), Path.GetRelativePath(root, current).Replace('\\', '/'));
                }
                if (SamePath(current, root)) break;
            }
            return moduleCache[directory] = (null, null);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static bool SamePath(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), PathComparison);
    private static bool Within(string path, string root) => SamePath(path, root) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);
    private static void EnsureNoLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException($"Symbolic links are not supported: {current}");
    }
    private static string? FindGitRoot(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(Path.Combine(current, ".git")) || File.Exists(Path.Combine(current, ".git"))) return current;
        return null;
    }
}
