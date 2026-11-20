namespace Reminders.Core;

/// <summary>A resource key and display arguments for the in-process UI.</summary>
public sealed record PresentationMessage(string Key, params object[] Arguments);

public static class PresentationExceptionExtensions
{
    private static readonly object PresentationKey = new();

    public static T WithPresentation<T>(this T exception, string key, params object[] arguments) where T : Exception
    {
        exception.Data[PresentationKey] = new PresentationMessage(key, arguments);
        return exception;
    }

    public static PresentationMessage? GetPresentation(this Exception exception) =>
        exception.Data[PresentationKey] as PresentationMessage;
}
