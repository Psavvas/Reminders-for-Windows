namespace Reminders.Windows.Services;

internal sealed record ICloudSetupStep(string Title, string Summary, string Instructions, string Note, string LinkLabel, string Link, string Glyph,
    string QuickInstructions, string DetailsTitle, bool ImportantNote = false);

internal static class ICloudSetupGuide
{
    public static IReadOnlyList<ICloudSetupStep> Steps { get; } = new ICloudSetupStep[]
    {
        new("Turn on iCloud Reminders",
            "Use the Apple Account that holds your reminder lists.",
            "On Mac, open System Settings → your name → iCloud, then enable Reminders. If you have not used iCloud Reminders before, create a list in the iCloud section of Apple's Reminders app.",
            "Sign in with your regular Apple Account password. App-specific passwords do not work with this app.",
            "Open iCloud Reminders", "https://www.icloud.com/reminders/", "\uE8FD",
            "On iPhone or iPad\nSettings → your name → iCloud → See All (or Show All)\nTurn on Reminders.", "Mac and account details"),
        new("Choose verification",
            "Verify with an Apple device or a text message.",
            "Open Settings → your name → Sign-In & Security → Two-Factor Authentication on your Apple device. Enable two-factor authentication and check that a trusted phone number can receive text messages. You can also manage trusted numbers at account.apple.com.",
            "Choose Sign in with an Apple device to use Apple's device prompts, then choose Trust when offered. For native text verification, use the six digits in the most recent SMS. Your trusted session is remembered on this PC until Apple expires or revokes it.",
            "Manage your Apple Account", "https://account.apple.com/", "\uE8F2",
            "Keep a trusted Apple device or phone nearby.\nYou'll choose a verification method when you sign in.", "Verification options"),
        new("Check Advanced Data Protection",
            "This app currently requires Advanced Data Protection to be off.",
            "On Mac, open System Settings → your name → iCloud → Advanced Data Protection. If it is on, review Apple's guidance before choosing whether to turn it off.",
            "Turning this off changes encryption protection for your whole iCloud account. You can keep it on and use Apple's Reminders instead.",
            "Read Apple's protection guidance", "https://support.apple.com/en-us/108756", "\uE72E",
            "On iPhone or iPad\nSettings → your name → iCloud → Advanced Data Protection",
            "Mac instructions", ImportantNote: true),
        new("Allow web access",
            "Let this app reach your reminders through iCloud's web services.",
            "In your Apple device's iCloud settings, turn on Access iCloud Data on the Web. On newer versions, open iCloud.com (or Data Access on iCloud.com) in those settings and turn on Allow Data Access.",
            "Advanced Data Protection normally turns web access off. Check web access separately, even after changing Advanced Data Protection. If browser access asks for extra device approval, this app does not handle that approval flow.",
            "Read Apple's web-access guidance", "https://support.apple.com/en-us/102630", "\uE774",
            "In your Apple device's iCloud settings, turn on Access iCloud Data on the Web.", "Other settings labels"),
        new("Check your lists",
            "Make sure your reminders are available on iCloud.com.",
            "Open iCloud Reminders using the link below. Finish any account setup and accept any updated iCloud terms Apple shows. Confirm your iCloud lists appear, then return here to sign in. Keep a trusted Apple device or phone nearby for verification.",
            "If the browser works but this app fails, review the exact error and the setup guide. The app cannot inspect these account settings, so a sign-in failure alone does not prove one is wrong. Troubleshooting also lets you open the diagnostic-log folder.",
            "Open iCloud Reminders", "https://www.icloud.com/reminders/", "\uE73E",
            "Open iCloud Reminders below and sign in.\nAccept any updated terms, then check that your lists appear.", "Troubleshooting")
    };
}
