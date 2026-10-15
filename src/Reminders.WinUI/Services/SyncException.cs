namespace Reminders.Windows;

public sealed class SyncException(string code, string message, string detail = "") : Exception(message)
{
    public string Code { get; } = code;
    public string Detail { get; } = detail;
}
