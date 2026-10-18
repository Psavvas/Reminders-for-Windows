namespace Reminders.Windows.Services;

internal sealed record AppleSignInFailure(string Message, string Diagnostic);

internal static class AppleSignInPolicy
{
    public const string StartUrl = "https://www.icloud.com/reminders/";
    public const string Instructions = "Sign in on Apple's page below. Apple will offer the verification methods available for your account. Choose Trust when offered to remember this PC.";

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
        var service = stage == "authentication" ? "sign-in" : "iCloud account";
        var message = status == 429
            ? "Apple is limiting sign-in requests (HTTP 429). Wait a little, then reload Apple sign-in."
            : $"Apple's {service} service returned HTTP {status}. Reload Apple sign-in to try again, or cancel and use text verification.";
        return new(message, $"apple sign-in: stage={stage} HTTP {status}");
    }

    public static AppleSignInFailure ImportFailure(Exception exception)
    {
        var code = exception is SyncException sync && sync.Code is "NETWORK" or "AUTH_REQUIRED" or "2FA_REQUIRED" or "TERMS_REQUIRED" ? sync.Code : "ERROR";
        var message = code switch
        {
            "NETWORK" => "Apple's page finished verification, but the app could not connect to iCloud to save the session. Reload Apple sign-in and try again, or cancel and use text verification.",
            "TERMS_REQUIRED" => "Apple requires updated iCloud terms. Finish those on Apple's page, then try again.",
            "2FA_REQUIRED" => "Apple has not finished verifying this session. Finish verification and choose Trust on Apple's page.",
            "AUTH_REQUIRED" => "iCloud did not accept the session returned by Apple's page. Reload Apple sign-in and try again.",
            _ => "The app could not read or save Apple's sign-in session. Reload Apple sign-in to try again, or cancel and use text verification."
        };
        // No exception messages, URLs, headers, bodies, or account identifiers.
        return new(message, $"apple sign-in: stage=import code={code} type={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
    }
}
