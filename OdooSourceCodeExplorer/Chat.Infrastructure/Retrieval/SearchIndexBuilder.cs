using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chat.App.Indexing;
using Chat.App.Retrieval;

namespace Chat.Infrastructure.Retrieval;

public sealed class SearchIndexBuilder(RetrievalOptions options, OllamaEmbeddingClient embeddings, QdrantVectorStore vectors)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<string> BuildAsync(string sourceIndexPath, Action<string>? progress = null, CancellationToken ct = default)
    {
        options.Validate();
        var input = Path.GetFullPath(sourceIndexPath);
        using var source = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(input, "manifest.json"), ct));
        var metadata = source.RootElement;
        if (metadata.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidOperationException("Unsupported source index schema.");
        var chunksPath = Path.Combine(input, "chunks.jsonl");
        var sourceHash = await FileHashAsync(chunksPath, ct);
        if (sourceHash != metadata.GetProperty("chunksSha256").GetString()) throw new InvalidOperationException("Source index checksum mismatch.");
        var chunks = new List<CodeChunk>();
        await foreach (var line in File.ReadLinesAsync(chunksPath, ct))
            chunks.Add(JsonSerializer.Deserialize<CodeChunk>(line, Json) ?? throw new InvalidOperationException("Invalid source chunk."));
        if (chunks.Count == 0 || chunks.Count != metadata.GetProperty("chunkCount").GetInt32() || chunks.DistinctBy(c => c.Id).Count() != chunks.Count)
            throw new InvalidOperationException("Source chunk count or IDs are invalid.");
        var output = Path.GetFullPath(options.OutputPath);
        ValidateOutput(output, input, metadata.GetProperty("repositoryPath").GetString()!);
        var digest = await embeddings.GetDigestAsync(ct);
        var id = "search-" + Guid.NewGuid().ToString("N");
        var collection = "businesschat_" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(output);
        var staging = Path.Combine(output, ".building-" + id);
        Directory.CreateDirectory(staging);
        var created = false;
        try
        {
            var db = Path.Combine(staging, "knowledge.sqlite");
            await new SqliteKnowledgeStore(db).BuildAsync(chunks, ct);
            var pending = new List<(CodeChunk Chunk, string Text, int Window)>();
            var points = 0;
            var dimension = 0;
            for (var index = 0; index < chunks.Count; index++)
            {
                var window = 0;
                foreach (var text in EmbeddingDocuments.ForChunk(chunks[index], options.WindowCharacters, options.WindowOverlap))
                {
                    pending.Add((chunks[index], text, window++));
                    if (pending.Count >= options.BatchSize) await FlushAsync();
                }
                if ((index + 1) % 50 == 0) progress?.Invoke($"Embedded {index + 1}/{chunks.Count} chunks ({points} vectors).");
            }
            await FlushAsync();
            await vectors.ValidateAsync(collection, dimension, points, ct);
            if (digest != await embeddings.GetDigestAsync(ct)) throw new InvalidOperationException("Embedding model changed during indexing.");
            if (sourceHash != await FileHashAsync(chunksPath, ct)) throw new InvalidOperationException("Source artifact changed during indexing.");
            var info = new KnowledgeIndexInfo(id, metadata.GetProperty("indexId").GetString()!, metadata.GetProperty("gitCommit").GetString()!,
                metadata.GetProperty("applicationVersion").GetString(), metadata.GetProperty("workingTreeDirty").GetBoolean(), chunks.Count);
            var manifest = new SearchIndexManifest(1, EmbeddingDocuments.Version, info, collection, options.EmbeddingModel, digest,
                dimension, points, options.WindowCharacters, options.WindowOverlap, sourceHash, await FileHashAsync(db, ct));
            await File.WriteAllTextAsync(Path.Combine(staging, "search-manifest.json"), JsonSerializer.Serialize(manifest, Json), ct);
            ct.ThrowIfCancellationRequested();
            var result = Path.Combine(output, id);
            Directory.Move(staging, result);
            return result;

            async Task FlushAsync()
            {
                if (pending.Count == 0) return;
                var batch = await embeddings.EmbedAsync(pending.Select(p => p.Text).ToArray(), ct);
                if (!created)
                {
                    dimension = batch[0].Length;
                    await vectors.CreateAsync(collection, dimension, ct);
                    created = true;
                }
                if (batch.Any(v => v.Length != dimension)) throw new InvalidOperationException("Embedding dimensions changed during indexing.");
                var items = pending.Select((p, i) => new VectorPoint(
                    new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{p.Chunk.Id}:{p.Window}"))[..16]).ToString(),
                    batch[i], p.Chunk.Id, p.Chunk.ModuleName)).ToArray();
                await vectors.UpsertAsync(collection, items, ct);
                points += items.Length;
                pending.Clear();
            }
        }
        catch
        {
            if (created)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await vectors.DeleteAsync(collection, cleanup.Token); }
                catch (Exception) { progress?.Invoke($"Cleanup failed; remove this run's unused Qdrant collection manually: {collection}"); }
            }
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
    }

    internal static async Task<string> FileHashAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
    }
    private static void ValidateOutput(string output, string input, string sourceRoot)
    {
        var roots = new List<string> { input, sourceRoot };
        for (var current = AppContext.BaseDirectory; current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(Path.Combine(current, ".git")) || File.Exists(Path.Combine(current, ".git"))) { roots.Add(current); break; }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (roots.Any(r => output.Equals(r, comparison) || output.StartsWith(Path.TrimEndingDirectorySeparator(r) + Path.DirectorySeparatorChar, comparison)))
            throw new ArgumentException("Search output must be outside both repositories and the source index.");
        for (var current = output; current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Search output must not use symbolic links.");
    }
}
