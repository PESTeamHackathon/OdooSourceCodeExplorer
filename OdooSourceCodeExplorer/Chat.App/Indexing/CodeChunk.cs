namespace Chat.App.Indexing;

public sealed record SourceDocument(string RelativePath, string Content, string? ModuleName, string? ModulePath);

// Repository-independent contract: a future Roslyn adapter can emit the same evidence format.
public interface ISourceChunker
{
    bool CanHandle(string relativePath);
    IReadOnlyList<CodeChunk> Chunk(SourceDocument document);
}

public sealed record CodeChunk(
    string Id, string Language, string RelativePath, string? ModuleName, string? ModulePath,
    string Kind, string? Symbol, string? QualifiedName, string? ClassName,
    int StartLine, int EndLine, string Context, string Content);
