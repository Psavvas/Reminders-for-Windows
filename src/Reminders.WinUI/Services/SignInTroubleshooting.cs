using Reminders.Core;
using System.Text.RegularExpressions;

namespace Reminders.Windows.Services;

internal enum SignInStage { Password, Delivery, Verification }
internal sealed record SignInFailure(string Title, string Reason, string NextStep, int GuideStep);

internal static class SignInTroubleshooting
{
    public static SignInFailure Explain(string? code, string message, string? detail, SignInStage stage,
        Func<PresentationMessage, string> format, string? presentationKey = null)
    {
        // Show only an HTTP status from diagnostic detail, never raw response
        // bodies or anything that could contain account data or session tokens.
        var http = Regex.Match(detail ?? "", @"\bHTTP [1-5][0-9]{2}\b").Value;
        var reason = string.IsNullOrWhiteSpace(message) ? format(new("SignInReasonUnknown")) : message;
        if (http.Length > 0 && !reason.Contains(http, StringComparison.Ordinal)) reason = format(new("SignInReasonWithHttpStatus", reason, http));

        SignInFailure Result(string title, string next, int step) => new(format(new(title)), reason, format(new(next)), step);
        if (presentationKey == "ErrorVerificationAccepted")
            return Result("SignInCodeAcceptedTitle",
                "SignInCodeAcceptedNext", 4);
        if (code == "TERMS_REQUIRED")
            return Result("SignInTermsTitle",
                "SignInTermsNext", 4);
        if (presentationKey == "ErrorTrustedPhoneMissing")
            return Result("SignInMissingPhoneTitle",
                "SignInMissingPhoneNext", 1);
        if (code == "NETWORK")
            return Result("SignInNetworkTitle",
                "SignInNetworkNext", 4);
        if (code == "AUTH_REQUIRED")
            return Result("SignInRejectedTitle",
                "SignInRejectedNext", 4);
        if (code == "2FA_REQUIRED" || stage == SignInStage.Verification)
            return Result("SignInVerificationTitle",
                "SignInVerificationNext", 1);
        if (stage == SignInStage.Delivery)
            return Result("SignInDeliveryTitle",
                "SignInDeliveryNext", 1);
        return Result("SignInUnknownTitle",
            "SignInUnknownNext", 4);
    }
}
