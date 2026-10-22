using System.Text.RegularExpressions;

namespace Reminders.Windows.Services;

internal enum SignInStage { Password, Delivery, Verification }
internal sealed record SignInFailure(string Title, string Reason, string NextStep, int GuideStep);

internal static class SignInTroubleshooting
{
    public static SignInFailure Explain(string? code, string message, string? detail, SignInStage stage)
    {
        // Show only an HTTP status from diagnostic detail, never raw response
        // bodies or anything that could contain account data or session tokens.
        var http = Regex.Match(detail ?? "", @"\bHTTP [1-5][0-9]{2}\b").Value;
        var reason = string.IsNullOrWhiteSpace(message) ? "Sign-in did not finish." : message;
        if (http.Length > 0 && !reason.Contains(http, StringComparison.Ordinal)) reason += $" ({http})";

        SignInFailure Result(string title, string next, int step) => new(title, reason, next, step);
        if (message.Contains("accepted the verification code", StringComparison.OrdinalIgnoreCase))
            return Result("Code accepted; account sign-in failed",
                "The code was accepted. Check that this account can open Reminders on iCloud.com and finish any account setup or updated terms there. Review web access, then try signing in again.", 4);
        if (code == "TERMS_REQUIRED")
            return Result("Updated iCloud terms need attention",
                "Open iCloud.com with the same account, review and accept Apple's updated terms, then sign in here again.", 4);
        if (message.Contains("no trusted phone number", StringComparison.OrdinalIgnoreCase))
            return Result("A trusted phone number is needed",
                "Choose Use Apple-device verification to finish on Apple's sign-in page, or add a trusted phone number that can receive SMS in your Apple Account settings.", 1);
        if (code == "NETWORK")
            return Result("Could not finish connecting to iCloud",
                "Check your internet connection and try again. If iCloud.com works but this app still fails, review the setup guide and open the diagnostic-log folder. This error does not confirm that your password or account settings are wrong.", 4);
        if (code == "AUTH_REQUIRED")
            return Result("Apple rejected this sign-in attempt",
                "Check the Apple Account and regular password on iCloud.com. A verification session may also have expired; start a new sign-in if needed. Review the account setup before trying again.", 4);
        if (code == "2FA_REQUIRED" || stage == SignInStage.Verification)
            return Result("Verification did not finish",
                "For text verification, enter the six digits from the most recent SMS. To use a code from another Apple device, choose Use Apple-device verification and finish on Apple's page. Review the setup guide if verification still fails.", 1);
        if (stage == SignInStage.Delivery)
            return Result("Could not request a verification code",
                "Check that your trusted phone number can receive SMS, then try again. Review the setup guide if a text arrives but verification still fails.", 1);
        return Result("Sign-in did not finish",
            "The app could not determine the exact cause. Check the same account in iCloud.com, review the setup guide, and use the diagnostic log if the issue continues.", 4);
    }
}
