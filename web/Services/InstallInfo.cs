using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace web.Services;

/// <summary>
/// What the Install app page needs to know about how this server is reached.
///
/// Browsers install a web app in its own window only from HTTPS (or localhost). A host that
/// runs dingoConfig for phones on its network, like the YY3588 dash, gives it an HTTPS endpoint
/// with a certificate from its own certificate authority, and names that authority's
/// certificate in the CaCertificate setting so the page can hand it out.
/// </summary>
public static class InstallInfo
{
    public const string CaCertificatePath = "/ca.crt";

    /// <summary>The CaCertificate file, when it is set and exists.</summary>
    public static string? CaCertificateFile(IConfiguration config)
    {
        var path = config["CaCertificate"];
        return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
    }

    /// <summary>The port of the server's HTTPS endpoint, if it has one.</summary>
    public static int? HttpsPort(IServer server)
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        foreach (var address in addresses)
        {
            if (!address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            // Kestrel lists wildcard bindings as https://[::]:5001 or https://0.0.0.0:5001
            var colon = address.LastIndexOf(':');
            if (colon > 0 && int.TryParse(address[(colon + 1)..].TrimEnd('/'), out var port))
                return port;
        }
        return null;
    }
}
