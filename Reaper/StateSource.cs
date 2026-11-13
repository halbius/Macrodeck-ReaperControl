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

/// <summary>"1" while REAPER plays or records (also paused), "0" while stopped - from the TRANSPORT reply.</summary>
public sealed record TransportPlaying : StateSource
{
    public override string Key => "transport:playing";
    public override string Request => "TRANSPORT";
}

/// <summary>"1" while REAPER is paused (also record-paused), "0" otherwise - from the TRANSPORT reply.</summary>
public sealed record TransportPaused : StateSource
{
    public override string Key => "transport:paused";
    public override string Request => "TRANSPORT";
}

/// <summary>"1" while REAPER records (also record-paused), "0" otherwise - from the TRANSPORT reply.</summary>
public sealed record TransportRecording : StateSource
{
    public override string Key => "transport:recording";
    public override string Request => "TRANSPORT";
}
