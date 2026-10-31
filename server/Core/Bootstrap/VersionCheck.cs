using System.Text.Json;

namespace Brs.Local.Bootstrap;

public static class VersionCheck
{
    // The client's review check only reads these Android action fields. An empty
    // review version retains the API root selected before this request.
    // This response does not log in a player or claim the game service is complete.
    public static byte[] Response() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        common = new { error_id = 0 },
        action = new { review_android_version = "", review_android_api_url = "" }
    });
}
