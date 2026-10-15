namespace Chat.App;

public enum ChatRole { User, Assistant }

public sealed record ChatMessage(ChatRole Role, string Content);
