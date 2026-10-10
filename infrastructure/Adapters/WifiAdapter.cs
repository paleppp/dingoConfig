using System.Net.Sockets;
using System.Text;
using domain.Enums;
using domain.Interfaces;
using domain.Models;

namespace infrastructure.Adapters;

/// <summary>
/// SLCAN over TCP, for a WiFi-CAN bridge such as the ESP32 one in dingo-tools.
/// The port is the bridge's address, "host" or "host:port" (3333 by default).
/// </summary>
public class WifiAdapter : ICommsAdapter
{
    public string Name => "WiFi";

    public const int DefaultTcpPort = 3333;
    private const int ConnectTimeoutMs = 3000;
    private const int InterFrameDelayMs = 2;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly Lock _writeLock = new();
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private string _bitrateCode = "6";
    private volatile bool _running;

    public bool IsConnected => _running && (_client?.Connected ?? false);

    public event DataReceivedHandler? DataReceived;
    public event EventHandler? Disconnected;

    public async Task<bool> InitAsync(string port, CanBitRate bitRate, CancellationToken ct)
    {
        if (!TryParseAddress(port, out var host, out var tcpPort))
        {
            Console.WriteLine($"{Name}: '{port}' is not an address, use host or host:port");
            return false;
        }

        try
        {
            _client = new TcpClient { NoDelay = true };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeoutMs);
            await _client.ConnectAsync(host, tcpPort, timeout.Token);

            // A WiFi link that drops leaves the socket open; keepalive notices within seconds
            var socket = _client.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 2);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);

            _stream = _client.GetStream();
        }
        catch (Exception e)
        {
            Console.WriteLine($"{Name}: cannot connect to {host}:{tcpPort}: {e.Message}");
            Close();
            return false;
        }

        _bitrateCode = ToSlcanBitrate(bitRate);
        return true;
    }

    public Task<bool> StartAsync(CancellationToken ct)
    {
        if (_stream == null) return Task.FromResult(false);

        try
        {
            // Close, set the bitrate, open
            WriteText($"C\rS{_bitrateCode}\rO\r");
        }
        catch (Exception e)
        {
            Console.WriteLine($"{Name}: {e.Message}");
            return Task.FromResult(false);
        }

        _running = true;
        _readCts = new CancellationTokenSource();
        var stream = _stream;
        var token = _readCts.Token;
        _readTask = Task.Run(() => ReadLoop(stream, token), CancellationToken.None);
        return Task.FromResult(true);
    }

    public Task<bool> StopAsync()
    {
        var wasRunning = _running;
        _running = false;

        if (wasRunning)
        {
            try
            {
                WriteText("C\r");
            }
            catch
            {
                // The bridge closes the bus by itself when the connection goes
            }
        }

        _readCts?.Cancel();
        Close();
        try
        {
            _readTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignored
        }

        _readCts?.Dispose();
        _readCts = null;
        _readTask = null;
        return Task.FromResult(true);
    }

    public Task<bool> WriteAsync(CanFrame frame, CancellationToken ct)
    {
        if (!_running || _stream == null) return Task.FromResult(false);

        var buffer = new byte[SlcanCodec.MaxFrameBytes];
        try
        {
            var len = SlcanCodec.Encode(frame, buffer, 0);
            lock (_writeLock) _stream.Write(buffer, 0, len);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            HandleDisconnection();
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public Task<bool> WriteBatchAsync(IReadOnlyList<CanFrame> frames, CancellationToken ct)
    {
        if (!_running || _stream == null) return Task.FromResult(false);

        var buffer = new byte[SlcanCodec.MaxFrameBytes];
        try
        {
            for (var i = 0; i < frames.Count; i++)
            {
                if (ct.IsCancellationRequested) return Task.FromResult(false);

                var len = SlcanCodec.Encode(frames[i], buffer, 0);
                lock (_writeLock) _stream.Write(buffer, 0, len);

                // Same pace as the serial SLCAN adapter: the PDM takes the frames one by one
                if (i < frames.Count - 1)
                    Thread.Sleep(InterFrameDelayMs);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            HandleDisconnection();
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    /// <summary>"host" or "host:port"; the port defaults to 3333</summary>
    public static bool TryParseAddress(string address, out string host, out int port)
    {
        host = address.Trim();
        port = DefaultTcpPort;
        if (host.Length == 0) return false;

        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon)
        {
            if (!int.TryParse(host[(colon + 1)..], out port) || port < 1 || port > 65535) return false;
            host = host[..colon];
        }

        return host.Length > 0 && !host.Contains(' ');
    }

    private void ReadLoop(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var line = new StringBuilder(32);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = stream.Read(buffer, 0, buffer.Length);
                if (n == 0) break; // the bridge closed the connection

                for (var i = 0; i < n; i++)
                {
                    var c = (char)buffer[i];
                    if (c == '\r')
                    {
                        if (line.Length > 0 && SlcanCodec.TryDecode(line.ToString(), out var frame))
                            DataReceived?.Invoke(this, new CanFrameEventArgs(frame));
                        line.Clear();
                    }
                    else if (c == '\a' || c == '\n')
                    {
                        line.Clear(); // a refused command, or a line ending we do not use
                    }
                    else if (line.Length < 64)
                    {
                        line.Append(c);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            // The link dropped, or StopAsync closed the socket
        }

        if (_running) HandleDisconnection();
    }

    private void WriteText(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        lock (_writeLock) _stream!.Write(bytes, 0, bytes.Length);
    }

    private void HandleDisconnection()
    {
        if (!_running) return;
        _running = false;

        //Note: Disconnecting is handled by the CommsAdapterManager
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void Close()
    {
        try
        {
            _stream?.Dispose();
            _client?.Dispose();
        }
        catch
        {
            // ignored
        }

        _stream = null;
        _client = null;
    }

    private static string ToSlcanBitrate(CanBitRate canBitRate) => canBitRate switch
    {
        CanBitRate.BitRate100K => "3",
        CanBitRate.BitRate125K => "4",
        CanBitRate.BitRate250K => "5",
        CanBitRate.BitRate500K => "6",
        CanBitRate.BitRate1000K => "8",
        _ => "6"
    };
}
