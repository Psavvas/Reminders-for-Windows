using Reminders.Core;
using System.Text.Json.Nodes;
using System.Diagnostics.CodeAnalysis;

namespace Reminders.Windows.Services;

internal sealed record AppleSignInFailure(PresentationMessage Message, string Diagnostic);

internal static class AppleSignInPolicy
{
    public const string StartUrl = "https://www.icloud.com/reminders/";

    public static bool IsRemindersResponse(Uri uri, int status)
    {
        var path = uri.AbsolutePath;
        return status == 200 && IsAppleUri(uri) &&
            (uri.Host == "ckdatabasews.icloud.com" || uri.Host.EndsWith("-ckdatabasews.icloud.com", StringComparison.OrdinalIgnoreCase)) &&
            path.StartsWith("/database/1/com.apple.reminders/production/private/", StringComparison.Ordinal) &&
            (path.EndsWith("/records/query", StringComparison.Ordinal) || path.EndsWith("/changes/zone", StringComparison.Ordinal));
    }

    public static bool CanImportAccount([NotNullWhen(true)] JsonNode? response)
    {
        if (response is not JsonObject account || account["dsInfo"] is not JsonObject identity || account["webservices"] is not JsonObject { Count: > 0 }) return false;
        if (account["hsaChallengeRequired"] is { } challenge && (challenge is not JsonValue flag || !flag.TryGetValue<bool>(out var required) || required)) return false;
        static bool HasText(JsonNode? value) => value is JsonValue text && text.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result);
        // hsaTrustedBrowser controls remembered trust. A verified session can
        // legitimately be usable without that optional browser preference.
        return HasText(identity["appleId"]) || HasText(identity["primaryEmail"]);
    }

    public static bool IsAppleUri(Uri uri) => uri.Scheme == "https" && uri.UserInfo.Length == 0 &&
        (uri.Host == "icloud.com" || uri.Host.EndsWith(".icloud.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host == "apple.com" || uri.Host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase));

    // window.open() can create a blank child before navigating it. Keep that
    // child attached to its opener; all subsequent navigations are checked.
    public static bool IsPopupUri(string address) => address == "about:blank" ||
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && IsAppleUri(uri);

    public static string? Stage(Uri uri)
    {
        if (!IsAppleUri(uri)) return null;
        if (uri.Host == "idmsa.apple.com" && uri.AbsolutePath.StartsWith("/appleauth/auth", StringComparison.Ordinal)) return "authentication";
        if (uri.Host == "setup.icloud.com" && uri.AbsolutePath.StartsWith("/setup/ws/1/", StringComparison.Ordinal)) return "account";
        return null;
    }

    public static string AccountEndpoint(Uri uri) => uri.AbsolutePath switch
    {
        "/setup/ws/1/accountLogin" => "accountLogin",
        "/setup/ws/1/validate" => "validate",
        "/setup/ws/1/loginOrCreateAccount" => "loginOrCreateAccount",
        _ => "other"
    };

    public static AppleSignInFailure? HttpFailure(Uri uri, int status)
    {
        var stage = Stage(uri);
        // Apple's normal authentication exchange uses 401 and 409 for
        // challenges. Neither means that a device prompt failed to arrive.
        if (stage is null || status is not (>= 500 and <= 599) and not 429) return null;
        var message = status == 429
            ? new PresentationMessage("AppleSignInRateLimited")
            : new PresentationMessage(stage == "authentication" ? "AppleSignInServiceUnavailable" : "AppleAccountServiceUnavailable", status);
        return new(message, $"apple sign-in: stage={stage} HTTP {status}");
    }

    public static AppleSignInFailure ImportFailure(Exception exception)
    {
        var code = exception is SyncException sync && sync.Code is "NETWORK" or "AUTH_REQUIRED" or "2FA_REQUIRED" or "TERMS_REQUIRED" ? sync.Code : "ERROR";
        var message = code switch
        {
            "NETWORK" => "AppleSignInImportNetwork",
            "TERMS_REQUIRED" => "AppleSignInImportTerms",
            "2FA_REQUIRED" => "AppleSignInImportPending",
            "AUTH_REQUIRED" => "AppleSignInImportRejected",
            _ => "AppleSignInImportFailed"
        };
        // No exception messages, URLs, headers, bodies, or account identifiers.
        return new(new(message), $"apple sign-in: stage=import code={code} type={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
    }
}
