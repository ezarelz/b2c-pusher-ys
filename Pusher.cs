using System.Text;
using System.Text.Json.Nodes;

namespace B2cPusher;

public sealed class Pusher
{
    private readonly AppConfig _cfg;
    private readonly Ledger _ledger;
    private readonly YonSuiteClient _ys;

    public Pusher(AppConfig cfg)
    {
        _cfg = cfg;
        _ledger = Ledger.Load(cfg.LedgerPath);
        _ys = new YonSuiteClient(cfg);
    }

    // ------------------------------------------------------------------ push

    public async Task<int> PushFileAsync(string file, bool dryRun, HashSet<string>? only = null, bool force = false)
    {
        var root = JsonNode.Parse(File.ReadAllText(file))
                   ?? throw new InvalidOperationException("File kosong: " + file);
        var (orders, makeBody, rootOrgCode) = SplitOrders(root);
        var corrections = Corrections.Load(_cfg.CorrectionsPath);
        var sourceFile = Path.GetFileName(file);

        var counts = new SortedDictionary<string, int>();
        void Count(string k) => counts[k] = counts.GetValueOrDefault(k) + 1;

        Console.WriteLine($"File: {file} ({orders.Count} order){(dryRun ? "  [DRY-RUN: tidak ada yang dikirim]" : "")}");
        if (only != null) Console.WriteLine($"Hanya order: {string.Join(", ", only)}");
        if (force) Console.WriteLine("FORCE: cek ledger dilewati, order terpilih dikirim ulang walaupun sudah SUCCESS");
        Console.WriteLine(new string('-', 78));

        for (var i = 0; i < orders.Count; i++)
        {
            if (orders[i] is not JsonObject original)
            {
                Count(Status.FailedValidation);
                Console.WriteLine($"[{i + 1}] bukan objek order, dilewati");
                continue;
            }

            // Kunci ledger diambil dari data ASLI (sebelum koreksi), supaya koreksi shopcode
            // tidak membuat baris ledger baru.
            var tid = JsonHelpers.Field(original, _cfg.TidField);
            var origShop = JsonHelpers.Field(original, _cfg.ShopcodeField);
            var key = Ledger.MakeKey(origShop, string.IsNullOrEmpty(tid) ? $"(tid kosong #{i + 1})" : tid);

            if (only != null && !only.Contains(tid))
            {
                Count("TIDAK DIPILIH (--only)");
                continue;
            }

            var order = original.DeepClone().AsObject();
            var applied = corrections.Apply(tid, order);
            var shop = JsonHelpers.Field(order, _cfg.ShopcodeField);
            var hash = JsonHelpers.Sha256(order.ToJsonString());
            var entry = _ledger.Get(key);

            Console.Write($"[{i + 1}/{orders.Count}] {shop,-7} {tid,-28} ");
            foreach (var a in applied) Console.Write($"\n      koreksi {a}\n      ");

            // --force: kirim ulang dengan sengaja (mis. order gagal transfer di YonSuite
            // setelah setting toko diperbaiki). Ledger tetap dicatat.
            if (force)
            {
                if (entry != null) Console.Write($"(force, status sebelumnya {entry.Status}) ");
                entry = null;
            }

            // 1. Sudah SUCCESS: lewati, atau tandai kalau datanya berubah.
            if (entry?.Status == Status.Success)
            {
                if (entry.PayloadHash == hash)
                {
                    Count("SKIPPED (sudah SUCCESS)");
                    Console.WriteLine("sudah SUCCESS, dilewati");
                }
                else
                {
                    if (!dryRun)
                    {
                        entry.Status = Status.ChangedAfterPush;
                        entry.LastMessage = "Data berubah setelah SUCCESS; tidak dikirim, perlu ditinjau manual";
                        _ledger.Save();
                    }
                    Count(Status.ChangedAfterPush);
                    Console.WriteLine("CHANGED_AFTER_PUSH (data berubah setelah sukses, tidak dikirim)");
                }
                continue;
            }

            if (entry?.Status == Status.ChangedAfterPush)
            {
                // PayloadHash masih hash payload yang dulu sukses terkirim.
                // Kalau datanya dikembalikan persis sama, status kembali ke SUCCESS.
                if (entry.PayloadHash == hash)
                {
                    if (!dryRun)
                    {
                        entry.Status = Status.Success;
                        entry.LastMessage = "Data kembali sama dengan yang sukses terkirim";
                        _ledger.Save();
                    }
                    Count("SKIPPED (sudah SUCCESS)");
                    Console.WriteLine("data kembali sama dengan yang sukses terkirim, status kembali SUCCESS, dilewati");
                    continue;
                }

                Count(Status.ChangedAfterPush);
                Console.WriteLine("CHANGED_AFTER_PUSH, tidak dikirim");
                continue;
            }

            // 2. Sebelumnya tidak pasti: cek dulu lewat query, jangan push buta.
            if (entry?.Status == Status.SentUnconfirmed && !dryRun)
            {
                var q = await _ys.QueryAsync(shop, tid);
                if (q.Found)
                {
                    MarkSuccess(entry, q.Id, "Dikonfirmasi lewat query");
                    _ledger.Save();
                    Count(Status.Success);
                    Console.WriteLine($"sudah ada di YonSuite (query), jadi SUCCESS tanpa kirim ulang. ID {q.Id}");
                    continue;
                }
                if (q.Error != null)
                {
                    entry.LastMessage = "Query gagal, status masih tidak pasti: " + q.Error;
                    _ledger.Save();
                    Count(Status.SentUnconfirmed);
                    Console.WriteLine("query gagal, tetap SENT_UNCONFIRMED (tidak dikirim ulang)");
                    continue;
                }
                Console.Write("tidak ditemukan di YonSuite, kirim ulang... ");
            }

            // 3. Validasi.
            var errors = Validate(order, tid, shop, rootOrgCode);
            if (errors.Count > 0)
            {
                if (!dryRun)
                {
                    var e = _ledger.GetOrCreate(key, tid, origShop, sourceFile);
                    e.Shopcode = shop;
                    e.Status = Status.FailedValidation;
                    e.PayloadHash = hash;
                    e.LastMessage = string.Join("; ", errors);
                    _ledger.Save();
                }
                Count(Status.FailedValidation);
                Console.WriteLine("FAILED_VALIDATION: " + string.Join("; ", errors));
                continue;
            }

            var body = makeBody(order);

            if (dryRun)
            {
                Directory.CreateDirectory(_cfg.DryRunDir);
                var path = Path.Combine(_cfg.DryRunDir, SafeName(tid) + ".json");
                File.WriteAllText(path, body.ToJsonString(JsonHelpers.Pretty), Encoding.UTF8);
                Count("DRY-RUN (payload ditulis)");
                Console.WriteLine($"lolos validasi, payload ditulis ke {path}");
                continue;
            }

            // 4. Tulis SENT_UNCONFIRMED SEBELUM memanggil API.
            var le = _ledger.GetOrCreate(key, tid, origShop, sourceFile);
            le.Shopcode = shop; // shopcode final (setelah koreksi) dipakai untuk query/verify
            le.Status = Status.SentUnconfirmed;
            le.PayloadHash = hash;
            le.Attempts++;
            le.LastAttemptAt = DateTimeOffset.Now;
            le.LastMessage = "Sedang dikirim";
            _ledger.Save();

            // 5. Kirim dan baca hasilnya.
            var r = await _ys.ImportAsync(body.ToJsonString());
            WriteLog(tid, le.Attempts, body, r);
            ApplyImportResult(le, tid, r);
            _ledger.Save();

            Count(le.Status);
            Console.WriteLine(le.Status == Status.Success
                ? $"SUCCESS, ID {le.YonsuiteId}"
                : $"{le.Status}: {JsonHelpers.Truncate(le.LastMessage, 120)}");

            if (_cfg.DelayMsBetweenCalls > 0) await Task.Delay(_cfg.DelayMsBetweenCalls);
        }

        PrintSummary(orders.Count, counts);
        return 0;
    }

    private void ApplyImportResult(LedgerEntry e, string tid, ImportResult r)
    {
        if (r.NotSent)
        {
            e.Status = Status.FailedPush;
            e.LastMessage = "Belum terkirim: " + r.Error;
            return;
        }

        if (r.NoResponse)
        {
            e.Status = Status.SentUnconfirmed;
            e.LastMessage = $"Tidak ada respons ({r.Error}); jalankan verify";
            return;
        }

        var json = JsonHelpers.TryParse(r.Text) as JsonObject;
        if (json is null)
        {
            // Server error tanpa JSON: bisa jadi order sudah tercatat. Anggap tidak pasti.
            e.Status = r.Http >= 500 ? Status.SentUnconfirmed : Status.FailedPush;
            e.LastMessage = $"HTTP {r.Http}: {JsonHelpers.Truncate(r.Text, 300)}";
            return;
        }

        var message = JsonHelpers.AsText(json["message"]);
        var code = JsonHelpers.AsText(json["code"]);
        var shown = MessageText.ToIndonesian(message);
        e.RawMessage = message;

        if (message.Contains(_cfg.SuccessMessage))
        {
            var id = JsonHelpers.FindIdForTid(json["data"], _cfg.TidField, tid);
            if (id != null)
            {
                MarkSuccess(e, id, shown);
            }
            else
            {
                // Pesan sukses tapi tid tidak ada di data: jangan percaya, cek lewat query.
                e.Status = Status.SentUnconfirmed;
                e.LastMessage = "Pesan sukses tetapi tid tidak ada di data; jalankan verify";
            }
            return;
        }

        if (r.Http >= 500)
        {
            e.Status = Status.SentUnconfirmed;
            e.LastMessage = $"HTTP {r.Http} {code} {shown}; jalankan verify";
            return;
        }

        e.Status = Status.FailedPush;
        if (string.IsNullOrWhiteSpace(message))
        {
            e.RawMessage = JsonHelpers.Truncate(r.Text, 300);
            e.LastMessage = MessageText.ToIndonesian(e.RawMessage);
        }
        else
        {
            // code "200" tidak ditampilkan karena menyesatkan (gagal tapi code 200).
            e.LastMessage = code is "" or "200" ? $"Ditolak YonSuite: {shown}" : $"Ditolak YonSuite (code {code}): {shown}";
        }
    }

    private static void MarkSuccess(LedgerEntry e, string id, string message)
    {
        e.Status = Status.Success;
        e.YonsuiteId = id;
        e.ConfirmedAt = DateTimeOffset.Now;
        e.LastMessage = message;
    }

    private List<string> Validate(JsonObject order, string tid, string shop, string rootOrgCode)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(tid)) errors.Add($"{_cfg.TidField} kosong");
        if (string.IsNullOrWhiteSpace(shop)) errors.Add($"{_cfg.ShopcodeField} kosong");
        else if (_cfg.AllowedShopcodes.Count > 0 && !_cfg.AllowedShopcodes.Contains(shop))
            errors.Add($"shopcode '{shop}' tidak ada di daftar toko");

        var org = order.ContainsKey(_cfg.OrgCodeField)
            ? JsonHelpers.Field(order, _cfg.OrgCodeField)
            : rootOrgCode;
        if (string.IsNullOrWhiteSpace(org)) errors.Add($"{_cfg.OrgCodeField} kosong");

        if (order["payment"] is JsonValue pv && decimal.TryParse(JsonHelpers.AsText(pv),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var payment)
            && payment < 0)
            errors.Add("payment negatif");

        return errors;
    }

    /// <summary>
    /// Ambil daftar order dari file dan cara membentuk body import untuk satu order.
    /// Mendukung: {"data":[...]} (properti lain di root ikut dikirim), [...], atau satu objek order.
    /// </summary>
    private (JsonArray Orders, Func<JsonObject, JsonObject> MakeBody, string RootOrgCode) SplitOrders(JsonNode root)
    {
        var prop = _cfg.OrdersArrayProperty;

        if (root is JsonArray arr)
            return (arr, o => new JsonObject { [prop] = new JsonArray(o) }, "");

        if (root is JsonObject obj)
        {
            // Pakai properti dari config (externalData); format lama "data" tetap didukung.
            var found = obj[prop] is JsonArray ? prop : obj["data"] is JsonArray ? "data" : null;
            if (found != null)
            {
                var inner = (JsonArray)obj[found]!;
                var template = obj.DeepClone().AsObject();
                template.Remove(found);
                var rootOrg = JsonHelpers.AsText(JsonHelpers.GetPath(obj, _cfg.RootOrgCodePath));
                if (rootOrg == "") rootOrg = JsonHelpers.Field(obj, _cfg.OrgCodeField);
                return (inner, o =>
                {
                    var b = template.DeepClone().AsObject();
                    b[found] = new JsonArray(o);
                    return b;
                }, rootOrg);
            }

            // Satu order saja di root.
            return (new JsonArray(obj.DeepClone()), o => new JsonObject { [prop] = new JsonArray(o) }, "");
        }

        throw new InvalidOperationException("Format file tidak dikenali.");
    }

    // ---------------------------------------------------------------- verify

    public async Task<int> VerifyAsync(int sampleSuccess)
    {
        var targets = _ledger.Entries.Values.Where(e => e.Status == Status.SentUnconfirmed).ToList();
        var sample = _ledger.Entries.Values.Where(e => e.Status == Status.Success)
            .OrderBy(_ => Random.Shared.Next()).Take(sampleSuccess).ToList();

        Console.WriteLine($"Verifikasi {targets.Count} order SENT_UNCONFIRMED dan {sample.Count} sampel SUCCESS");
        Console.WriteLine(new string('-', 78));

        foreach (var e in targets)
        {
            var q = await _ys.QueryAsync(e.Shopcode, e.Tid);
            if (q.Found)
            {
                MarkSuccess(e, q.Id, "Dikonfirmasi lewat verify");
                Console.WriteLine($"{e.Tid,-28} ditemukan, jadi SUCCESS (ID {q.Id})");
            }
            else if (q.Error != null)
            {
                e.LastMessage = "Verify gagal: " + q.Error;
                Console.WriteLine($"{e.Tid,-28} query gagal, tetap SENT_UNCONFIRMED");
            }
            else
            {
                e.LastMessage = "Tidak ditemukan saat verify; aman dikirim ulang pada push berikutnya";
                Console.WriteLine($"{e.Tid,-28} TIDAK ditemukan, akan dikirim ulang pada push berikutnya");
            }
            _ledger.Save();
        }

        var mismatch = 0;
        foreach (var e in sample)
        {
            var q = await _ys.QueryAsync(e.Shopcode, e.Tid);
            var ok = q.Found && (string.IsNullOrEmpty(e.YonsuiteId) || string.IsNullOrEmpty(q.Id) || q.Id == e.YonsuiteId);
            if (!ok) mismatch++;
            Console.WriteLine($"{e.Tid,-28} sampel SUCCESS: {(ok ? "cocok" : $"PERLU DICEK (found={q.Found}, id={q.Id}, error={q.Error})")}");
        }

        Console.WriteLine();
        Report();
        return mismatch == 0 ? 0 : 2;
    }

    // ---------------------------------------------------------------- report

    public int Report()
    {
        var all = _ledger.Entries.Values.ToList();
        Console.WriteLine($"Ledger: {all.Count} order");
        foreach (var g in all.GroupBy(e => e.Status).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-20} {g.Count(),6}");

        Console.WriteLine("Per toko:");
        foreach (var g in all.GroupBy(e => e.Shopcode).OrderBy(g => g.Key))
        {
            var detail = string.Join(", ", g.GroupBy(e => e.Status).OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Count()}"));
            Console.WriteLine($"  {g.Key,-8} {detail}");
        }

        var csvPath = Path.Combine(Path.GetDirectoryName(_cfg.LedgerPath) ?? ".", "ledger_export.csv");
        var sb = new StringBuilder();
        sb.AppendLine("shopcode,tid,status,yonsuite_id,attempts,last_attempt_at,confirmed_at,source_file,last_message,raw_message");
        foreach (var e in all.OrderBy(e => e.Shopcode).ThenBy(e => e.Tid))
        {
            sb.AppendLine(string.Join(",", new[]
            {
                e.Shopcode, e.Tid, e.Status, e.YonsuiteId, e.Attempts.ToString(),
                e.LastAttemptAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                e.ConfirmedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                e.SourceFile, e.LastMessage, e.RawMessage
            }.Select(Csv)));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath))!);
        File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"Ekspor: {csvPath}");
        return 0;
    }

    // --------------------------------------------------------------- helpers

    private static void PrintSummary(int total, SortedDictionary<string, int> counts)
    {
        Console.WriteLine(new string('-', 78));
        Console.WriteLine("Ringkasan run ini:");
        foreach (var kv in counts) Console.WriteLine($"  {kv.Key,-28} {kv.Value,6}");
        var sum = counts.Values.Sum();
        Console.WriteLine($"  {"TOTAL",-28} {sum,6}  (isi file: {total}) {(sum == total ? "KLOP" : "TIDAK KLOP, cek!")}");
    }

    private void WriteLog(string tid, int attempt, JsonObject body, ImportResult r)
    {
        Directory.CreateDirectory(_cfg.LogDir);
        var log = new JsonObject
        {
            ["time"] = DateTimeOffset.Now.ToString("O"),
            ["tid"] = tid,
            ["attempt"] = attempt,
            ["request"] = body.DeepClone(),
            ["http"] = r.Http,
            ["noResponse"] = r.NoResponse,
            ["notSent"] = r.NotSent,
            ["error"] = r.Error,
            ["response"] = JsonHelpers.TryParse(r.Text) ?? JsonValue.Create(r.Text)
        };
        var path = Path.Combine(_cfg.LogDir, $"{SafeName(tid)}_try{attempt}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(path, log.ToJsonString(JsonHelpers.Pretty), Encoding.UTF8);
    }

    private static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrEmpty(clean) ? "tanpa-tid" : clean;
    }

    private static string Csv(string? s)
    {
        s ??= "";
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
