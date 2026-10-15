using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Chat.App.Indexing;

namespace Chat.Indexer;

/// <summary>
/// Conservative lexical segmentation, not a Python parser or semantic analyzer.
/// Strings/comments and continued expressions are masked before detecting declarations.
/// Nested functions remain inside their enclosing method to retain execution context.
/// </summary>
public sealed class PythonChunker : ISourceChunker
{
    private static readonly Regex Declaration = new(
        @"^(?<indent>[ \t]*)(?<kind>class|(?:async\s+)?def)\s+(?<name>[^\W\d]\w*)\b", RegexOptions.CultureInvariant);
    private static readonly Regex ModelAssignment = new(@"^\s*_(name|inherit|description)\s*=", RegexOptions.CultureInvariant);

    public bool CanHandle(string relativePath) => relativePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<CodeChunk> Chunk(SourceDocument document)
    {
        var lines = document.Content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // Split's terminal empty item is not a source line.
        if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1];
        var lexical = Mask(lines);
        var nodes = new List<Node>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lexical[i].StatementStart) continue;
            var match = Declaration.Match(lexical[i].Text);
            if (!match.Success) continue;
            var indent = Indent(match.Groups["indent"].Value);
            var end = i + 1;
            while (end < lines.Length && (!lexical[end].StatementStart ||
                   string.IsNullOrWhiteSpace(lexical[end].Text) || Indent(lines[end]) > indent)) end++;
            var start = i;
            // Attach decorators, including multiline decorator expressions.
            var previous = i - 1;
            while (previous >= 0)
            {
                while (previous >= 0 && (!lexical[previous].StatementStart || string.IsNullOrWhiteSpace(lexical[previous].Text))) previous--;
                if (previous < 0 || Indent(lines[previous]) != indent || !lexical[previous].Text.TrimStart().StartsWith('@')) break;
                start = previous--;
            }
            nodes.Add(new Node(start, i, end, indent, match.Groups["kind"].Value == "class", match.Groups["name"].Value));
        }

        var chunks = new List<CodeChunk>();
        var classContexts = new Dictionary<Node, string>();
        Segment(0, lines.Length, null, null, nodes.Where(n => n.Indent == 0).ToList());
        return chunks;

        void Segment(int start, int end, Node? owner, string? ownerName, List<Node> children)
        {
            var cursor = start;
            foreach (var child in children)
            {
                Add(cursor, child.Start, owner is null ? "module" : "class-context", owner, ownerName);
                var qualified = ownerName is null ? child.Name : ownerName + "." + child.Name;
                if (child.IsClass)
                {
                    var nested = nodes.Where(n => n.Header > child.Header && n.Header < child.End && n.Indent > child.Indent).ToList();
                    var direct = nested.Where(n => !nested.Any(p => p.Header < n.Header && p.End > n.Header && p.Indent < n.Indent)).ToList();
                    Segment(child.Start, child.End, child, qualified, direct);
                }
                else Add(child.Start, child.End, owner is null ? "function" : "method", child, qualified, owner, ownerName);
                cursor = child.End;
            }
            Add(cursor, end, owner is null ? "module" : "class-context", owner, ownerName);
        }

        void Add(int start, int end, string kind, Node? symbol, string? qualified, Node? owner = null, string? ownerName = null)
        {
            if (start >= end || !lines[start..end].Any(l => !string.IsNullOrWhiteSpace(l))) return;
            var content = string.Join('\n', lines[start..end]);
            var contextClass = owner ?? (symbol?.IsClass == true ? symbol : null);
            var context = contextClass is null ? "" : ClassContext(contextClass);
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"python-v1\n{document.RelativePath}\n{start + 1}\n{end}\n{kind}\n{qualified}\n{document.ModulePath}\n{context}\n{content}"))).ToLowerInvariant();
            chunks.Add(new CodeChunk(id, "python", document.RelativePath, document.ModuleName, document.ModulePath,
                kind, symbol?.Name, qualified, ownerName ?? (symbol?.IsClass == true ? qualified : null),
                start + 1, end, context, content));
        }

        string ClassContext(Node cls)
        {
            if (classContexts.TryGetValue(cls, out var cached)) return cached;
            var context = new List<string>();
            var headerEnd = cls.Header + 1;
            while (headerEnd < cls.End && !lexical[headerEnd].StatementStart) headerEnd++;
            context.AddRange(lines[cls.Start..headerEnd]);
            var body = Enumerable.Range(headerEnd, cls.End - headerEnd)
                .Where(i => lexical[i].StatementStart && !string.IsNullOrWhiteSpace(lexical[i].Text) && Indent(lines[i]) > cls.Indent).ToList();
            if (body.Count > 0)
            {
                var bodyIndent = body.Min(i => Indent(lines[i]));
                foreach (var i in body.Where(i => Indent(lines[i]) == bodyIndent && ModelAssignment.IsMatch(lexical[i].Text)))
                {
                    var stop = i + 1;
                    while (stop < cls.End && !lexical[stop].StatementStart) stop++;
                    context.AddRange(lines[i..stop]);
                }
            }
            return classContexts[cls] = string.Join('\n', context);
        }
    }

    private sealed record Node(int Start, int Header, int End, int Indent, bool IsClass, string Name);
    private sealed record LexicalLine(string Text, bool StatementStart);
    private static int Indent(string line)
    {
        var column = 0;
        foreach (var ch in line)
        {
            if (ch == ' ') column++;
            else if (ch == '\t') column = (column / 8 + 1) * 8;
            else break;
        }
        return column;
    }

    private static LexicalLine[] Mask(string[] lines)
    {
        var result = new LexicalLine[lines.Length];
        char quote = '\0';
        var triple = false;
        var depth = 0;
        var continuation = false;
        for (var row = 0; row < lines.Length; row++)
        {
            var line = lines[row];
            var masked = line.ToCharArray();
            var statementStart = quote == '\0' && depth == 0 && !continuation;
            for (var col = 0; col < line.Length; col++)
            {
                var ch = line[col];
                if (quote != '\0')
                {
                    masked[col] = ' ';
                    if (ch == '\\')
                    {
                        if (col + 1 < line.Length) masked[++col] = ' ';
                        continue;
                    }
                    if (ch == quote && (!triple || col + 2 < line.Length && line[col + 1] == quote && line[col + 2] == quote))
                    {
                        if (triple) { masked[++col] = ' '; masked[++col] = ' '; }
                        quote = '\0';
                    }
                }
                else if (ch == '#') { Array.Fill(masked, ' ', col, line.Length - col); break; }
                else if (ch is '\'' or '"')
                {
                    quote = ch;
                    triple = col + 2 < line.Length && line[col + 1] == ch && line[col + 2] == ch;
                    masked[col] = ' ';
                    if (triple) { masked[++col] = ' '; masked[++col] = ' '; }
                }
                else if (ch is '(' or '[' or '{') depth++;
                else if (ch is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            }
            continuation = new string(masked).TrimEnd().EndsWith('\\');
            result[row] = new LexicalLine(new string(masked), statementStart);
            // Ordinary strings may span a physical newline only with a backslash.
            if (!triple && quote != '\0' && !line.EndsWith('\\')) quote = '\0';
        }
        return result;
    }
}
