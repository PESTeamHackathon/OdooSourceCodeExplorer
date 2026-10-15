using Chat.App.Indexing;

namespace Chat.Infrastructure.Retrieval;

public static class EmbeddingDocuments
{
    public const string Version = "code-windows-nomic-v1";
    public static IEnumerable<string> ForChunk(CodeChunk chunk, int characters, int overlap)
    {
        if (characters < 2 || overlap < 0 || overlap > characters / 2) throw new ArgumentException("Invalid embedding window.");
        var text = SearchText.Identifiers($"Module: {chunk.ModuleName}\nSymbol: {chunk.QualifiedName}\n{chunk.Context}\n{chunk.Content}");
        for (var start = 0; start < text.Length;)
        {
            var end = Math.Min(text.Length, start + characters);
            // Avoid splitting a UTF-16 surrogate pair at either boundary.
            if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
            yield return "search_document: " + text[start..end];
            if (end == text.Length) break;
            start = end - overlap;
            if (start > 0 && char.IsLowSurrogate(text[start])) start--;
        }
    }
}
