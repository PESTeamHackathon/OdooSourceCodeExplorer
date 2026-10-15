namespace Chat.App;

public enum AttachmentKind { Image, Text }

// File content supplied by the user. It is untrusted data, never instructions.
public sealed record ChatAttachment(string FileName, string MediaType, byte[] Data)
{
    public const int MaxPerMessage = 5;
    public const int MaxImageBytes = 5 * 1024 * 1024;
    public const int MaxTextBytes = 200 * 1024;
    public const int MaxConversationBytes = 20 * 1024 * 1024;

    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".log",
        ".py", ".js", ".ts", ".cs", ".java", ".sql", ".html", ".css", ".ini", ".conf"
    };

    // Comma-separated list for a file picker's accept attribute.
    public static string AcceptedExtensions { get; } = string.Join(",", ImageTypes.Keys.Concat(TextExtensions));

    public AttachmentKind Kind => MediaType.StartsWith("image/", StringComparison.Ordinal) ? AttachmentKind.Image : AttachmentKind.Text;

    public int MaxBytes => Kind == AttachmentKind.Image ? MaxImageBytes : MaxTextBytes;

    // The media type is derived from the extension, never trusted from the browser.
    public static bool TryGetMediaType(string fileName, out string mediaType)
    {
        var extension = Path.GetExtension(fileName);
        if (ImageTypes.TryGetValue(extension, out var imageType))
        {
            mediaType = imageType;
            return true;
        }
        mediaType = TextExtensions.Contains(extension) ? "text/plain" : "";
        return mediaType.Length > 0;
    }
}
