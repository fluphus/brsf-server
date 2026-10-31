using System.Globalization;
using System.Text.Json;

namespace Brs.AndroidLocalHost;

// Single process-memory value only. Never saved to Bundle/preferences/files or
// logs. The Android adapter exposes only this fixed prompt protocol, not a
// generic text/URL/file/clipboard bridge.
internal sealed class NativeCredentials
{
    public const string PromptName = "BRSF_CREDENTIALS_V1";
    public const string RootUrl = "http://127.0.0.1:18180/";
    private readonly object gate = new();
    private Value? value;
    private sealed class Value(string uid, string id, string password)
    {
        public string Uid { get; } = uid;
        public string Id { get; } = id;
        public string Password { get; } = password;
        public override string ToString() => "[retained credentials]";
    }
    public static bool IsRoot(string? url) => url == RootUrl;
    private static bool Alphanumeric(string text) => text.All(char.IsAsciiLetterOrDigit);
    private static string Reply(bool ok) => ok ? "{\"ok\":true}" : "{\"ok\":false}";
    public string Handle(string? senderUrl, string? currentUrl, string? payload, Action<string> copy)
    {
        if (!IsRoot(senderUrl) || !IsRoot(currentUrl) || payload is null || payload.Length > 1024) return Reply(false);
        try
        {
            using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("action", out var actionValue) || actionValue.ValueKind != JsonValueKind.String) return Reply(false);
            var action = actionValue.GetString(); var fields = root.EnumerateObject().Select(p => p.Name).ToArray();
            bool Fields(params string[] expected) => fields.Length == expected.Length && fields.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
            lock (gate)
            {
                switch (action)
                {
                    case "retain" when Fields("action", "uid", "takeoverId", "password"):
                    {
                        var uid = root.GetProperty("uid").GetString()!;
                        var id = root.GetProperty("takeoverId").GetString()!;
                        var password = root.GetProperty("password").GetString()!;
                        if (uid is null || !ulong.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n == 0 || n.ToString(CultureInfo.InvariantCulture) != uid ||
                            id is null || id.Length != 16 || !Alphanumeric(id) || password is null || password.Length is < 8 or > 12 || !Alphanumeric(password)) return Reply(false);
                        value = new(uid, id, password); return Reply(true);
                    }
                    case "restore" when Fields("action"):
                        return JsonSerializer.Serialize(new { ok = true, record = value is null ? null : new { uid = value.Uid, takeoverId = value.Id, password = value.Password } });
                    case "clear" when Fields("action"):
                        value = null; return Reply(true);
                    case "copy" when Fields("action", "field"):
                    {
                        if (value is null) return Reply(false);
                        var text = root.GetProperty("field").GetString() switch
                        {
                            "id" => value.Id, "password" => value.Password,
                            "all" => "ID: " + value.Id + "\nPASS: " + value.Password, _ => null
                        };
                        if (text is null) return Reply(false);
                        copy(text); return Reply(true);
                    }
                    default: return Reply(false);
                }
            }
        }
        catch { return Reply(false); } // no exception/payload logging
    }
}
