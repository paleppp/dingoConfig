using System.Text;
using System.Text.RegularExpressions;
using domain.Devices;
using domain.Firmware;
using domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace application.Services;

/// <summary>
/// Updates a dingoFW module's application over CAN through its OpenBLT bootloader, on the adapter
/// that is already connected: the app is sent into the bootloader (Bootloader, byte 6 = 1), then
/// XCP on base + 12 / + 13 erases, writes and reads back the .srec, and the module resets into
/// the new firmware. Ported from CoffeeDingoConfig; the bootloader has to be on the module once,
/// over SWD or USB-DFU, before this works.
/// </summary>
public sealed class CanFlashService(
    DeviceManager deviceManager,
    ICommsAdapterManager adapters,
    ILogger<CanFlashService> logger)
{
    public sealed record FlashResult(bool Ok, string Log, string? Version = null);
    public sealed record FlashStatus(bool Busy, int Percent, string Phase);

    /// <summary>Where the app lives behind the bootloader, and how much room it has.</summary>
    public sealed record AppRegion(uint Base, int MaxBytes);

    // F446 boards: sector 0 is the 16 KB bootloader, the app gets sectors 1-6.
    // CANBoard: the 8 KB bootloader, then the app up to the 2 KB config page at 0x0800F800.
    public static AppRegion RegionOf(FwDevice device) => device.Def.TypeName.Equals("CANBoard", StringComparison.OrdinalIgnoreCase)
        ? new AppRegion(0x08002000, 0x0800F800 - 0x08002000)
        : new AppRegion(0x08004000, 368 * 1024);

    private volatile int _percent;
    private volatile string _phase = "Idle";
    private volatile bool _busy;

    public FlashStatus Status => new(_busy, _percent, _phase);
    public event Action? StatusChanged;

    private void Set(string phase, int percent)
    {
        _phase = phase;
        _percent = percent;
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Why this image cannot go on this device, or null when it can. Checked before anything is
    /// sent, so a wrong file never puts a running module into its bootloader.
    /// </summary>
    public static string? Refusal(FwDevice device, SRecordImage image)
    {
        var region = RegionOf(device);
        if (image.MinAddress != region.Base)
            return $"The image starts at 0x{image.MinAddress:X8}, a {device.Def.TypeName} app behind the bootloader at 0x{region.Base:X8}. " +
                   "Use the .srec of a bootloader build for this board.";
        if (image.EndAddress > region.Base + region.MaxBytes)
            return $"The image is {image.TotalBytes / 1024} KB, more than the {region.MaxBytes / 1024} KB a {device.Def.TypeName} app has.";
        if (image.TotalBytes < 1024)
            return "The image is under 1 KB, not a firmware.";
        if (image.BoardMarker() is { } board && !BoardFits(board, device.Def.TypeName))
            return $"This image is for a {board}, not a {device.Def.TypeName}.";
        return null;
    }

    // "dingoPDM v7" fits a dingoPDM, "dingoPDM-Max v1" a dingoPDM-Max, "PT-DPDM4" a PT-DPDM
    private static bool BoardFits(string board, string typeName)
    {
        var name = Regex.Replace(board, @"\s*v?\d+$", "");
        return name.Equals(typeName, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<FlashResult> FlashAsync(Guid deviceId, SRecordImage image, CancellationToken ct = default)
    {
        var log = new StringBuilder();
        if (deviceManager.GetDevice(deviceId) is not FwDevice device)
            return new FlashResult(false, "The device is not in the project.");
        if (!adapters.IsConnected)
            return new FlashResult(false, "Connect the CAN adapter first.");
        if (Refusal(device, image) is { } refusal)
            return new FlashResult(false, refusal);
        if (_busy)
            return new FlashResult(false, "Another update is running.");

        var before = device.Version;
        log.AppendLine($"{image.TotalBytes} bytes in {image.Segments.Count} block(s) from 0x{image.MinAddress:X8}" +
                       (image.VersionMarker() is { } v ? $", firmware {v}" : "") + ".");

        _busy = true;
        Set("Entering bootloader", 2);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(6));
        var token = timeout.Token;

        try
        {
            // A module whose app is running is sent into the bootloader. One that is quiet may be
            // between sleeps and back in a moment; if it stays quiet it is most likely already
            // waiting in its bootloader after an update that did not finish.
            if (!device.Connected)
            {
                Set("Waiting for the module", 3);
                for (var i = 0; i < 40 && !device.Connected; i++)
                    await Task.Delay(250, token);
            }

            if (device.Connected)
            {
                deviceManager.RequestCanUpdate(deviceId);
                log.AppendLine("Sent the module into its bootloader.");
                await Task.Delay(700, token); // the app writes the hand-off and resets
            }
            else
            {
                log.AppendLine($"{device.Name} is quiet; looking for it in its bootloader.");
            }

            using var xcp = new XcpCanMaster(adapters, device.BaseId);

            Set("Connecting", 5);
            if (!await xcp.ConnectAsync(retries: 25, token))
                return Fail(log, $"No answer on 0x{xcp.RespId:X3}. Is the OpenBLT bootloader on this module " +
                                 "(flashed once over SWD or USB)? Is the bus quiet enough?");
            log.AppendLine($"Bootloader connected on 0x{xcp.CmdId:X3} / 0x{xcp.RespId:X3}.");
            await xcp.GetStatusAsync(token);
            if (!await xcp.ProgramStartAsync(token))
                return Fail(log, "The bootloader refused to start programming.");

            int total = image.TotalBytes, written = 0;
            foreach (var seg in image.Segments)
            {
                Set("Erasing", 8);
                if (!await xcp.ClearMemoryAsync(seg.Address, (uint)seg.Data.Length, token))
                    return Fail(log, $"Erasing at 0x{seg.Address:X8} failed.");
                var start = written;
                if (!await xcp.WriteDataAsync(seg.Address, seg.Data,
                        n => Set("Writing", 10 + (int)(55L * (start + n) / total)), token))
                    return Fail(log, $"Writing at 0x{seg.Address:X8} failed.");
                written += seg.Data.Length;
            }
            log.AppendLine($"Wrote {written} bytes.");

            // Before the read-back: this is what makes the bootloader write the vector block
            Set("Finishing", 68);
            if (!await xcp.ProgramDoneAsync(token))
                return Fail(log, "The bootloader refused to finish programming.");

            var verified = 0;
            foreach (var seg in image.Segments)
            {
                var start = verified;
                var read = await xcp.ReadDataAsync(seg.Address, seg.Data.Length,
                    n => Set("Verifying", 70 + (int)(25L * (start + n) / total)), token);
                if (read == null)
                    return Fail(log, $"Reading back at 0x{seg.Address:X8} failed. The module stays in its bootloader; try again.");
                if (!read.AsSpan().SequenceEqual(seg.Data))
                    return Fail(log, $"What was written at 0x{seg.Address:X8} does not match the file. " +
                                     "The module stays in its bootloader; try again.");
                verified += seg.Data.Length;
            }
            log.AppendLine("Read back and verified.");

            Set("Starting the new firmware", 96);
            await xcp.ProgramResetAsync(token);

            // The version only changes once the new app answers; the same version reflashed never does.
            // Up to 10 s: a module boots in well under one, the simulator restarts its emulation.
            var expected = image.VersionMarker() is { } marker ? $"v{marker}" : null;
            await Task.Delay(1000, token);
            string? version = null;
            for (var i = 0; i < 30 && version == null; i++)
            {
                if (i % 4 == 0) deviceManager.RequestVersion(deviceId);
                await Task.Delay(300, token);
                if (device.Version != "v0.0.0" && (device.Version == expected || (expected == null && device.Version != before)))
                    version = device.Version;
            }

            Set("Done", 100);
            log.AppendLine(version != null
                ? $"Done, {device.Name} reports {version}."
                : $"Done. {device.Name} has not reported {expected ?? "a new version"} yet; it still showed {device.Version}.");
            version ??= device.Version;
            logger.LogInformation("CAN update of {Name} done, {Bytes} bytes, {Version}", device.Name, written, version);
            return new FlashResult(true, log.ToString(), version);
        }
        catch (OperationCanceledException)
        {
            return Fail(log, "Timed out or cancelled. If the module stays quiet, it is waiting in its bootloader: run the update again.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CAN update of {Name} failed", device.Name);
            return Fail(log, ex.Message);
        }
        finally
        {
            _busy = false;
            StatusChanged?.Invoke();
        }
    }

    private FlashResult Fail(StringBuilder log, string message)
    {
        Set("Failed", _percent);
        log.AppendLine(message);
        return new FlashResult(false, log.ToString());
    }
}
