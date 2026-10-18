#if DEBUG
namespace Reminders.Windows.Services;

// Account-free integration fixture for the real WebView2 popup host. It never
// navigates to Apple or reads credentials. Excluded from Release builds.
internal static class AppleSignInPreview
{
    public static bool Enabled => Environment.GetCommandLineArgs().Contains("--preview-apple-popup", StringComparer.OrdinalIgnoreCase);
    public const string Html = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Popup handoff check</title>
        <style>body{font:18px system-ui;padding:24px}button{font:inherit;padding:12px}</style></head>
        <body><h1>Account-free popup check</h1><p id="result">Ready to check window.opener and postMessage.</p>
        <button onclick="openPopup()">Open test popup</button>
        <script>
        let child;
        addEventListener('message', e => {
          if (e.source === child && e.data === 'handoff-ok') {
            document.getElementById('result').textContent = 'PASS: popup returned its result to the original page.';
          }
        });
        function openPopup() {
          child = window.open('about:blank', 'handoff-check');
          if (!child || child.closed) {
            document.getElementById('result').textContent = 'FAIL: popup was closed or detached.';
            return;
          }
          child.document.write('<!doctype html><html lang="en"><head><title>Test child window</title></head><body style="font:18px system-ui;padding:24px"><h1>Test child window</h1><button style="font:inherit;padding:12px" onclick="window.opener.postMessage(\'handoff-ok\',\'*\');window.close()">Return test result</button></body></html>');
          child.document.close();
        }
        </script></body></html>
        """;
}
#endif
