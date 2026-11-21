using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace B2cPusher;

/// <summary>
/// YonSuite palsu untuk tes lokal. Perilaku ditentukan dari isi tid:
///   ...BADSTORE... -> ditolak: 店铺编码不存在或没有做门店对照 (code tetap "200", seperti aslinya)
///   ...TIMEOUT...  -> order DICATAT, tapi respons pertama lambat 30 detik (klien timeout)
///   ...DROP...     -> panggilan pertama TIDAK dicatat dan lambat 30 detik; panggilan kedua normal
///   ...LOSTDATA... -> dicatat, pesan sukses, tapi tid tidak muncul di data
///   lainnya        -> sukses normal
/// Mock juga mencatat import ganda, supaya terlihat kalau aplikasi membuat order dobel.
/// Lihat keadaan mock di http://localhost:5055/mock/state
/// </summary>
public static class MockServer
{
    private const string Token = "mock-token-123";
    private const string Url = "http://localhost:5055";

    private static readonly ConcurrentDictionary<string, List<string>> Imported = new();
    private static readonly ConcurrentDictionary<string, int> Calls = new();

    private static readonly JsonSerializerOptions JsonOut = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public static async Task RunAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();

        app.MapGet("/open-auth/selfAppAuth/getAccessToken", () => Json(new JsonObject
        {
            ["code"] = "00000",
            ["message"] = "成功！",
            ["data"] = new JsonObject { ["access_token"] = Token, ["expire"] = 7200 }
        }));

        app.MapPost("/yonbip/sd/dst/tradevouch/import", async (HttpRequest req) =>
        {
            if (req.Query["access_token"] != Token) return Unauthorized();

            var body = await ReadJson(req);
            var orders = body as JsonArray ?? (body as JsonObject)?["data"] as JsonArray ?? new JsonArray();

            var data = new JsonObject();
            int ok = 0, failed = 0;
            var reasons = new List<string>();
            var slow = false;
            var dropped = false;

            foreach (var node in orders)
            {
                if (node is not JsonObject o) continue;
                var tid = JsonHelpers.Field(o, "tid");
                var n = Calls.AddOrUpdate(tid, 1, (_, v) => v + 1);

                if (tid.Contains("BADSTORE"))
                {
                    failed++;
                    reasons.Add($"{tid}: 店铺编码不存在或没有做门店对照");
                    Console.WriteLine($"[mock] import {tid} -> DITOLAK (toko)");
                    continue;
                }

                if (tid.Contains("DROP") && n == 1)
                {
                    dropped = true;
                    Console.WriteLine($"[mock] import {tid} -> request 'hilang' (tidak dicatat, tidak dijawab)");
                    continue;
                }

                var id = Random.Shared.NextInt64(2_000_000_000_000_000_000, 3_000_000_000_000_000_000).ToString();
                var list = Imported.GetOrAdd(tid, _ => new List<string>());
                int total;
                lock (list) { list.Add(id); total = list.Count; }
                ok++;

                Console.WriteLine($"[mock] import {tid} -> DICATAT id {id}{(total > 1 ? $"  !!! DUPLIKAT ke-{total} !!!" : "")}");

                if (tid.Contains("TIMEOUT") && n == 1) slow = true;
                if (!tid.Contains("LOSTDATA")) data[id] = tid;
            }

            if (slow || dropped) await Task.Delay(TimeSpan.FromSeconds(30));

            var message = failed == 0
                ? "导入原单成功 导入失败0单"
                : $"导入原单成功{ok}单 导入失败{failed}单: {string.Join("; ", reasons)}";

            return Json(new JsonObject { ["code"] = "200", ["message"] = message, ["data"] = data });
        });

        app.MapPost("/yonbip/sd/dst/tradevouch/query", async (HttpRequest req) =>
        {
            if (req.Query["access_token"] != Token) return Unauthorized();

            var body = await ReadJson(req) as JsonObject;
            var tid = JsonHelpers.AsText((body?["partParam"] as JsonObject)?["tid"]);
            if (string.IsNullOrEmpty(tid)) tid = JsonHelpers.AsText(body?["tid"]);

            var records = new JsonArray();
            if (Imported.TryGetValue(tid, out var ids))
            {
                lock (ids)
                {
                    foreach (var id in ids) records.Add(new JsonObject { ["id"] = id, ["tid"] = tid });
                }
            }
            Console.WriteLine($"[mock] query  {tid} -> {(records.Count == 0 ? "tidak ada" : $"{records.Count} record")}");

            return Json(new JsonObject
            {
                ["code"] = "200",
                ["message"] = "操作成功",
                ["data"] = new JsonObject { ["recordCount"] = records.Count, ["recordList"] = records }
            });
        });

        app.MapGet("/mock/state", () =>
        {
            var orders = new JsonArray();
            foreach (var kv in Imported.OrderBy(k => k.Key))
            {
                int count;
                lock (kv.Value) count = kv.Value.Count;
                orders.Add(new JsonObject { ["tid"] = kv.Key, ["importCount"] = count, ["duplicate"] = count > 1 });
            }
            var dup = Imported.Count(kv => { lock (kv.Value) return kv.Value.Count > 1; });
            return Json(new JsonObject
            {
                ["ordersInMock"] = Imported.Count,
                ["duplicates"] = dup,
                ["orders"] = orders
            });
        });

        app.MapPost("/mock/reset", () =>
        {
            Imported.Clear();
            Calls.Clear();
            Console.WriteLine("[mock] reset");
            return Results.Ok("reset");
        });

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"Mock YonSuite jalan di {Url}");
        Console.WriteLine($"  Lihat keadaan: {Url}/mock/state    Reset: POST {Url}/mock/reset");
        Console.WriteLine("  Ctrl+C untuk berhenti.");
        await app.RunAsync(Url);
    }

    private static async Task<JsonNode?> ReadJson(HttpRequest req)
    {
        using var reader = new StreamReader(req.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        return JsonHelpers.TryParse(text);
    }

    private static IResult Json(JsonObject obj) =>
        Results.Text(obj.ToJsonString(JsonOut), "application/json; charset=utf-8", Encoding.UTF8);

    private static IResult Unauthorized() =>
        Results.Text("{\"code\":\"401\",\"message\":\"token invalid\"}", "application/json; charset=utf-8", Encoding.UTF8, 401);
}
