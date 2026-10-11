using System.Globalization;
using domain.Devices;
using domain.Devices.Functions;
using domain.Enums;
using domain.Interfaces;

namespace web.Components.Mobile;

/// <summary>A block as a row in the phone's settings list</summary>
public sealed record MobileItem(int Number, string Name, bool Enabled, bool Active, string Value, string? Owner);

/// <summary>A group of blocks; Single sections (the device, the wiper) open straight into their form</summary>
public sealed record MobileSection(string Key, string Title, IReadOnlyList<MobileItem> Items, bool Single = false, string? Summary = null);

/// <summary>A block's form: its title, what set it up if not the user, and its fields</summary>
public sealed record BlockForm(string Title, string? Owner, IReadOnlyList<FormField> Fields);

/// <summary>
/// The phone's view of a device's settings: the same blocks as the Configuration tab's grids,
/// as lists and forms. The keys match the Configuration tab's cfg-* sections.
/// </summary>
public static class MobileBlocks
{
    public static IReadOnlyList<MobileSection> Sections(FwDevice d)
    {
        var list = new List<MobileSection>
        {
            new("device", "Device", [], Single: true,
                Summary: $"{d.Def.TypeName} · base ID {Hex(d.BaseId)} · {BitRateText(d.BitRate)}" +
                         (d.Def.CanSleep ? (d.SleepEnabled ? " · sleep on" : " · sleep off") : ""))
        };

        Add(list, "outputs", "Outputs", d.Outputs, o => new MobileItem(o.Number, o.Name, o.Enabled,
            o.State == OutState.On, OutputValue(o), d.ManagedBy(o)));
        Add(list, "digital-inputs", "Digital inputs", d.DigitalInputs, i => new MobileItem(i.Number, i.Name, i.Enabled,
            i.State, OnOff(i.State), Owner(d, i)));
        Add(list, "analog-inputs", "Analog inputs", d.AnalogInputs, i => new MobileItem(i.Number, i.Name, i.Enabled,
            i.Switch.State, $"{i.Millivolts:F0} mV", null));
        Add(list, "digital-outputs", "Digital outputs", d.DigitalOutputs, o => new MobileItem(o.Number, o.Name, o.Enabled,
            o.State, OnOff(o.State), null));
        Add(list, "can-inputs", "CAN inputs", d.CanInputs, i => new MobileItem(i.Number, i.Name, i.Enabled,
            i.Output, $"{Hex(i.Id)} · {i.Value}", Owner(d, i)));
        Add(list, "can-outputs", "CAN outputs", d.CanOutputs, o => new MobileItem(o.Number, o.Name, o.Enabled,
            false, Hex(o.Id), null));
        Add(list, "virtual-inputs", "Virtual inputs", d.VirtualInputs, i => new MobileItem(i.Number, i.Name, i.Enabled,
            i.Value, OnOff(i.Value), Owner(d, i)));
        Add(list, "conditions", "Conditions", d.Conditions, c => new MobileItem(c.Number, c.Name, c.Enabled,
            c.Value > 0, c.Value.ToString(), Owner(d, c)));
        Add(list, "counters", "Counters", d.Counters, c => new MobileItem(c.Number, c.Name, c.Enabled,
            c.Value > 0, c.Value.ToString(), Owner(d, c)));
        Add(list, "flashers", "Flashers", d.Flashers, f => new MobileItem(f.Number, f.Name, f.Enabled,
            f.Value, OnOff(f.Value), Owner(d, f)));
        Add(list, "timers", "Timers", d.Timers, t => new MobileItem(t.Number, t.Name, t.Enabled,
            false, $"{Seconds(t.Time)} {TimerModeText(t.Mode).ToLowerInvariant()}", Owner(d, t)));
        Add(list, "can-messages", "CAN messages", d.CanMessages, m => new MobileItem(m.Number, m.Name, m.Enabled,
            false, Hex(m.Id), Owner(d, m)));

        if (d.Def.HasWipers)
            list.Add(new MobileSection("wiper", "Wiper", [], Single: true,
                Summary: (d.Wipers.Enabled ? "enabled" : "disabled") + (Owner(d, d.Wipers) is { } w ? $" · {w}" : "")));
        if (d.Def.HasStarterDisable)
            list.Add(new MobileSection("starter-disable", "Starter disable", [], Single: true,
                Summary: d.StarterDisable.Enabled ? "enabled" : "disabled"));

        return list;
    }

    /// <summary>The form of one block, null when there is no such block</summary>
    public static BlockForm? Form(FwDevice d, string section, int number)
    {
        return section switch
        {
            "device" => DeviceForm(d),
            "outputs" => Find(d.Outputs, number, o => o.Number) is { } o ? OutputForm(d, o) : null,
            "digital-inputs" => Find(d.DigitalInputs, number, i => i.Number) is { } di ? DigitalInputForm(d, di) : null,
            "analog-inputs" => Find(d.AnalogInputs, number, i => i.Number) is { } ai ? AnalogInputForm(ai) : null,
            "digital-outputs" => Find(d.DigitalOutputs, number, o => o.Number) is { } dout ? DigitalOutputForm(dout) : null,
            "can-inputs" => Find(d.CanInputs, number, i => i.Number) is { } ci ? CanInputForm(d, ci) : null,
            "can-outputs" => Find(d.CanOutputs, number, o => o.Number) is { } co ? CanOutputForm(co) : null,
            "virtual-inputs" => Find(d.VirtualInputs, number, i => i.Number) is { } vi ? VirtualInputForm(d, vi) : null,
            "conditions" => Find(d.Conditions, number, c => c.Number) is { } c ? ConditionForm(d, c) : null,
            "counters" => Find(d.Counters, number, c => c.Number) is { } k ? CounterForm(d, k) : null,
            "flashers" => Find(d.Flashers, number, f => f.Number) is { } f ? FlasherForm(d, f) : null,
            "timers" => Find(d.Timers, number, t => t.Number) is { } t ? TimerForm(d, t) : null,
            "can-messages" => Find(d.CanMessages, number, m => m.Number) is { } m ? CanMessageForm(d, m) : null,
            _ => null
        };
    }

    // ---- Forms -----------------------------------------------------------------------------------------------

    private static BlockForm DeviceForm(FwDevice d)
    {
        var f = new List<FormField>
        {
            new TextField("Name", () => d.Name, v => d.Name = v),
            new CanIdField("Base ID", () => d.BaseId, v => d.BaseId = Math.Clamp(v, 0, 0x7FF))
                { Help = "Its status messages start at this ID; 11 bit, up to 0x7FF" },
            Choice.Of("Bitrate", () => d.BitRate, v => d.BitRate = v,
                (CanBitRate.BitRate1000K, "1000K"), (CanBitRate.BitRate500K, "500K"), (CanBitRate.BitRate250K, "250K"),
                (CanBitRate.BitRate125K, "125K"), (CanBitRate.BitRate100K, "100K")),
            new ToggleField("CAN filters", () => d.CanFiltersEnabled, v => d.CanFiltersEnabled = v),
            new ToggleField("Connect USB to CAN", () => d.ConnectUsbToCan, v => d.ConnectUsbToCan = v)
                { Shown = () => d.Def.HasUsb },
            new VariableField("Mute CAN TX while", () => d.MuteCanTxInput, v => d.MuteCanTxInput = v, ["bool"])
        };

        if (d.Def.CanSleep)
        {
            f.Add(new HeadingField("Sleep"));
            f.Add(new ToggleField("Sleep enabled", () => d.SleepEnabled, v => d.SleepEnabled = v));
            f.Add(new ToggleField("No wake on digital inputs", () => d.DisableDigInWake, v => d.DisableDigInWake = v));
            f.Add(new ToggleField("No wake on CAN", () => d.DisableCanWake, v => d.DisableCanWake = v));
            f.Add(new VariableField("Force sleep while", () => d.ForceSleepInput, v => d.ForceSleepInput = v, ["bool"]));
        }

        if (d.Keypads.Count > 0)
        {
            f.Add(new HeadingField("Keypads"));
            foreach (var kp in d.Keypads)
                f.Add(new ToggleField($"Keypad {kp.Number}", () => kp.Enabled, v => kp.Enabled = v));
        }

        return new BlockForm(d.Name, null, f);
    }

    private static BlockForm OutputForm(FwDevice d, Output o)
    {
        var owner = d.ManagedBy(o);
        var locked = owner != null;
        var pairs = new List<(int, string)> { (-1, "Not paired") };
        pairs.AddRange(d.Outputs.Where(x => x.Number != o.Number).Select(x => (x.Number - 1, $"Output {x.Number} · {x.Name}")));

        // Set up elsewhere, the current limits stay the user's
        var f = new List<FormField>
        {
            new ToggleField("Enabled", () => o.Enabled, v => o.Enabled = v) { Locked = locked },
            new TextField("Name", () => o.Name, v => o.Name = v) { Locked = locked },
            new LiveField("Now", () => o.PrimaryOutput >= 0
                ? $"follows output {o.PrimaryOutput + 1}"
                : $"{StateText(o.State)} · {o.Current:F1} A" + (o.ResetCount > 0 ? $" · {o.ResetCount} resets" : "")),
            new VariableField("Switched on by", () => o.Input, v => o.Input = v, ["bool"])
                { Locked = locked, Shown = () => o.PrimaryOutput < 0 },
            new ChoiceField("Paired with", () => o.PrimaryOutput, v => o.PrimaryOutput = v, pairs)
                { Locked = locked, Help = "A paired output follows the other one, for loads over one output's limit" },

            new HeadingField("Current"),
            new NumberField("Current limit", () => o.CurrentLimit, v => o.CurrentLimit = v, 0, 100) { Unit = "A", Decimals = 1 },
            new NumberField("Inrush limit", () => o.InrushCurrentLimit, v => o.InrushCurrentLimit = v, 0, 100) { Unit = "A", Decimals = 1 },
            new NumberField("Inrush time", () => o.InrushTime, v => o.InrushTime = (int)v, 0, 10000) { Unit = "ms" },

            new HeadingField("After a trip"),
            Choice.Of("Retry", () => o.ResetMode, v => o.ResetMode = v,
                (ResetMode.None, "Stay off"), (ResetMode.Count, "A number of times"), (ResetMode.Endless, "Forever")) with { Locked = locked },
            new NumberField("Retries", () => o.ResetCountLimit, v => o.ResetCountLimit = (int)v, 0, 20)
                { Locked = locked, Shown = () => o.ResetMode == ResetMode.Count },
            new NumberField("Wait before retry", () => o.ResetTime, v => o.ResetTime = (int)v, 0, 60000)
                { Unit = "ms", Locked = locked, Shown = () => o.ResetMode != ResetMode.None },

            new HeadingField("PWM"),
            new ToggleField("PWM", () => o.PwmEnabled, v => o.PwmEnabled = v) { Locked = locked },
            new NumberField("Frequency", () => o.Frequency, v => o.Frequency = (int)v, 0, 400)
                { Unit = "Hz", Locked = locked, Shown = () => o.PwmEnabled },
            new ToggleField("Duty cycle from a variable", () => o.VariableDutyCycle, v => o.VariableDutyCycle = v)
                { Locked = locked, Shown = () => o.PwmEnabled },
            new NumberField("Duty cycle", () => o.FixedDutyCycle, v => o.FixedDutyCycle = (int)v, 0, 100)
                { Unit = "%", Locked = locked, Shown = () => o.PwmEnabled && !o.VariableDutyCycle },
            new VariableField("Duty cycle from", () => o.DutyCycleInput, v => o.DutyCycleInput = v, ["int", "float"])
                { Locked = locked, Shown = () => o.PwmEnabled && o.VariableDutyCycle },
            new NumberField("Divided by", () => o.DutyCycleDenominator, v => o.DutyCycleDenominator = (int)v, 1, 255)
                { Locked = locked, Shown = () => o.PwmEnabled && o.VariableDutyCycle, Help = "The variable is divided by this to give 0-100 %" },
            new NumberField("Minimum duty cycle", () => o.MinDutyCycle, v => o.MinDutyCycle = (int)v, 0, 100)
                { Unit = "%", Locked = locked, Shown = () => o.PwmEnabled },
            new ToggleField("Soft start", () => o.SoftStartEnabled, v => o.SoftStartEnabled = v)
                { Locked = locked, Shown = () => o.PwmEnabled },
            new NumberField("Soft start time", () => o.SoftStartRampTime, v => o.SoftStartRampTime = (int)v, 0, 10000)
                { Unit = "ms", Locked = locked, Shown = () => o.PwmEnabled && o.SoftStartEnabled }
        };
        return new BlockForm(o.Name, owner, f);
    }

    private static BlockForm DigitalInputForm(FwDevice d, DigitalInput i)
    {
        var owner = Owner(d, i);
        return new BlockForm(i.Name, owner,
        [
            new ToggleField("Enabled", () => i.Enabled, v => i.Enabled = v),
            new TextField("Name", () => i.Name, v => i.Name = v),
            new LiveField("Now", () => OnOff(i.State)),
            Mode(() => i.Mode, v => i.Mode = v),
            new ToggleField("Invert", () => i.Invert, v => i.Invert = v),
            Choice.Of("Pull", () => i.Pull, v => i.Pull = v,
                (InputPull.NoPull, "None"), (InputPull.PullUp, "Pull-up"), (InputPull.PullDown, "Pull-down")),
            new NumberField("Debounce", () => i.DebounceTime, v => i.DebounceTime = (int)v, 0, 1000) { Unit = "ms" }
        ]);
    }

    private static BlockForm AnalogInputForm(AnalogInput i) => new(i.Name, null,
    [
        new ToggleField("Enabled", () => i.Enabled, v => i.Enabled = v),
        new LiveField("Now", () => $"{i.Millivolts:F0} mV · switch {OnOff(i.Switch.State)} · rotary {i.Rotary.Pos}"),
        new HeadingField("As a switch"),
        new ToggleField("Switch", () => i.Switch.Enabled, v => i.Switch.Enabled = v),
        Mode(() => i.Switch.Mode, v => i.Switch.Mode = v) with { Shown = () => i.Switch.Enabled },
        new ToggleField("Invert", () => i.Switch.Invert, v => i.Switch.Invert = v) { Shown = () => i.Switch.Enabled },
        new NumberField("Threshold", () => i.Switch.Threshold, v => i.Switch.Threshold = (int)v, 0, 5000)
            { Unit = "mV", Shown = () => i.Switch.Enabled },
        new HeadingField("As a rotary switch"),
        new ToggleField("Rotary switch", () => i.Rotary.Enabled, v => i.Rotary.Enabled = v),
        new NumberField("Offset", () => i.Rotary.Offset, v => i.Rotary.Offset = v, 0, 5000)
            { Unit = "mV", Shown = () => i.Rotary.Enabled },
        new NumberField("Step", () => i.Rotary.Step, v => i.Rotary.Step = v, 0, 5000)
            { Unit = "mV", Shown = () => i.Rotary.Enabled },
        new NumberField("Highest position", () => i.Rotary.MaxPos, v => i.Rotary.MaxPos = v, 0, 5000)
            { Shown = () => i.Rotary.Enabled }
    ]);

    private static BlockForm DigitalOutputForm(DigitalOutput o) => new(o.Name, null,
    [
        new ToggleField("Enabled", () => o.Enabled, v => o.Enabled = v),
        new TextField("Name", () => o.Name, v => o.Name = v),
        new LiveField("Now", () => OnOff(o.State)),
        new VariableField("Switched on by", () => o.Input, v => o.Input = v, ["bool"])
    ]);

    private static BlockForm CanInputForm(FwDevice d, CanInput i)
    {
        var owner = Owner(d, i);
        return new BlockForm(i.Name, owner,
        [
            new ToggleField("Enabled", () => i.Enabled, v => i.Enabled = v),
            new TextField("Name", () => i.Name, v => i.Name = v),
            new LiveField("Now", () => $"value {i.Value} · {OnOff(i.Output)}"),
            new HeadingField("Where in the frame"),
            new CanIdField("ID", () => i.Id, v => i.Id = v),
            new NumberField("Start bit", () => i.StartBit, v => i.StartBit = (int)v, 0, 64),
            new NumberField("Bit length", () => i.BitLength, v => i.BitLength = (int)v, 1, 64),
            Order(() => i.ByteOrder, v => i.ByteOrder = v),
            new ToggleField("Signed", () => i.Signed, v => i.Signed = v),
            new NumberField("Factor", () => i.Factor, v => i.Factor = v, -1e6, 1e6) { Decimals = 4 },
            new NumberField("Offset", () => i.Offset, v => i.Offset = v, -1e6, 1e6) { Decimals = 4 },
            new HeadingField("On when"),
            Compare(() => i.Operator, v => i.Operator = v),
            new NumberField("Than", () => i.Operand, v => i.Operand = v, -1e6, 1e6) { Decimals = 4 },
            Mode(() => i.Mode, v => i.Mode = v),
            new ToggleField("Timeout", () => i.TimeoutEnabled, v => i.TimeoutEnabled = v)
                { Help = "Reads as off when the frame stops coming" },
            new NumberField("After", () => i.Timeout, v => i.Timeout = (int)v, 0, 60000)
                { Unit = "ms", Shown = () => i.TimeoutEnabled }
        ]);
    }

    private static BlockForm CanOutputForm(CanOutput o) => new(o.Name, null,
    [
        new ToggleField("Enabled", () => o.Enabled, v => o.Enabled = v),
        new TextField("Name", () => o.Name, v => o.Name = v),
        new VariableField("Sends", () => o.Input, v => o.Input = v, ["bool", "int", "float"]),
        new CanIdField("ID", () => o.Id, v => o.Id = v),
        new NumberField("Start bit", () => o.StartBit, v => o.StartBit = (int)v, 0, 64),
        new NumberField("Bit length", () => o.BitLength, v => o.BitLength = (int)v, 1, 64),
        Order(() => o.ByteOrder, v => o.ByteOrder = v),
        new ToggleField("Signed", () => o.Signed, v => o.Signed = v),
        new NumberField("Factor", () => o.Factor, v => o.Factor = v, -1e6, 1e6) { Decimals = 4 },
        new NumberField("Offset", () => o.Offset, v => o.Offset = v, -1e6, 1e6) { Decimals = 4 },
        new NumberField("Every", () => o.Interval, v => o.Interval = (int)v, 0, 60000) { Unit = "ms" }
    ]);

    private static BlockForm VirtualInputForm(FwDevice d, VirtualInput i)
    {
        var owner = Owner(d, i);
        var locked = owner != null;
        return new BlockForm(i.Name, owner,
        [
            new ToggleField("Enabled", () => i.Enabled, v => i.Enabled = v) { Locked = locked },
            new TextField("Name", () => i.Name, v => i.Name = v) { Locked = locked },
            new LiveField("Now", () => OnOff(i.Value)),
            Mode(() => i.Mode, v => i.Mode = v) with { Locked = locked },
            new HeadingField("On when"),
            new ToggleField("Not", () => i.Not0, v => i.Not0 = v) { Locked = locked },
            new VariableField("First", () => i.Var0, v => i.Var0 = v, ["bool"]) { Locked = locked },
            Logic(() => i.Cond0, v => i.Cond0 = v) with { Locked = locked },
            new ToggleField("Not", () => i.Not1, v => i.Not1 = v) { Locked = locked },
            new VariableField("Second", () => i.Var1, v => i.Var1 = v, ["bool"]) { Locked = locked },
            Logic(() => i.Cond1, v => i.Cond1 = v) with { Locked = locked },
            new ToggleField("Not", () => i.Not2, v => i.Not2 = v) { Locked = locked },
            new VariableField("Third", () => i.Var2, v => i.Var2 = v, ["bool"]) { Locked = locked }
        ]);
    }

    private static BlockForm ConditionForm(FwDevice d, Condition c)
    {
        var owner = Owner(d, c);
        var locked = owner != null;
        return new BlockForm(c.Name, owner,
        [
            new ToggleField("Enabled", () => c.Enabled, v => c.Enabled = v) { Locked = locked },
            new TextField("Name", () => c.Name, v => c.Name = v) { Locked = locked },
            new LiveField("Now", () => c.Value.ToString()),
            new VariableField("Compares", () => c.Input, v => c.Input = v, ["bool", "int", "float"]) { Locked = locked },
            Compare(() => c.Operator, v => c.Operator = v) with { Locked = locked },
            new NumberField("With", () => c.Arg, v => c.Arg = v, -1e6, 1e6) { Decimals = 2, Locked = locked }
        ]);
    }

    private static BlockForm CounterForm(FwDevice d, Counter c)
    {
        var owner = Owner(d, c);
        var locked = owner != null;
        return new BlockForm(c.Name, owner,
        [
            new ToggleField("Enabled", () => c.Enabled, v => c.Enabled = v) { Locked = locked },
            new TextField("Name", () => c.Name, v => c.Name = v) { Locked = locked },
            new LiveField("Now", () => c.Value.ToString()),
            new NumberField("Lowest", () => c.MinCount, v => c.MinCount = (int)v, 0, 255) { Locked = locked },
            new NumberField("Highest", () => c.MaxCount, v => c.MaxCount = (int)v, 0, 255) { Locked = locked },
            new ToggleField("Wrap around", () => c.WrapAround, v => c.WrapAround = v) { Locked = locked },
            new HeadingField("Up"),
            new VariableField("Counts up on", () => c.IncInput, v => c.IncInput = v, ["bool"]) { Locked = locked },
            Edge(() => c.IncEdge, v => c.IncEdge = v) with { Locked = locked },
            new HeadingField("Down"),
            new VariableField("Counts down on", () => c.DecInput, v => c.DecInput = v, ["bool"]) { Locked = locked },
            Edge(() => c.DecEdge, v => c.DecEdge = v) with { Locked = locked },
            new HeadingField("Reset"),
            new VariableField("Resets on", () => c.ResetInput, v => c.ResetInput = v, ["bool"]) { Locked = locked },
            Edge(() => c.ResetEdge, v => c.ResetEdge = v) with { Locked = locked },
            new ToggleField("Hold to reset", () => c.HoldToReset, v => c.HoldToReset = v) { Locked = locked },
            new NumberField("Hold for", () => c.ResetTime, v => c.ResetTime = (int)v, 0, 10000)
                { Unit = "ms", Locked = locked, Shown = () => c.HoldToReset }
        ]);
    }

    private static BlockForm FlasherForm(FwDevice d, Flasher f)
    {
        var owner = Owner(d, f);
        var locked = owner != null;
        return new BlockForm(f.Name, owner,
        [
            new ToggleField("Enabled", () => f.Enabled, v => f.Enabled = v) { Locked = locked },
            new TextField("Name", () => f.Name, v => f.Name = v) { Locked = locked },
            new LiveField("Now", () => OnOff(f.Value)),
            new VariableField("Flashes while", () => f.Input, v => f.Input = v, ["bool"]) { Locked = locked },
            new ToggleField("Single flash", () => f.Single, v => f.Single = v) { Locked = locked },
            new NumberField("On", () => f.OnTime, v => f.OnTime = (int)v, 0, 60000) { Unit = "ms", Locked = locked },
            new NumberField("Off", () => f.OffTime, v => f.OffTime = (int)v, 0, 60000) { Unit = "ms", Locked = locked }
        ]);
    }

    private static BlockForm TimerForm(FwDevice d, DeviceTimer t)
    {
        var owner = Owner(d, t);
        var locked = owner != null;
        return new BlockForm(t.Name, owner,
        [
            new ToggleField("Enabled", () => t.Enabled, v => t.Enabled = v) { Locked = locked },
            new TextField("Name", () => t.Name, v => t.Name = v) { Locked = locked },
            Choice.Of("Mode", () => t.Mode, v => t.Mode = v,
                (TimerMode.OnDelay, "On delay"), (TimerMode.OffDelay, "Off delay"),
                (TimerMode.PulseRetrig, "Pulse, retriggered"), (TimerMode.PulseOneShot, "Pulse, one shot")) with { Locked = locked },
            new NumberField("Time", () => t.Time, v => t.Time = (int)v, 0, 3600000) { Unit = "ms", Locked = locked },
            new VariableField("Started by", () => t.Input, v => t.Input = v, ["bool"]) { Locked = locked },
            new VariableField("Reset by", () => t.ResetInput, v => t.ResetInput = v, ["bool"]) { Locked = locked }
        ]);
    }

    private static BlockForm CanMessageForm(FwDevice d, CanMessage m)
    {
        var owner = Owner(d, m);
        var locked = owner != null;
        return new BlockForm(m.Name, owner,
        [
            new ToggleField("Enabled", () => m.Enabled, v => m.Enabled = v) { Locked = locked },
            new TextField("Name", () => m.Name, v => m.Name = v) { Locked = locked },
            new VariableField("Sent while", () => m.Input, v => m.Input = v, ["bool"]) { Locked = locked },
            new CanIdField("ID", () => m.Id, v => m.Id = v) { Locked = locked },
            new NumberField("Length", () => m.Dlc, v => m.Dlc = (int)v, 0, CanMessage.DataLength) { Unit = "bytes", Locked = locked },
            new HexBytesField("Data", () => m.Data.Take(m.Dlc).ToList(), bytes =>
            {
                for (var i = 0; i < m.Data.Count; i++) m.Data[i] = i < bytes.Length ? bytes[i] : 0;
            }) { Locked = locked, Help = "In hex, for example 01 FF 00" },
            new NumberField("Repeat every", () => m.Interval, v => m.Interval = (int)v, 0, 60000)
                { Unit = "ms", Locked = locked, Help = "0 sends once each time it is switched on" }
        ]);
    }

    // ---- Shared pieces ---------------------------------------------------------------------------------------

    private static ChoiceField Mode(Func<InputMode> get, Action<InputMode> set) =>
        Choice.Of("Mode", get, set, (InputMode.Momentary, "Momentary"), (InputMode.Latched, "Latching (each press toggles)"));

    private static ChoiceField Order(Func<ByteOrder> get, Action<ByteOrder> set) =>
        Choice.Of("Byte order", get, set, (ByteOrder.LittleEndian, "Intel (little endian)"), (ByteOrder.BigEndian, "Motorola (big endian)"));

    private static ChoiceField Compare(Func<Operator> get, Action<Operator> set) =>
        Choice.Of("Is", get, set, (Operator.Equal, "equal to"), (Operator.NotEqual, "not equal to"),
            (Operator.GreaterThan, "greater than"), (Operator.LessThan, "less than"),
            (Operator.GreaterThanOrEqual, "at least"), (Operator.LessThanOrEqual, "at most"),
            (Operator.BitwiseAnd, "has bits of (AND)"), (Operator.BitwiseNand, "lacks bits of (NAND)"));

    private static ChoiceField Logic(Func<Conditional> get, Action<Conditional> set) =>
        Choice.Of("Then", get, set, (Conditional.And, "and"), (Conditional.Or, "or"), (Conditional.Nor, "nor"));

    private static ChoiceField Edge(Func<InputEdge> get, Action<InputEdge> set) =>
        Choice.Of("On", get, set, (InputEdge.Rising, "switching on"), (InputEdge.Falling, "switching off"), (InputEdge.Both, "both"));

    private static void Add<T>(List<MobileSection> list, string key, string title, IEnumerable<T> blocks, Func<T, MobileItem> item)
    {
        var items = blocks.Select(item).ToList();
        if (items.Count > 0) list.Add(new MobileSection(key, title, items));
    }

    private static T? Find<T>(IEnumerable<T> blocks, int number, Func<T, int> numberOf) where T : class =>
        blocks.FirstOrDefault(b => numberOf(b) == number);

    private static string? Owner(FwDevice d, object block) => block is IDeviceFunction f ? d.ManagedBy(f) : null;

    private static string OutputValue(Output o) => o.State switch
    {
        OutState.Overcurrent => "OVERCURRENT",
        OutState.Fault => "FAULT",
        _ when o.PrimaryOutput >= 0 => $"follows {o.PrimaryOutput + 1}",
        _ => $"{o.Current:F1} A"
    };

    private static string StateText(OutState state) => state switch
    {
        OutState.On => "on",
        OutState.Overcurrent => "overcurrent",
        OutState.Fault => "fault",
        _ => "off"
    };

    public static string OnOff(bool state) => state ? "ON" : "OFF";

    public static string Hex(int id) => id > 0x7FF ? $"0x{id:X8}" : $"0x{id:X3}";

    private static string BitRateText(CanBitRate rate) => rate switch
    {
        CanBitRate.BitRate1000K => "1000K",
        CanBitRate.BitRate500K => "500K",
        CanBitRate.BitRate250K => "250K",
        CanBitRate.BitRate125K => "125K",
        CanBitRate.BitRate100K => "100K",
        _ => rate.ToString()
    };

    private static string Seconds(int ms) => (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " s";

    private static string TimerModeText(TimerMode mode) => mode switch
    {
        TimerMode.OnDelay => "On delay",
        TimerMode.OffDelay => "Off delay",
        TimerMode.PulseRetrig => "Pulse",
        TimerMode.PulseOneShot => "One shot",
        _ => mode.ToString()
    };
}
