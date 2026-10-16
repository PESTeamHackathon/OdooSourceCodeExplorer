namespace Chat.App;

public enum ChatMode { Indexed, Exploration }

// Immutable server capabilities; no paths, diagnostics or provider details reach the UI.
public sealed record ChatCapabilities(bool IndexedAvailable, bool ExplorationAvailable, ChatMode DefaultMode)
{
    public bool IsAvailable(ChatMode mode) => mode switch
    {
        ChatMode.Indexed => IndexedAvailable,
        ChatMode.Exploration => ExplorationAvailable,
        _ => false
    };
}
