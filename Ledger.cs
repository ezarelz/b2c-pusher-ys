using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace B2cPusher;

public static class Status
{
    public const string FailedValidation = "FAILED_VALIDATION";
    public const string SentUnconfirmed = "SENT_UNCONFIRMED";
    public const string FailedPush = "FAILED_PUSH";
    public const string Success = "SUCCESS";
    public const string ChangedAfterPush = "CHANGED_AFTER_PUSH";
}

public sealed class LedgerEntry
{
    public string Key { get; set; } = "";
    public string Tid { get; set; } = "";
    public string Shopcode { get; set; } = "";
    public string Status { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public string YonsuiteId { get; set; } = "";
    public string LastMessage { get; set; } = "";
    /// <summary>Pesan asli dari YonSuite (bisa Mandarin), untuk laporan ke YY.</summary>
    public string RawMessage { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string SourceFile { get; set; } = "";
}

/// <summary>
/// Ledger sederhana berbasis file JSON (tanpa paket NuGet).
/// Disimpan ulang setiap kali ada perubahan status, ditulis atomik (tmp lalu rename),
/// supaya crash di tengah proses tidak merusak file.
/// Di Ecolink nanti diganti tabel database dengan unique key (shopcode, tid).
/// </summary>
public sealed class Ledger
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    public Dictionary<string, LedgerEntry> Entries { get; private set; } = new();

    private Ledger(string path) => _path = path;

    public static string MakeKey(string shopcode, string tid) => $"{shopcode}|{tid}";

    public static Ledger Load(string path)
    {
        var ledger = new Ledger(path);
        if (File.Exists(path))
        {
            var list = JsonSerializer.Deserialize<List<LedgerEntry>>(File.ReadAllText(path), Options) ?? new();
            ledger.Entries = list.ToDictionary(e => e.Key, e => e);
        }
        return ledger;
    }

    public LedgerEntry? Get(string key) => Entries.TryGetValue(key, out var e) ? e : null;

    public LedgerEntry GetOrCreate(string key, string tid, string shopcode, string sourceFile)
    {
        if (!Entries.TryGetValue(key, out var e))
        {
            e = new LedgerEntry
            {
                Key = key,
                Tid = tid,
                Shopcode = shopcode,
                CreatedAt = DateTimeOffset.Now,
                SourceFile = sourceFile
            };
            Entries[key] = e;
        }
        return e;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        var json = JsonSerializer.Serialize(Entries.Values.OrderBy(e => e.CreatedAt).ToList(), Options);
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, _path, overwrite: true);
    }
}