namespace ReaperControl.Reaper;

/// <summary>A value in REAPER that the plugin can read via the web interface.</summary>
public abstract record StateSource
{
    /// <summary>Cache key, unique per value.</summary>
    public abstract string Key { get; }

    /// <summary>The web interface request part that reads it.</summary>
    public abstract string Request { get; }
}

/// <summary>Toggle state of an action: "&gt;0" on, "0" off, "-1" the action has no state.</summary>
public sealed record CommandToggleState(string CommandId) : StateSource
{
    public override string Key => "cmd:" + CommandId;
    public override string Request => "GET/" + CommandId;
}

/// <summary>
/// A value a ReaScript published with reaper.SetExtState(section, name, value, false).
/// Empty when it was never set - usually because the script is not running.
/// </summary>
public sealed record ExtStateValue(string Section, string Name) : StateSource
{
    public override string Key => $"ext:{Section}/{Name}";
    public override string Request => $"GET/EXTSTATE/{Section}/{Name}";
}
