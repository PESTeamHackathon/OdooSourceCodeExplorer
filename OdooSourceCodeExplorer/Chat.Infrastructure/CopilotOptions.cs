namespace Chat.Infrastructure;

public sealed class CopilotOptions
{
    public const string SectionName = "Copilot";
    // Null lets Copilot choose an available default for the authenticated account.
    public string? Model { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    // GitHub bills AI credits in USD; the euro rate is a fixed setting for display, not a live rate.
    public double UsdPerAiCredit { get; set; } = 0.01;
    public double? EurPerUsd { get; set; }
    public string BaseDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot-business-chat");
}
