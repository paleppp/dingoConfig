using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace web.Services;

/// <summary>
/// Whether the page is shown in its phone layout. Decided once per page load from the browser's
/// user agent, or from the dc-view cookie when the user has picked a view themselves.
/// </summary>
public sealed partial record UiMode(bool IsMobile)
{
    public const string CookieName = "dc-view";

    public static bool Detect(HttpContext? context)
    {
        if (context == null) return false;

        var chosen = context.Request.Cookies[CookieName];
        if (chosen == "mobile") return true;
        if (chosen == "desktop") return false;

        var agent = context.Request.Headers.UserAgent.ToString();
        return PhoneAgent().IsMatch(agent);
    }

    // Phones and small tablets; "Mobi" covers most phone browsers, Android tablets without it too
    [GeneratedRegex("Mobi|Android|iPhone|iPod|Windows Phone", RegexOptions.IgnoreCase)]
    private static partial Regex PhoneAgent();
}
