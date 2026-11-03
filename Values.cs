using System.Globalization;
using System.Text.Json;

namespace ReaperControl;

/// <summary>
/// Parameter and form values arrive as <c>object</c> - depending on the transport a string,
/// a number or a JsonElement. This normalizes them to strings.
/// </summary>
internal static class Values
{
    public static string? AsString(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement e => e.GetRawText(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    // Generic so it takes both config-flow input (object?) and ActionExecutionContext.Parameters
    // (object): two overloads differing only in nullability are the same signature (CS0111).
    public static string? Get<T>(IReadOnlyDictionary<string, T> values, string key) =>
        values.TryGetValue(key, out var v) ? NullIfBlank(AsString(v)) : null;

    public static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
