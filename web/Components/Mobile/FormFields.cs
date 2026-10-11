namespace web.Components.Mobile;

/// <summary>One row of a block's form on a phone. MobileForm renders the kinds below.</summary>
public abstract record FormField(string Label)
{
    public string? Help { get; init; }

    /// <summary>Set up elsewhere (a vehicle function or the ignition): shown, not editable</summary>
    public bool Locked { get; init; }

    /// <summary>Hidden while this returns false, e.g. the reset time without a reset mode</summary>
    public Func<bool>? Shown { get; init; }
}

public sealed record HeadingField(string Label) : FormField(Label);

public sealed record ToggleField(string Label, Func<bool> Get, Action<bool> Set) : FormField(Label);

public sealed record TextField(string Label, Func<string> Get, Action<string> Set, int MaxLength = 32) : FormField(Label);

public sealed record NumberField(string Label, Func<double> Get, Action<double> Set, double Min, double Max) : FormField(Label)
{
    public string? Unit { get; init; }
    public int Decimals { get; init; }
}

public sealed record ChoiceField(string Label, Func<int> Get, Action<int> Set, IReadOnlyList<(int Value, string Text)> Options)
    : FormField(Label);

/// <summary>A variable from the device's var map, picked in the variable dialog</summary>
public sealed record VariableField(string Label, Func<int> Get, Action<int> Set, string[] Types) : FormField(Label);

/// <summary>A CAN ID in hex; above 0x7FF it is extended</summary>
public sealed record CanIdField(string Label, Func<int> Get, Action<int> Set) : FormField(Label);

/// <summary>Up to eight bytes in hex, e.g. "01 FF 00"</summary>
public sealed record HexBytesField(string Label, Func<IReadOnlyList<int>> Get, Action<byte[]> Set) : FormField(Label);

/// <summary>A live value from the device, not editable</summary>
public sealed record LiveField(string Label, Func<string> Get) : FormField(Label);

public static class Choice
{
    /// <summary>A choice between the values of an enum, with the given texts</summary>
    public static ChoiceField Of<TEnum>(string label, Func<TEnum> get, Action<TEnum> set, params (TEnum Value, string Text)[] options)
        where TEnum : struct, Enum =>
        new(label, () => Convert.ToInt32(get()), v => set((TEnum)Enum.ToObject(typeof(TEnum), v)),
            options.Select(o => (Convert.ToInt32(o.Value), o.Text)).ToList());
}
