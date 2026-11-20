using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Reminders.Core;
using Reminders.Windows.Services;

internal static class EnglishUiResourceChecks
{
    private static readonly Dictionary<string, string> Strings = Load("EnglishUiResources.resw").Root!.Elements("data")
        .ToDictionary(entry => (string)entry.Attribute("name")!, entry => (string)entry.Element("value")!, StringComparer.Ordinal);

    internal static string Format(PresentationMessage message) =>
        string.Format(CultureInfo.GetCultureInfo("en-US"), Strings[message.Key], message.Arguments);

    internal static void Run(Action<bool, string> check)
    {
        const string xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var uids = Load("MainWindow.xaml").Root!.DescendantsAndSelf().Attributes(XName.Get("Uid", xaml))
            .Select(attribute => attribute.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var uid in uids)
            check(Strings.Keys.Any(key => key.StartsWith(uid + ".", StringComparison.Ordinal)), $"XAML resource for {uid}");
        foreach (var key in Strings.Keys.Where(key => key.Contains('.')))
            check(uids.Contains(key.Split('.', 2)[0]), $"XAML owner for {key}");
        foreach (var section in Enum.GetValues<ReminderSectionId>())
            check(Strings.ContainsKey($"ReminderSection{section}"), $"group title for {section}");
        foreach (var value in Strings.Values)
            _ = CompositeFormat.Parse(value);

        check(Format(new("ReminderCountMany", 1200)) == "1,200 reminders", "count retains English number formatting");
        check(Format(new("NotifyNamesAndMore", "One, Two, Three", 2)) == "One, Two, Three, and 2 more", "notification names and count placeholders");
        var due = new DateTime(2026, 10, 12, 15, 30, 0);
        check(Format(new("DueDateAndTime", due)) == "Mon, Oct 12 · " + due.ToString("t", CultureInfo.GetCultureInfo("en-US")),
            "date template retains English rendering");
        var timedDue = new DateTimeOffset(due, TimeSpan.Zero);
        var notificationCulture = new CultureInfo("en-US", useUserOverride: false);
        notificationCulture.DateTimeFormat.ShortTimePattern = "h:mm tt";
        check(string.Format(notificationCulture, Strings["NotifyTimedDate"], timedDue) == "Mon 12 Oct, 3:30 PM",
            "timed notification honors a 12-hour short time pattern");
        notificationCulture.DateTimeFormat.ShortTimePattern = "HH:mm";
        check(string.Format(notificationCulture, Strings["NotifyTimedDate"], timedDue) == "Mon 12 Oct, 15:30",
            "timed notification honors a 24-hour short time pattern");
        check(Format(new("NotifyMissedOne", 1)) == "1 reminder was due while you were away",
            "one missed reminder uses singular wording");
        check(Format(new("NotifyMissedMany", 2)) == "2 reminders were due while you were away",
            "multiple missed reminders use plural wording");
        var exception = new CoreException("NETWORK", "diagnostic message", "private detail").WithPresentation("SignInReasonWithHttpStatus", "display reason", "HTTP 503");
        var presentation = exception.Body.Property("presentation");
        check(exception.Body.Text("message") == "diagnostic message" && presentation.Text("Key") == "SignInReasonWithHttpStatus"
            && presentation.Array("Arguments").Count == 2 && !presentation!.ToJsonString().Contains("private detail"), "event carries display metadata alongside unmodified diagnostics");
    }

    private static XDocument Load(string name)
    {
        using var stream = typeof(EnglishUiResourceChecks).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing test resource {name}");
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }
}
