using Reminders.Core;

namespace Reminders.Windows;

public sealed class SyncException(string code, string message, string detail = "") : Exception(message)
{
    public string Code { get; } = code;
    public string Detail { get; } = detail;

    internal SyncException(CoreException error) : this(error.Code, error.Message, error.Detail)
    {
        if (error.GetPresentation() is { } presentation) this.WithPresentation(presentation.Key, presentation.Arguments);
    }
}
