using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace infrastructure.Adapters;

/// <summary>A WiFi-CAN bridge that answered on the local network</summary>
public record WifiBridge(string Name, string Address, int Port, string Firmware, bool Busy)
{
    public string Endpoint => $"{Address}:{Port}";
}

/// <summary>
/// Finds WiFi-CAN bridges on the networks this computer is on, so nobody has to type an address.
/// A broadcast on UDP 3334 asks them; they answer with their name and TCP port, and also
/// announce themselves there once a second for when the broadcast cannot be sent.
/// </summary>
public static class WifiDiscovery
{
    public const int DiscoveryPort = 3334;
    private static readonly byte[] Request = Encoding.ASCII.GetBytes("DINGOCAN-DISCOVER");

    public static async Task<IReadOnlyList<WifiBridge>> FindAsync(TimeSpan listenFor, CancellationToken ct = default)
    {
        var found = new ConcurrentDictionary<string, WifiBridge>();
        var sockets = new List<UdpClient>();

        // One question per network, sent from this computer's address on it, so it leaves
        // through the right adapter
        foreach (var (local, broadcast) in Ipv4Networks())
        {
            var udp = TryOpen(new IPEndPoint(local, 0));
            if (udp == null) continue;
            sockets.Add(udp);
            await TrySend(udp, broadcast, ct);
        }

        // And one to every network, for systems that do not list theirs (Android)
        var any = TryOpen(new IPEndPoint(IPAddress.Any, 0));
        if (any != null)
        {
            sockets.Add(any);
            await TrySend(any, IPAddress.Broadcast, ct);
        }

        // The bridges' own announcements; another program may hold the port, then the answers do
        var listener = TryOpen(new IPEndPoint(IPAddress.Any, DiscoveryPort), reuse: true);
        if (listener != null) sockets.Add(listener);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(listenFor);
        await Task.WhenAll(sockets.Select(s => ReceiveAsync(s, found, timeout.Token)));

        foreach (var s in sockets) s.Dispose();
        return found.Values.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Address).ToList();
    }

    /// <summary>A bridge's answer or announcement: {"dingocan":1,"name":..,"port":..,"fw":..,"busy":..}</summary>
    public static bool TryParse(byte[] data, IPAddress from, out WifiBridge bridge)
    {
        bridge = null!;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (!root.TryGetProperty("dingocan", out var tag) || tag.ValueKind != JsonValueKind.Number) return false;

            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var port = root.TryGetProperty("port", out var p) && p.TryGetInt32(out var value) ? value : WifiAdapter.DefaultTcpPort;
            var firmware = root.TryGetProperty("fw", out var f) ? f.GetString() ?? "" : "";
            var busy = root.TryGetProperty("busy", out var b) && b.ValueKind == JsonValueKind.True;
            if (port is < 1 or > 65535) return false;

            var address = from.IsIPv4MappedToIPv6 ? from.MapToIPv4() : from;
            bridge = new WifiBridge(name.Length > 0 ? name : address.ToString(), address.ToString(), port, firmware, busy);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task ReceiveAsync(UdpClient udp, ConcurrentDictionary<string, WifiBridge> found, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(ct);
                if (TryParse(result.Buffer, result.RemoteEndPoint.Address, out var bridge))
                    found[bridge.Address] = bridge;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Time is up, or this network went away
        }
    }

    private static UdpClient? TryOpen(IPEndPoint local, bool reuse = false)
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            if (reuse) udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(local);
            return udp;
        }
        catch (SocketException)
        {
            udp.Dispose();
            return null;
        }
    }

    private static async Task TrySend(UdpClient udp, IPAddress to, CancellationToken ct)
    {
        try
        {
            await udp.SendAsync(Request, new IPEndPoint(to, DiscoveryPort), ct);
        }
        catch (SocketException)
        {
            // No route on this network; the others may still answer
        }
    }

    // This computer's IPv4 address and the broadcast address of each network it is on
    private static List<(IPAddress Local, IPAddress Broadcast)> Ipv4Networks()
    {
        var networks = new List<(IPAddress, IPAddress)>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return networks;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            UnicastIPAddressInformationCollection addresses;
            try
            {
                addresses = nic.GetIPProperties().UnicastAddresses;
            }
            catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
            {
                continue;
            }

            foreach (var address in addresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask == null) continue;
                var ip = address.Address.GetAddressBytes();
                var mask = address.IPv4Mask.GetAddressBytes();
                if (mask.All(b => b == 0)) continue;
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                networks.Add((address.Address, new IPAddress(broadcast)));
            }
        }

        return networks;
    }
}
