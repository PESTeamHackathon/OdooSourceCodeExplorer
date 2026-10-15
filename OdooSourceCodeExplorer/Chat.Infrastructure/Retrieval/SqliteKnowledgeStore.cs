using System.Text.Json;
using System.Text.RegularExpressions;
using Chat.App.Indexing;
using Chat.App.Retrieval;
using Microsoft.Data.Sqlite;

namespace Chat.Infrastructure.Retrieval;

public sealed class SqliteKnowledgeStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private SqliteConnection Open(bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    public async Task BuildAsync(IReadOnlyList<CodeChunk> chunks, CancellationToken ct)
    {
        using var connection = Open(false);
        using var transaction = connection.BeginTransaction();
        using var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = """
            CREATE TABLE chunks(id TEXT PRIMARY KEY, module TEXT, symbol TEXT, qualified_name TEXT, json TEXT NOT NULL);
            CREATE VIRTUAL TABLE chunks_fts USING fts5(chunk_id UNINDEXED, symbols, body, tokenize='unicode61');
            """;
        await create.ExecuteNonQueryAsync(ct);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO chunks VALUES ($id, $module, $symbol, $qualified, $json);
            INSERT INTO chunks_fts(chunk_id, symbols, body) VALUES ($id, $symbols, $body);
            """;
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$id", chunk.Id);
            insert.Parameters.AddWithValue("$module", (object?)chunk.ModuleName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$symbol", (object?)chunk.Symbol ?? DBNull.Value);
            insert.Parameters.AddWithValue("$qualified", (object?)chunk.QualifiedName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(chunk, Json));
            insert.Parameters.AddWithValue("$symbols", SearchText.Identifiers($"{chunk.ModuleName} {chunk.QualifiedName} {chunk.RelativePath}"));
            insert.Parameters.AddWithValue("$body", SearchText.Identifiers(chunk.Context + "\n" + chunk.Content));
            await insert.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
    }

    public async Task<IReadOnlyList<RankedChunk>> SearchAsync(string question, string? module, int limit, CancellationToken ct)
    {
        var expression = SearchText.KeywordExpression(question);
        if (expression.Length == 0) return [];
        using var connection = Open(true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chunks_fts.chunk_id, bm25(chunks_fts, 0, 5, 1) AS score
            FROM chunks_fts JOIN chunks ON chunks.id = chunks_fts.chunk_id
            WHERE chunks_fts MATCH $query AND ($module IS NULL OR chunks.module = $module)
            ORDER BY CASE WHEN chunks.symbol = $exact COLLATE NOCASE OR chunks.qualified_name = $exact COLLATE NOCASE THEN 0 ELSE 1 END,
                     score, chunks.id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", expression);
        command.Parameters.AddWithValue("$exact", question.Trim());
        command.Parameters.AddWithValue("$module", (object?)module ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<RankedChunk>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), -reader.GetDouble(1)));
        return result;
    }

    public async Task<IReadOnlyList<CodeChunk>> LoadAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        using var connection = Open(true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM chunks WHERE id = $id";
        var parameter = command.Parameters.Add("$id", SqliteType.Text);
        var result = new List<CodeChunk>();
        foreach (var id in ids)
        {
            parameter.Value = id;
            if (await command.ExecuteScalarAsync(ct) is string json)
                result.Add(JsonSerializer.Deserialize<CodeChunk>(json, Json)!);
        }
        return result;
    }
}

public static class SearchText
{
    public static string Identifiers(string text) => Regex.Replace(text, @"(?<=[a-z0-9])(?=[A-Z])", " ").Replace('_', ' ');
    public static string KeywordExpression(string text)
    {
        var stop = new HashSet<string>("a an the is are was were be to of in on for with and or what how when does do our this that happens can cannot it".Split(' '), StringComparer.OrdinalIgnoreCase);
        return string.Join(" OR ", Regex.Matches(Identifiers(text), @"[\p{L}\p{N}]+")
            .Select(m => m.Value.ToLowerInvariant()).Where(t => !stop.Contains(t)).Distinct().Take(32).Select(t => "\"" + t + "\""));
    }
}
