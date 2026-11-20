using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Windows.ApplicationModel.Resources;
using Reminders.Core;

namespace Reminders.Windows.Services;

internal static class L10n
{
    private const int AppModelErrorNoPackage = 15700;
    private static readonly Lazy<ResourceManager> Manager = new(CreateManager);
    private static readonly Lazy<ResourceMap> Strings = new(() => Manager.Value.MainResourceMap.GetSubtree("Resources"));

    internal static string Get(string key) => Strings.Value.GetValue(key).ValueAsString;

    internal static string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    internal static string Message(PresentationMessage message) => Format(message.Key, message.Arguments);

    internal static string Error(Exception exception) =>
        exception.GetPresentation() is { } presentation ? Message(presentation) : exception.Message;

    internal static string EventMessage(JsonElement payload, string fallback)
    {
        var presentation = payload.Property("presentation");
        return presentation.Text("Key") is { Length: > 0 } key
            ? Format(key, presentation.GetProperty("Arguments").EnumerateArray().Select(FormatArgument).ToArray())
            : payload.Text("message", fallback);
    }

    private static object FormatArgument(JsonElement argument) => argument.ValueKind switch
    {
        JsonValueKind.Number when argument.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => argument.GetDouble(),
        _ => argument.ToString()
    };

    private static ResourceManager CreateManager()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) == AppModelErrorNoPackage
            ? new ResourceManager(Path.Combine(AppContext.BaseDirectory, "Reminders.pri"))
            : new ResourceManager();
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
