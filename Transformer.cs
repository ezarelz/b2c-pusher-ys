using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace B2cPusher;

public sealed class StoreMapEntry
{
    public long StoreId { get; set; }
    public string Shopcode { get; set; } = "";
    /// <summary>Nilai "source" Jubelio yang diharapkan. Kosong = tidak dicek (belum diketahui).</summary>
    public string Source { get; set; } = "";
    /// <summary>"Purbasari" atau "Nerman".</summary>
    public string Brand { get; set; } = "";
}

public sealed class TransformException : Exception
{
    public TransformException(string message) : base(message) { }
}

/// <summary>
/// Contoh acuan (reference) untuk fitur F3 Ecolink:
/// raw JSON order Jubelio (seperti yang tersimpan di Ecolink) -> payload tradevouch/import.
/// Aturan mapping mengikuti keputusan 21 Nov 2026.
/// </summary>
public sealed class Transformer
{
    private static readonly TimeSpan Wib = TimeSpan.FromHours(7);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly AppConfig _cfg;

    public Transformer(AppConfig cfg) => _cfg = cfg;

    public int Run(string input, string output, string tidPrefix)
    {
        var files = Directory.Exists(input)
            ? Directory.GetFiles(input, "*.json").OrderBy(f => f).ToArray()
            : new[] { input };

        var raws = new List<(string File, JsonObject Order)>();
        foreach (var f in files)
        {
            var node = JsonHelpers.TryParse(File.ReadAllText(f));
            if (node is JsonObject o)
            {
                if (o.ContainsKey("externalData"))
                {
                    Console.WriteLine($"Lewati {Path.GetFileName(f)}: ini sudah payload YonSuite, bukan raw Jubelio");
                    continue;
                }
                raws.Add((f, o));
            }
            else if (node is JsonArray arr)
            {
                foreach (var item in arr)
                    if (item is JsonObject io) raws.Add((f, io));
            }
            else
            {
                Console.WriteLine($"Lewati {Path.GetFileName(f)}: bukan JSON yang valid");
            }
        }

        var external = new JsonArray();
        var report = new StringBuilder();
        report.AppendLine("salesorder_no,salesorder_id,store_id,status,hasil,shopcode,grand_total,total_payment,pesan");
        int ok = 0, skipped = 0, failed = 0;

        Console.WriteLine($"Transform {raws.Count} order dari {input}");
        Console.WriteLine(new string('-', 78));

        foreach (var (file, raw) in raws)
        {
            var no = F(raw, "salesorder_no");
            var id = F(raw, "salesorder_id");
            var storeId = F(raw, "store_id");
            var status = F(raw, "status");
            var grand = Dec(raw, "grand_total");

            if (!string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase) || F(raw, "is_canceled") == "true")
            {
                skipped++;
                Console.WriteLine($"{no,-34} DILEWATI: status {status} (hanya COMPLETED yang dikirim)");
                report.AppendLine(Csv(no, id, storeId, status, "SKIPPED", "", Fmt(grand), "", "status bukan COMPLETED"));
                continue;
            }

            try
            {
                var (order, warnings) = Build(raw, tidPrefix);
                external.Add(order);
                ok++;
                var lines = (JsonArray)order["OrderInfo"]!;
                var totalPayment = lines.Sum(l => Dec((JsonObject)l!, "payment"));
                var note = string.Join("; ", warnings);
                Console.WriteLine($"{no,-34} OK -> {F(order, "shopcode")}, payment {Fmt(totalPayment)}{(note == "" ? "" : "  PERINGATAN: " + note)}");
                report.AppendLine(Csv(no, id, storeId, status, warnings.Count == 0 ? "OK" : "OK_DENGAN_PERINGATAN",
                    F(order, "shopcode"), Fmt(grand), Fmt(totalPayment), note));
            }
            catch (TransformException ex)
            {
                failed++;
                Console.WriteLine($"{no,-34} GAGAL: {ex.Message}");
                report.AppendLine(Csv(no, id, storeId, status, "ERROR", "", Fmt(grand), "", ex.Message));
            }
        }

        var payload = new JsonObject
        {
            ["externalData"] = external,
            ["partParam"] = new JsonObject { ["orgCode"] = _cfg.OrgCode }
        };

        var outDir = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        File.WriteAllText(output, payload.ToJsonString(JsonHelpers.Pretty), Encoding.UTF8);
        var reportPath = Path.ChangeExtension(output, ".report.csv");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(true));

        Console.WriteLine(new string('-', 78));
        Console.WriteLine($"OK {ok}, dilewati {skipped}, gagal {failed}  (total {raws.Count})");
        Console.WriteLine($"Payload : {output}");
        Console.WriteLine($"Laporan : {reportPath}");
        return failed == 0 ? 0 : 2;
    }

    private (JsonObject Order, List<string> Warnings) Build(JsonObject raw, string tidPrefix)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // --- Toko: store_id -> shopcode, dicek silang dengan channel dan brand.
        var storeIdText = F(raw, "store_id");
        long.TryParse(storeIdText, NumberStyles.Integer, Inv, out var storeId);
        var map = _cfg.StoreMap.FirstOrDefault(m => m.StoreId == storeId);
        if (map is null)
        {
            errors.Add($"store_id {storeIdText} belum ada di StoreMap");
        }
        else
        {
            var source = F(raw, "source");
            if (map.Source != "" && !string.Equals(map.Source, source, StringComparison.OrdinalIgnoreCase))
                errors.Add($"channel tidak cocok: source '{source}', {map.Shopcode} untuk '{map.Source}'");

            var storeName = F(raw, "store_name");
            if (storeName == "") storeName = F(raw, "store");
            var isNerman = storeName.Contains(_cfg.NermanKeyword, StringComparison.OrdinalIgnoreCase);
            var mapNerman = string.Equals(map.Brand, "Nerman", StringComparison.OrdinalIgnoreCase);
            if (isNerman != mapNerman)
                errors.Add($"brand tidak cocok: toko '{storeName}', {map.Shopcode} untuk brand {map.Brand}");
        }

        // --- Identitas dan waktu.
        var salesorderNo = F(raw, "salesorder_no");
        if (salesorderNo == "") errors.Add("salesorder_no kosong");
        var tid = tidPrefix + salesorderNo;

        var created = ToWib(F(raw, "transaction_date"));
        if (created is null) errors.Add("transaction_date kosong atau tidak valid");
        var payTime = ToWib(F(raw, "payment_date")) ?? created;
        var completed = ToWib(F(raw, "completed_date")) ?? ToWib(F(raw, "last_modified"));

        // --- Nilai: payment = grand_total (keputusan 21 Nov).
        var grand = Dec(raw, "grand_total");
        var postFee = Dec(raw, "shipping_cost");

        var items = (raw["items"] as JsonArray)?
            .OfType<JsonObject>()
            .Where(i => F(i, "is_canceled_item") != "true")
            .ToList() ?? new List<JsonObject>();
        if (items.Count == 0) errors.Add("tidak ada item aktif");

        var lineData = new List<(JsonObject Item, decimal Qty, decimal Price, decimal Amount, string Warehouse)>();
        foreach (var item in items)
        {
            var code = F(item, "item_code");
            if (code == "") errors.Add("item_code kosong");
            var qty = Dec(item, "qty");
            if (qty <= 0) errors.Add($"qty {code} tidak valid");

            var loc = F(item, "loc_name");
            if (loc == "") loc = F(raw, "location_name");
            if (!_cfg.WarehouseMap.TryGetValue(loc, out var wh))
            {
                errors.Add($"lokasi '{loc}' belum ada di WarehouseMap");
                wh = "";
            }
            lineData.Add((item, qty, Dec(item, "price"), Dec(item, "amount"), wh));
        }

        if (errors.Count > 0) throw new TransformException(string.Join("; ", errors.Distinct()));

        // Jumlah payment semua baris + ongkir harus sama dengan grand_total.
        // Selisih (mis. diskon level order / add_disc) dibagi proporsional ke baris.
        var target = grand - postFee;
        var sumAmount = lineData.Sum(l => l.Amount);
        var diff = sumAmount - target;

        // Keputusan 21 Nov: payment = grand_total untuk semua channel, termasuk Shopee
        // (grand_total Shopee sudah dipotong biaya layanan). Selisihnya masuk ke diskon baris tanpa peringatan.

        var payments = new decimal[lineData.Count];
        decimal allocated = 0;
        for (var i = 0; i < lineData.Count; i++)
        {
            decimal share;
            if (i == lineData.Count - 1) share = diff - allocated;
            else share = sumAmount == 0 ? 0 : Math.Round(diff * lineData[i].Amount / sumAmount, 0, MidpointRounding.AwayFromZero);
            allocated += share;
            payments[i] = lineData[i].Amount - share;
            if (payments[i] < 0) throw new TransformException($"payment baris {i + 1} negatif setelah alokasi diskon");
        }

        // --- Penerima: nama penting; wilayah diisi nama kota (master wilayah YonSuite masih China).
        var city = F(raw, "shipping_city").Trim();
        var country = F(raw, "shipping_country");
        if (country == "") country = "Indonesia";
        var phoneRaw = F(raw, "shipping_phone");
        if (phoneRaw == "") phoneRaw = F(raw, "customer_phone");
        var mobile = BuildMobile(phoneRaw, _cfg.PhonePlaceholder);

        var isCod = F(raw, "is_cod") == "true";
        var memo = $"Last updated {completed ?? "-"} WIB|Jubelio {F(raw, "salesorder_id")}|store_id {storeIdText}|{F(raw, "payment_method")}";

        var shop = map!.Shopcode;
        var courier = F(raw, "courier");
        var awb = F(raw, "tracking_no");
        if (awb == "") awb = F(raw, "tracking_number");

        var orderInfo = new JsonArray();
        for (var i = 0; i < lineData.Count; i++)
        {
            var l = lineData[i];
            var gross = l.Price * l.Qty;
            var pay = payments[i];
            var tax = _cfg.TaxMode == "inclusive11"
                ? Math.Round(pay * 11m / 111m, 0, MidpointRounding.AwayFromZero)
                : 0m;

            orderInfo.Add(new JsonObject
            {
                ["tid"] = tid,
                ["oid"] = $"{tid}-{i + 1}",
                ["shopcode"] = shop,
                ["num_iid"] = "",
                ["ebskuid"] = "",
                ["sku_id"] = "",
                ["itemcode"] = F(l.Item, "item_code"),
                ["num"] = Fmt(l.Qty),
                ["price"] = Fmt(l.Price),
                ["dealPrice"] = Fmt(Math.Round(pay / l.Qty, 2)),
                ["total_fee"] = Fmt(gross),
                ["discount_fee"] = Fmt(gross - pay),
                ["adjust_fee"] = "",
                ["payment"] = Fmt(pay),
                ["sub_order_tax_fee"] = Fmt(tax),
                ["warehouseCode"] = l.Warehouse,
                ["warehouseErpCode"] = l.Warehouse,
                ["batchno"] = "",
                ["orderVouchDetailCustomCharacter"] = new JsonObject(),
                ["logistics_company"] = courier,
                ["invoice_no"] = awb
            });
        }

        var order = new JsonObject
        {
            ["plat"] = "Other",
            ["shopcode"] = shop,
            ["store_id"] = shop,
            ["tid"] = tid,
            ["storeorder_id"] = tid,
            ["type"] = isCod ? "COD" : "Online Payment",
            ["status"] = "Shipped",
            ["created"] = created,
            ["pay_time"] = payTime,
            ["buyer_nick"] = F(raw, "customer_name"),
            ["buyer_message"] = "",
            ["seller_memo"] = memo,
            ["receiver_name"] = F(raw, "shipping_full_name"),
            ["receiver_mobile"] = mobile,
            ["receiver_phone"] = "",
            ["receiver_country"] = country,
            ["receiver_state"] = city,
            ["receiver_city"] = city,
            ["receiver_district"] = city,
            ["receiver_town"] = "",
            ["receiver_address"] = F(raw, "shipping_address"),
            ["receiver_zip"] = F(raw, "shipping_post_code"),
            ["post_fee"] = Fmt(postFee),
            ["coupon_ticket"] = "0",
            ["coupon_card"] = "0",
            ["isPromotion"] = "false",
            ["customerCode"] = "",
            ["departmentCode"] = "",
            ["salesmanCode"] = "",
            // Keputusan 21 Nov: tanggal dokumen = tanggal transaksi (mirroring Jubelio).
            ["consign_time"] = created,
            ["shipping_type"] = "",
            ["ys_currency"] = "",
            ["currencyCode"] = "IDR",
            ["OrderInfo"] = orderInfo,
            ["tradeVouchCustomCharacter"] = new JsonObject()
        };

        // HP tersamar -> placeholder adalah perilaku normal (aturan mapping), jadi tidak diberi peringatan.
        if (city == "") warnings.Add("shipping_city kosong, wilayah dikirim kosong");
        return (order, warnings);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Nomor HP penerima (keputusan 21 Nov, opsi C):
    /// - nomor utuh (tanpa '*', minimal 8 digit) dipakai apa adanya (angka saja);
    /// - nomor tersamar marketplace, mis. "(+62)857******15": placeholder yang ujungnya
    ///   diganti digit terakhir yang terlihat (maksimal 4) -> "081234567815";
    /// - tidak ada digit di ujung: placeholder utuh.
    /// </summary>
    public static string BuildMobile(string raw, string placeholder)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (!raw.Contains('*') && digits.Length >= 8) return digits;

        var tail = new string(raw.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        if (tail.Length > 4) tail = tail[^4..];
        if (tail.Length == 0 || tail.Length >= placeholder.Length) return placeholder;
        return placeholder[..^tail.Length] + tail;
    }

    private static string F(JsonObject obj, string field) => JsonHelpers.Field(obj, field);

    private static decimal Dec(JsonObject obj, string field) =>
        decimal.TryParse(JsonHelpers.Field(obj, field), NumberStyles.Any, Inv, out var d) ? d : 0m;

    private static string Fmt(decimal d) =>
        d == decimal.Truncate(d)
            ? decimal.Truncate(d).ToString(Inv)
            : Math.Round(d, 2).ToString("0.##", Inv);

    private static string? ToWib(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        if (!DateTimeOffset.TryParse(iso, Inv, DateTimeStyles.AssumeUniversal, out var d)) return null;
        return d.ToOffset(Wib).ToString("yyyy-MM-dd HH:mm:ss", Inv);
    }

    private static string Csv(params string[] values) =>
        string.Join(",", values.Select(v =>
            v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v));
}
