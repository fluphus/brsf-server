using System.Collections.ObjectModel;

namespace Brs.Portable.Content;

public static class RawQueryAdapter
{
    // ASP.NET query semantics: '&' components, first '=', '+' spaces, percent decode,
    // case-insensitive decoded keys; preserve every duplicate value (including V/v).
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(string raw)
    {
        if (raw.Length != 0 && raw[0] != '?') throw new InvalidDataException("Raw query requires its leading question mark.");
        var items = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in (raw.Length == 0 ? "" : raw[1..]).Split('&'))
        {
            if (segment.Length == 0) continue;
            var equals = segment.IndexOf('=');
            var key = Decode(equals < 0 ? segment : segment[..equals]);
            var value = Decode(equals < 0 ? "" : segment[(equals + 1)..]);
            if (!items.TryGetValue(key, out var values)) items.Add(key, values = []);
            values.Add(value);
        }
        return new ReadOnlyDictionary<string, IReadOnlyList<string>>(items.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)Array.AsReadOnly(p.Value.ToArray()), StringComparer.OrdinalIgnoreCase));
    }
    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
