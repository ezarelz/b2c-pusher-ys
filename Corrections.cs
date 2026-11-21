using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace B2cPusher;

/// <summary>
/// Koreksi data tanpa mengubah file asli. Format corrections.csv:
///   tid,field,value,reason
///   SIM-MOCK-005,orgCode,sopi,orgCode lupa diisi
/// Hanya field di level order (bukan field bersarang). Di Ecolink nanti ini jadi tabel koreksi + halaman UI.
/// </summary>
public sealed class Corrections
{
    private readonly Dictionary<string, List<(string Field, string Value, string Reason)>> _byTid = new();

    public static Corrections Load(string path)
    {
        var c = new Corrections();
        if (!File.Exists(path)) return c;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("tid,", StringComparison.OrdinalIgnoreCase)) continue; // header

            var parts = line.Split(',', 4);
            if (parts.Length < 3) continue;

            var tid = parts[0].Trim();
            var reason = parts.Length == 4 ? parts[3].Trim() : "";
            if (!c._byTid.TryGetValue(tid, out var list))
            {
                list = new();
                c._byTid[tid] = list;
            }
            list.Add((parts[1].Trim(), parts[2].Trim(), reason));
        }
        return c;
    }

    /// <summary>Terapkan koreksi ke order. Mengembalikan daftar keterangan koreksi yang diterapkan.</summary>
    public List<string> Apply(string tid, JsonObject order)
    {
        var applied = new List<string>();
        if (!_byTid.TryGetValue(tid, out var list)) return applied;

        foreach (var (field, value, reason) in list)
        {
            var old = order[field];
            JsonNode? newNode;
            if (old is JsonValue && old.GetValueKind() == JsonValueKind.Number
                && decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            {
                newNode = JsonValue.Create(d);
            }
            else
            {
                newNode = JsonValue.Create(value);
            }

            applied.Add($"{field}: '{JsonHelpers.AsText(old)}' -> '{value}' ({reason})");
            order[field] = newNode;
        }
        return applied;
    }
}
