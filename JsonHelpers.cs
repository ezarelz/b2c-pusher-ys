using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace B2cPusher;

public static class JsonHelpers
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Nilai node sebagai teks: string tanpa tanda kutip, selain itu JSON-nya.</summary>
    public static string AsText(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return node.ToJsonString();
    }

    public static string Field(JsonObject obj, string field) => AsText(obj[field]);

    /// <summary>Ambil node lewat path bertitik, misalnya "partParam.orgCode".</summary>
    public static JsonNode? GetPath(JsonNode? node, string path)
    {
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is not JsonObject obj) return null;
            node = obj[part];
        }
        return node;
    }

    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Cari objek (di kedalaman mana pun) yang field-nya sama dengan nilai yang dicari.</summary>
    public static JsonObject? FindObjectWith(JsonNode? node, string field, string value)
    {
        switch (node)
        {
            case JsonObject obj:
                if (AsText(obj[field]) == value) return obj;
                foreach (var kv in obj)
                {
                    var found = FindObjectWith(kv.Value, field, value);
                    if (found != null) return found;
                }
                return null;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    var found = FindObjectWith(item, field, value);
                    if (found != null) return found;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Ambil ID YonSuite untuk tid dari bagian "data" respons import.
    /// Mendukung bentuk {"&lt;id&gt;":"&lt;tid&gt;"}, {"&lt;tid&gt;":"&lt;id&gt;"}, atau objek berisi tid + id.
    /// </summary>
    public static string? FindIdForTid(JsonNode? data, string tidField, string tid)
    {
        if (data is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (kv.Value is JsonValue && AsText(kv.Value) == tid) return kv.Key;
                if (kv.Key == tid && kv.Value is JsonValue) return AsText(kv.Value);
            }
        }

        var match = FindObjectWith(data, tidField, tid);
        if (match != null)
        {
            var id = AsText(match["id"]);
            return id;
        }
        return null;
    }

    public static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
