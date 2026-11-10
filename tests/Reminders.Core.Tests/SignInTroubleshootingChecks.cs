using System.Text.Json;
using Reminders.Windows.Services;

internal static class SignInTroubleshootingChecks
{
    public static void Run(Action<bool, string> check)
    {
        var unknown = SignInTroubleshooting.Explain("ERROR", "Unexpected response", "session-token=secret", SignInStage.Password);
        check(unknown.Reason == "Unexpected response" && unknown.NextStep.Contains("exact cause"), "unknown failure stays explicit about uncertainty");
        check(!unknown.Reason.Contains("secret") && !unknown.NextStep.Contains("Advanced Data Protection"), "unknown failure exposes no raw detail or guessed account setting");
        var network = SignInTroubleshooting.Explain("NETWORK", "iCloud sign-in failed", "HTTP 503: private response", SignInStage.Password);
        check(network.Reason.Contains("HTTP 503") && !network.Reason.Contains("private response"), "HTTP status is visible without response data");
        check(network.NextStep.Contains("internet connection"), "network failure suggests a connection check");
        var accepted = SignInTroubleshooting.Explain("NETWORK", "Apple accepted the verification code, but iCloud account sign-in failed (HTTP 500).", "HTTP 500: private", SignInStage.Verification);
        check(accepted.Title.Contains("Code accepted") && accepted.GuideStep == 4, "accepted code failure opens account setup help");
        check(!accepted.NextStep.Contains("new code") && !accepted.NextStep.Contains("six digits"), "accepted code failure does not tell users to replace their accepted code");
        var wrong = SignInTroubleshooting.Explain("2FA_REQUIRED", "Incorrect verification code.", "", SignInStage.Verification);
        check(wrong.Reason == "Incorrect verification code." && wrong.GuideStep == 1, "Apple's incorrect code reason is preserved and SMS help is selected");
        var terms = SignInTroubleshooting.Explain("TERMS_REQUIRED", "Updated terms", "", SignInStage.Verification);
        check(terms.GuideStep == 4 && terms.NextStep.Contains("terms"), "terms error during verification opens account setup help");
        var missing = SignInTroubleshooting.Explain("AUTH_REQUIRED", "Apple has no trusted phone number for this account.", "", SignInStage.Delivery);
        check(missing.GuideStep == 1 && missing.Title.Contains("trusted phone number"), "explicit missing number selects phone setup help");
        var invalid = SignInTroubleshooting.Explain("BAD_REQUEST", "missing apple_id", "", SignInStage.Password);
        check(!invalid.Title.Contains("Verification"), "password-form validation does not become a verification failure");
        var oldPreferences = JsonSerializer.Deserialize<UiPreferenceData>("""{"navigation_pane_open":false,"CompletionAnimation":false,"DetailPaneFraction":0.65}""")!;
        check(!oldPreferences.ICloudSetupCompleted, "existing preferences default to showing the guide once");
        oldPreferences.ICloudSetupCompleted = true;
        var saved = JsonSerializer.Deserialize<UiPreferenceData>(JsonSerializer.Serialize(oldPreferences))!;
        check(saved.ICloudSetupCompleted && !saved.NavigationPaneOpen && !saved.CompletionAnimation && saved.DetailPaneFraction == 0.65, "guide completion survives serialization without overwriting other preferences");
    }
}
