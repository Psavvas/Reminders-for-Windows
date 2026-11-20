namespace Reminders.Windows.Services;

internal sealed record ICloudSetupStep(string Title, string Summary, string Instructions, string Note, string LinkLabel, string Link, string Glyph,
    string QuickInstructions, string DetailsTitle, bool ImportantNote = false);

internal static class ICloudSetupGuide
{
    public static IReadOnlyList<ICloudSetupStep> Steps { get; } = new ICloudSetupStep[]
    {
        new(L10n.Get("SetupRemindersTitle"),
            L10n.Get("SetupRemindersSummary"),
            L10n.Get("SetupRemindersDetails"),
            L10n.Get("SetupRemindersNote"),
            L10n.Get("SetupRemindersLink"), "https://www.icloud.com/reminders/", "\uE8FD",
            L10n.Get("SetupRemindersInstructions"), L10n.Get("SetupRemindersDetailsHeading")),
        new(L10n.Get("SetupVerificationTitle"),
            L10n.Get("SetupVerificationSummary"),
            L10n.Get("SetupVerificationDetails"),
            L10n.Get("SetupVerificationNote"),
            L10n.Get("SetupVerificationLink"), "https://account.apple.com/", "\uE8F2",
            L10n.Get("SetupVerificationInstructions"), L10n.Get("SetupVerificationDetailsHeading")),
        new(L10n.Get("SetupDataProtectionTitle"),
            L10n.Get("SetupDataProtectionSummary"),
            L10n.Get("SetupDataProtectionDetails"),
            L10n.Get("SetupDataProtectionNote"),
            L10n.Get("SetupDataProtectionLink"), "https://support.apple.com/en-us/108756", "\uE72E",
            L10n.Get("SetupDataProtectionInstructions"),
            L10n.Get("SetupDataProtectionDetailsHeading"), ImportantNote: true),
        new(L10n.Get("SetupWebAccessTitle"),
            L10n.Get("SetupWebAccessSummary"),
            L10n.Get("SetupWebAccessDetails"),
            L10n.Get("SetupWebAccessNote"),
            L10n.Get("SetupWebAccessLink"), "https://support.apple.com/en-us/102630", "\uE774",
            L10n.Get("SetupWebAccessInstructions"), L10n.Get("SetupWebAccessDetailsHeading")),
        new(L10n.Get("SetupVerifyListsTitle"),
            L10n.Get("SetupVerifyListsSummary"),
            L10n.Get("SetupVerifyListsDetails"),
            L10n.Get("SetupVerifyListsNote"),
            L10n.Get("SetupVerifyListsLink"), "https://www.icloud.com/reminders/", "\uE73E",
            L10n.Get("SetupVerifyListsInstructions"), L10n.Get("SetupVerifyListsDetailsHeading"))
    };
}
