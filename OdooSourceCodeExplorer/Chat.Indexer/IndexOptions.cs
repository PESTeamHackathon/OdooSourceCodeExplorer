namespace Chat.Indexer;

public sealed class SourceRepositoryOptions
{
    public string Path { get; set; } = "";
    public string? ApplicationVersion { get; set; }
}

public sealed class IndexOptions
{
    // ConfigurationBinder appends to initialized arrays; start empty to avoid
    // accidentally retaining "." when the caller selects a subset.
    public string[] IncludePaths { get; set; } = [];
    public long MaxFileBytes { get; set; } = 2_097_152;
    // Soft limit: report oversized semantic units; never silently truncate evidence.
    public int MaxChunkCharacters { get; set; } = 12_000;
    public string OutputPath { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusinessChat", "indexes");
}

public sealed record IndexedFile(string RelativePath, string Sha256, int ChunkCount);
public sealed record SkippedFile(string RelativePath, string Reason);
public sealed record IndexManifest(
    int SchemaVersion, string ChunkerVersion, string IndexId, DateTimeOffset CreatedUtc,
    string RepositoryPath, string GitCommit, bool WorkingTreeDirty, string? ApplicationVersion,
    string[] IncludePaths, long MaxFileBytes, int MaxChunkCharacters,
    int ChunkCount, int OversizedChunkCount, string ChunksSha256,
    IReadOnlyList<IndexedFile> Files, IReadOnlyList<SkippedFile> SkippedFiles,
    string[] Limitations);
