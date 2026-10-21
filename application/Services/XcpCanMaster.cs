using domain.Interfaces;
using domain.Models;

namespace application.Services;

/// <summary>
/// XCP-over-CAN master for the OpenBLT bootloader, on the connected adapter. The command set
/// and order follow LibOpenBLT's xcploader.c; one XCP packet is one classic CAN frame. The
/// bootloader listens on the module's base ID + 12 and answers on base + 13, the IDs the
/// firmware hands it (bootloader can.c DingoLoadCanConfig). One command at a time, each checked
/// for the positive response, so a lost or garbled frame stops the flash instead of writing
/// garbage. Ported from CoffeeDingoConfig.
/// </summary>
public sealed class XcpCanMaster(ICommsAdapterManager adapters, int baseId) : IDisposable
{
    private const byte CmdConnect = 0xFF, CmdDisconnect = 0xFE, CmdGetStatus = 0xFD;
    private const byte CmdSetMta = 0xF6, CmdUpload = 0xF5;
    private const byte CmdProgramStart = 0xD2, CmdProgramClear = 0xD1, CmdProgram = 0xD0;
    private const byte CmdProgramReset = 0xCF, CmdProgramMax = 0xC9;
    private const byte PidRes = 0xFF; // positive response

    private readonly object _gate = new();
    private TaskCompletionSource<byte[]>? _pending;
    private bool _subscribed;

    public int CmdId { get; } = baseId + 12;
    public int RespId { get; } = baseId + 13;
    public bool Intel { get; private set; } = true;
    public int MaxCto { get; private set; } = 8;
    public int MaxDto { get; private set; } = 8;
    public int MaxProgCto { get; private set; } = 8;

    public void Dispose()
    {
        if (_subscribed) adapters.DataReceived -= OnFrame;
        _subscribed = false;
    }

    private void OnFrame(object? sender, CanFrameEventArgs e)
    {
        if (e.Frame.Id != RespId) return;
        TaskCompletionSource<byte[]>? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }
        if (pending == null) return; // a late or unasked-for frame

        var n = Math.Min(e.Frame.Len, e.Frame.Payload.Length);
        var resp = new byte[n];
        Array.Copy(e.Frame.Payload, resp, n);
        pending.TrySetResult(resp);
    }

    /// <summary>Sends one command packet and waits for its response; null on timeout or send failure.</summary>
    private async Task<byte[]?> SendAsync(byte[] packet, int len, int timeoutMs, CancellationToken ct)
    {
        if (!_subscribed)
        {
            adapters.DataReceived += OnFrame;
            _subscribed = true;
        }

        var adapter = adapters.ActiveAdapter;
        if (adapter == null) return null;

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _pending = tcs;

        var payload = new byte[8];
        Array.Copy(packet, payload, len);
        if (!await adapter.WriteAsync(new CanFrame(CmdId, len, payload), ct).ConfigureAwait(false))
        {
            lock (_gate) if (_pending == tcs) _pending = null;
            return null;
        }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
        if (done != tcs.Task)
        {
            lock (_gate) if (_pending == tcs) _pending = null;
            return null;
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    private static bool Ok(byte[]? r) => r is { Length: >= 1 } && r[0] == PidRes;

    private void PutLong(uint value, byte[] buf, int off)
    {
        if (Intel)
        {
            buf[off] = (byte)value; buf[off + 1] = (byte)(value >> 8);
            buf[off + 2] = (byte)(value >> 16); buf[off + 3] = (byte)(value >> 24);
        }
        else
        {
            buf[off] = (byte)(value >> 24); buf[off + 1] = (byte)(value >> 16);
            buf[off + 2] = (byte)(value >> 8); buf[off + 3] = (byte)value;
        }
    }

    /// <summary>CONNECT, retried while the bootloader comes up after the reset.</summary>
    public async Task<bool> ConnectAsync(int retries, CancellationToken ct)
    {
        for (var i = 0; i < retries && !ct.IsCancellationRequested; i++)
        {
            var r = await SendAsync([CmdConnect, 0x00], 2, 1500, ct).ConfigureAwait(false);
            if (r is { Length: >= 8 } && r[0] == PidRes)
            {
                Intel = (r[2] & 0x01) == 0;
                MaxCto = r[3] == 0 ? 8 : r[3];
                MaxProgCto = MaxCto;
                MaxDto = Intel ? r[4] | (r[5] << 8) : r[5] | (r[4] << 8);
                if (MaxDto == 0) MaxDto = 8;
                return true;
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }

    public async Task<bool> GetStatusAsync(CancellationToken ct) =>
        Ok(await SendAsync([CmdGetStatus], 1, 1000, ct).ConfigureAwait(false));

    public async Task<bool> ProgramStartAsync(CancellationToken ct)
    {
        var r = await SendAsync([CmdProgramStart], 1, 3000, ct).ConfigureAwait(false);
        if (!Ok(r)) return false;
        if (r!.Length >= 4 && r[3] != 0) MaxProgCto = Math.Min((int)r[3], 8);
        return true;
    }

    private async Task<bool> SetMtaAsync(uint address, CancellationToken ct)
    {
        var p = new byte[8];
        p[0] = CmdSetMta;
        PutLong(address, p, 4);
        return Ok(await SendAsync(p, 8, 1000, ct).ConfigureAwait(false));
    }

    /// <summary>Erases a range (SET_MTA, PROGRAM_CLEAR); a large erase takes seconds.</summary>
    public async Task<bool> ClearMemoryAsync(uint address, uint length, CancellationToken ct)
    {
        if (!await SetMtaAsync(address, ct).ConfigureAwait(false)) return false;
        var p = new byte[8];
        p[0] = CmdProgramClear;
        PutLong(length, p, 4);
        return Ok(await SendAsync(p, 8, 20000, ct).ConfigureAwait(false));
    }

    /// <summary>Programs one contiguous block: SET_MTA, PROGRAM_MAX packets, a PROGRAM for the rest.</summary>
    public async Task<bool> WriteDataAsync(uint address, byte[] data, Action<int>? onBytes, CancellationToken ct)
    {
        if (!await SetMtaAsync(address, ct).ConfigureAwait(false)) return false;
        var chunk = Math.Max(1, MaxProgCto - 1);
        var offset = 0;
        while (offset < data.Length)
        {
            var n = Math.Min(chunk, data.Length - offset);
            var p = new byte[8];
            bool ok;
            if (n == chunk)
            {
                p[0] = CmdProgramMax;
                Array.Copy(data, offset, p, 1, n);
                ok = Ok(await SendAsync(p, n + 1, 5000, ct).ConfigureAwait(false));
            }
            else
            {
                p[0] = CmdProgram;
                p[1] = (byte)n;
                Array.Copy(data, offset, p, 2, n);
                ok = Ok(await SendAsync(p, n + 2, 5000, ct).ConfigureAwait(false));
            }
            if (!ok) return false;
            offset += n;
            onBytes?.Invoke(offset);
        }
        return true;
    }

    /// <summary>Reads a range back (SET_MTA, UPLOAD) to verify what was programmed.</summary>
    public async Task<byte[]?> ReadDataAsync(uint address, int length, Action<int>? onBytes, CancellationToken ct)
    {
        if (!await SetMtaAsync(address, ct).ConfigureAwait(false)) return null;
        var result = new byte[length];
        var chunk = Math.Max(1, MaxDto - 1);
        var offset = 0;
        while (offset < length)
        {
            var n = Math.Min(chunk, length - offset);
            var r = await SendAsync([CmdUpload, (byte)n], 2, 2000, ct).ConfigureAwait(false);
            if (!Ok(r) || r!.Length < n + 1) return null;
            Array.Copy(r, 1, result, offset, n);
            offset += n;
            onBytes?.Invoke(offset);
        }
        return result;
    }

    /// <summary>
    /// Ends programming: PROGRAM with size 0 makes the bootloader write what it buffered, the vector
    /// block last, so an interrupted update leaves no startable app behind.
    /// </summary>
    public async Task<bool> ProgramDoneAsync(CancellationToken ct) =>
        Ok(await SendAsync([CmdProgram, 0x00], 2, 5000, ct).ConfigureAwait(false));

    /// <summary>Resets into the new app; the module may reset before it answers.</summary>
    public async Task ProgramResetAsync(CancellationToken ct) =>
        await SendAsync([CmdProgramReset], 1, 1000, ct).ConfigureAwait(false);

    /// <summary>Leaves the bootloader running without starting the app.</summary>
    public async Task DisconnectAsync(CancellationToken ct) =>
        await SendAsync([CmdDisconnect], 1, 1000, ct).ConfigureAwait(false);
}
