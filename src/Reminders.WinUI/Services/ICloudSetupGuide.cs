namespace Reminders.Windows.Services;

internal sealed record ICloudSetupStep(string Title, string Summary, string Instructions, string Note, string LinkLabel, string Link, string Glyph);

internal static class ICloudSetupGuide
{
    public static IReadOnlyList<ICloudSetupStep> Steps { get; } = new ICloudSetupStep[]
    {
        new("Turn on iCloud Reminders",
            "Use the Apple Account that holds your reminder lists.",
            "On iPhone or iPad, open Settings → your name → iCloud → See All (or Show All), then turn on Reminders. On Mac, open System Settings → your name → iCloud and enable Reminders. Create a list in the iCloud section of Apple's Reminders app if you have not used it before.",
            "When signing in here, use your regular Apple Account password. An app-specific password is not a replacement for this sign-in.",
            "Open iCloud Reminders", "https://www.icloud.com/reminders/", "\uE8FD"),
        new("Receive verification codes by text",
            "This app supports two-factor verification using SMS to a trusted phone number.",
            "Open Settings → your name → Sign-In & Security → Two-Factor Authentication on your Apple device. Enable two-factor authentication and check that a trusted phone number can receive text messages. You can also manage trusted numbers at account.apple.com.",
            "Use the six digits in the most recent SMS. Codes displayed in Apple device notifications use a different verification method that this app does not support. A text arriving confirms delivery; sign-in can still fail at a later step.",
            "Manage your Apple Account", "https://account.apple.com/", "\uE8F2"),
        new("Check Advanced Data Protection",
            "This version requires Advanced Data Protection to be off for compatibility.",
            "On iPhone or iPad, open Settings → your name → iCloud → Advanced Data Protection. On Mac, open System Settings → your name → iCloud → Advanced Data Protection. If it is on, review Apple's guidance before choosing whether to turn it off.",
            "Turning it off changes encryption protection for your whole iCloud account. If you prefer to keep Advanced Data Protection on, use Apple's Reminders instead. This app cannot change this setting for you.",
            "Read Apple's protection guidance", "https://support.apple.com/en-us/108756", "\uE72E"),
        new("Allow access to iCloud data on the web",
            "Reminders for Windows needs access to your iCloud data through Apple's web services.",
            "In your Apple device's iCloud settings, turn on Access iCloud Data on the Web. On newer versions, open iCloud.com (or Data Access on iCloud.com) in those settings and turn on Allow Data Access.",
            "Advanced Data Protection normally turns web access off. Check web access separately, even after changing Advanced Data Protection. If browser access asks for extra device approval, this app does not handle that approval flow.",
            "Read Apple's web-access guidance", "https://support.apple.com/en-us/102630", "\uE774"),
        new("Check your account in a browser",
            "Before signing in here, make sure the same account can open Reminders on iCloud.com.",
            "Open iCloud Reminders using the link below. Finish any account setup and accept any updated iCloud terms Apple shows. Confirm your iCloud lists appear, then return here to sign in. Keep your phone nearby for the SMS code.",
            "If the browser works but this app fails, review the exact error and the setup guide. The app cannot inspect these account settings, so a sign-in failure alone does not prove one is wrong. Troubleshooting also lets you open the diagnostic-log folder.",
            "Check Reminders in your browser", "https://www.icloud.com/reminders/", "\uE73E")
    };
}
