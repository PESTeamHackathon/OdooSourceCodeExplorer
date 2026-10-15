namespace Chat.Web.Components.Pages;

/// <summary>Placeholder Odoo-style app tiles shown on the home page. They are decorative and do not link anywhere.</summary>
public sealed record AppModule(string Name, string Icon);

public static class AppModules
{
    private const string Purple = "#714B67";
    private const string Orange = "#F5A623";
    private const string Teal = "#21B799";
    private const string Navy = "#2C5C7A";
    private const string Coral = "#E46E78";
    private const string Blue = "#1E88E5";

    public static IReadOnlyList<AppModule> All { get; } =
    [
        new("Accounting", $"""<circle cx="16" cy="16" r="8" fill="{Orange}"/><circle cx="32" cy="32" r="8" fill="{Teal}"/><rect x="21" y="5" width="6" height="38" rx="3" fill="{Purple}" transform="rotate(45 24 24)"/>"""),
        new("Knowledge", $"""<rect x="19" y="5" width="20" height="26" rx="1" fill="{Teal}"/><path d="M9 13h20v30l-10-8-10 8z" fill="{Purple}"/>"""),
        new("Sign", $"""<path d="M8 31c6-2 10-16 14-20 3-3 6 1 2 8s-8 14-5 14 6-9 9-9 2 6 5 6 6-4 8-4" stroke="{Navy}" stroke-width="3" fill="none" stroke-linecap="round" stroke-linejoin="round"/><path d="M12 38h26" stroke="{Teal}" stroke-width="3" stroke-linecap="round"/>"""),
        new("CRM", $"""<path d="M5 15h22l-9 20z" fill="{Teal}"/><path d="M43 15H21l9 20z" fill="{Purple}"/><path d="M18 22h12l-6 13z" fill="#1F2937"/>"""),
        new("Studio", $"""<rect x="21" y="5" width="6" height="38" rx="3" fill="{Blue}" transform="rotate(45 24 24)"/><rect x="21" y="5" width="6" height="38" rx="3" fill="{Purple}" transform="rotate(-45 24 24)"/>"""),
        new("Subscriptions", $"""<path d="M30 10.5A14 14 0 1 0 38 24" stroke="{Orange}" stroke-width="5" fill="none" stroke-linecap="round"/><circle cx="37" cy="15" r="4" fill="{Teal}"/><circle cx="31" cy="37" r="3.5" fill="{Teal}"/>"""),
        new("AI", $"""<path d="M6 40L17 9h7l11 31h-7l-7-21-7 21z" fill="{Purple}"/><path d="M13 31h15l-2 6H11z" fill="{Coral}"/><rect x="36" y="9" width="6" height="31" rx="3" fill="{Orange}"/>"""),
        new("Point of Sale", $"""<path d="M7 12h34l2 22H5z" fill="{Orange}"/><rect x="13" y="12" width="7" height="22" fill="{Purple}"/><rect x="28" y="12" width="7" height="22" fill="{Purple}"/><path d="M5 34h38" stroke="#C47F12" stroke-width="2"/>"""),
        new("Discuss", """<path d="M24 7a17 17 0 1 1 0 34H7V24A17 17 0 0 1 24 7z" fill="#F57C00"/><path d="M24 7a17 17 0 0 1 17 17H24z" fill="#FB9B3A"/>"""),
        new("Documents", $"""<rect x="10" y="9" width="16" height="26" rx="2" fill="{Blue}" transform="rotate(-12 18 22)"/><rect x="18" y="11" width="16" height="26" rx="2" fill="{Orange}" transform="rotate(10 26 24)"/><rect x="20" y="17" width="10" height="20" rx="2" fill="{Coral}" transform="rotate(25 25 27)"/>"""),
        new("Project", $"""<path d="M7 25l7-7 10 10-7 7z" fill="{Purple}"/><path d="M17 35L36 10l7 6-19 25z" fill="{Teal}"/>"""),
        new("Timesheets", $"""<circle cx="24" cy="24" r="17" fill="{Navy}"/><path d="M24 7A17 17 0 0 0 7 24" stroke="{Coral}" stroke-width="4" fill="none"/><path d="M24 24l-7-9" stroke="#fff" stroke-width="4" stroke-linecap="round"/>"""),
        new("Field Service", $"""<path d="M30 5L11 28h13l-6 16 19-23H24z" fill="{Orange}"/><path d="M30 5L11 28h13z" fill="{Purple}"/>"""),
        new("Planning", $"""<rect x="6" y="11" width="36" height="26" rx="3" fill="{Orange}"/><rect x="22" y="7" width="4" height="34" rx="2" fill="{Purple}"/><path d="M18 18l-7 6 7 6zM30 18l7 6-7 6z" fill="#fff"/>"""),
        new("Helpdesk", $"""<path d="M19 6h10v13h13v10H29v13H19V29H6V19h13z" fill="{Teal}"/><path d="M19 19h10v10H19z" fill="#145F55"/>"""),
        new("eCommerce", $"""<path d="M17 16v-3a7 7 0 0 1 14 0v3" stroke="{Purple}" stroke-width="3" fill="none"/><path d="M8 16h32l-3 25H11z" fill="{Purple}"/><path d="M8 16h32l-1 7H9z" fill="#8E5E83"/>"""),
        new("Website", $"""<circle cx="24" cy="24" r="17" fill="{Teal}"/><path d="M7 26c6-5 12-5 17 0s11 5 17 0a17 17 0 0 1-34 0z" fill="{Blue}"/><path d="M8 19c6-4 11-4 16 0s11 4 16 0" stroke="#fff" stroke-width="3" fill="none"/>"""),
        new("Email Marketing", $"""<path d="M42 6L6 22l14 5z" fill="{Blue}"/><path d="M42 6L20 27l6 15z" fill="{Purple}"/>"""),
        new("Purchase", $"""<rect x="6" y="11" width="36" height="26" rx="3" fill="{Purple}"/><rect x="6" y="11" width="36" height="9" rx="3" fill="{Teal}"/><rect x="6" y="20" width="36" height="3" fill="#1F2937"/>"""),
        new("Inventory", $"""<path d="M24 5l17 10v18L24 43 7 33V15z" fill="{Orange}"/><path d="M24 24l17-9v18L24 43z" fill="{Purple}"/><path d="M24 24L7 15v18l17 10z" fill="#C2582C" opacity=".55"/>"""),
        new("Manufacturing", $"""<path d="M6 40V22l12-8v8l12-8v26z" fill="{Teal}"/><rect x="30" y="16" width="12" height="24" fill="{Orange}"/><rect x="18" y="26" width="8" height="14" fill="#145F55"/>"""),
        new("Sales", $"""<rect x="7" y="22" width="9" height="19" rx="1" fill="{Purple}"/><rect x="19" y="14" width="9" height="27" rx="1" fill="{Coral}"/><rect x="31" y="7" width="9" height="34" rx="1" fill="{Orange}"/>"""),
        new("HR", $"""<circle cx="24" cy="14" r="7" fill="{Purple}"/><rect x="5" y="25" width="38" height="11" rx="5.5" fill="{Orange}"/><rect x="18" y="25" width="25" height="11" rx="5.5" fill="{Teal}"/>"""),
        new("Dashboard", $"""<rect x="6" y="6" width="16" height="16" rx="2" fill="{Purple}"/><rect x="26" y="6" width="16" height="6" rx="2" fill="{Coral}"/><rect x="26" y="16" width="16" height="6" rx="2" fill="{Coral}"/><rect x="6" y="26" width="7" height="16" rx="2" fill="{Blue}"/><rect x="16" y="26" width="7" height="16" rx="2" fill="{Blue}"/><rect x="26" y="26" width="7" height="16" rx="2" fill="{Teal}"/><rect x="36" y="26" width="6" height="16" rx="2" fill="{Teal}"/>"""),
    ];
}
