using System.Text.Json;
using Reminders.Windows.Services;

internal static class OnboardingChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var fresh = new UiPreferenceData();
        check(fresh.ShouldShowWelcome && !fresh.ICloudSetupCompleted, "New installs see the welcome before iCloud setup");
        check(!fresh.ShouldOfferAppTour(authenticated: false), "Cached or signed-out sessions do not start the first-login tour");
        check(fresh.ShouldOfferAppTour(authenticated: true), "A successful first login offers the app tour");

        fresh.WelcomeCompleted = true;
        var continued = RoundTrip(fresh);
        check(!continued.ShouldShowWelcome && !continued.ICloudSetupCompleted,
            "Continuing the welcome persists without claiming that iCloud setup is complete");
        check(continued.ShouldOfferAppTour(authenticated: true), "Completing the welcome does not consume the login tour");

        continued.AppTourCompleted = true;
        var finished = RoundTrip(continued);
        check(finished.AppTourCompleted && !finished.ShouldOfferAppTour(authenticated: true),
            "Finishing or skipping the tour is remembered across launches");
        check(!finished.ShouldOfferAppTour(authenticated: false), "Signing out does not reenable a completed tour");

        var upgraded = JsonSerializer.Deserialize<UiPreferenceData>("""
            {"ICloudSetupCompleted":true,"InterfaceStyle":"apple","DetailPaneFraction":0.4,"navigation_pane_open":false}
            """)!;
        check(!upgraded.ShouldShowWelcome, "Existing users bypass the new welcome when upgrading");
        check(upgraded.ShouldOfferAppTour(authenticated: true), "Existing users can discover the tour after an authenticated launch");
        var preserved = RoundTrip(upgraded);
        check(preserved.InterfaceStyle == "apple" && preserved.DetailPaneFraction == 0.4 && !preserved.NavigationPaneOpen,
            "Saving onboarding progress preserves appearance and layout preferences");
        check(!JsonSerializer.Serialize(preserved).Contains("ShouldShowWelcome", StringComparison.Ordinal),
            "Derived onboarding decisions are not stored as preferences");
        check(JsonSerializer.Deserialize<UiPreferenceData>("{}")!.ShouldShowWelcome,
            "Older preferences without completed setup still enter the welcome");
    }

    private static UiPreferenceData RoundTrip(UiPreferenceData value) =>
        JsonSerializer.Deserialize<UiPreferenceData>(JsonSerializer.Serialize(value))!;
}
