using Reminders.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    // The same checked-in policy is embedded in every build, including offline
    // sign-in. Its format is deliberately limited to headings and paragraphs.
    private static ScrollViewer CreatePrivacyPolicyContent()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("Reminders.Windows.Privacy.md")
            ?? throw new InvalidOperationException(L10n.Get("PrivacyPolicyMissing"));
        using var reader = new StreamReader(stream);
        var panel = new StackPanel { Spacing = 14 };
        foreach (var block in reader.ReadToEnd().Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (block.StartsWith("# ", StringComparison.Ordinal)) continue;
            var heading = block.StartsWith("## ", StringComparison.Ordinal);
            panel.Children.Add(new TextBlock
            {
                Text = heading ? block[3..].Trim() : block.Trim().Replace("\n", " "),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontSize = heading ? 18 : 14,
                FontWeight = heading ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Margin = heading ? new Thickness(0, 10, 0, 0) : new Thickness(0)
            });
        }
        panel.Children.Add(new HyperlinkButton
        {
            Content = L10n.Get("ContactPaulSavvas"),
            NavigateUri = new Uri("mailto:hello@paulsavvas.com"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0)
        });
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 480 };
    }

    private async void PrivacyPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.RequestedTheme,
                Title = L10n.Get("PrivacyPolicy"),
                Content = CreatePrivacyPolicyContent(),
                CloseButtonText = L10n.Get("Close"),
                DefaultButton = ContentDialogButton.Close
            };
            await dialog.ShowAsync();
        }
        catch (Exception error) { ShowInfo(L10n.Get("CouldntOpenPrivacyPolicy"), L10n.Error(error), InfoBarSeverity.Error); }
        finally { _dialogOpen = false; }
    }
}
