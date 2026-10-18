using Reminders.Windows.Services;
using Reminders.Windows;

internal static class AppleSignInPolicyChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(AppleSignInPolicy.IsPopupUri("about:blank"), "blank popup preserves the opener while Apple prepares navigation");
        check(AppleSignInPolicy.IsPopupUri("https://idmsa.apple.com/appleauth/auth/authorize/signin"), "Apple authentication popups are permitted");
        foreach (var address in new[] { "http://www.icloud.com/", "https://apple.com.example.com/", "https://user@www.icloud.com/", "file:///C:/test", "javascript:alert(1)", "about:config" })
            check(!AppleSignInPolicy.IsPopupUri(address), "popup rejects unsafe destination " + address);
        var auth = new Uri("https://idmsa.apple.com/appleauth/auth/signin/complete?account=private@example.com&token=private-token");
        foreach (var status in new[] { 200, 204, 400, 401, 403, 409 })
            check(AppleSignInPolicy.HttpFailure(auth, status) is null, "authentication status is left to Apple's page " + status);
        var failure = AppleSignInPolicy.HttpFailure(auth, 503)!;
        check(failure.Message.Contains("sign-in service returned HTTP 503"), "authentication server failure is distinct from iCloud account transfer");
        check(failure.Diagnostic == "apple sign-in: stage=authentication HTTP 503", "HTTP diagnostics exclude URL and account parameters");
        check(AppleSignInPolicy.HttpFailure(new Uri("https://setup.icloud.com/setup/ws/1/accountLogin"), 500)!.Message.Contains("iCloud account"), "account server failure identifies the right stage");
        check(AppleSignInPolicy.HttpFailure(auth, 429)!.Message.Contains("Wait a little"), "rate limiting does not encourage immediate repeated sign-in");
        check(AppleSignInPolicy.HttpFailure(new Uri("https://www.apple.com/legal/privacy/"), 503) is null, "unrelated page failure is not labelled authentication failure");
        check(AppleSignInPolicy.AccountEndpoint(new Uri("https://setup.icloud.com/setup/ws/1/validate?secret=hidden")) == "validate", "validation endpoint is recognized without logging query strings");
        check(AppleSignInPolicy.AccountEndpoint(new Uri("https://setup.icloud.com/setup/ws/1/private-user-id")) == "other", "unknown endpoint names are not copied into diagnostics");
        foreach (var error in new Exception[] { new SyncException("NETWORK", "private-token", "private@example.com"), new IOException("private-token"), new SyncException("private-token", "private@example.com") })
        {
            var result = AppleSignInPolicy.ImportFailure(error);
            check(!result.Message.Contains("private") && !result.Diagnostic.Contains("private"), "import diagnostics and UI omit arbitrary exception contents");
        }
        check(AppleSignInPolicy.ImportFailure(new SyncException("NETWORK", "hidden")).Message.Contains("finished verification"), "post-verification network failure is not reported as a missing device prompt");
    }
}
