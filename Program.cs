using System.Text;
using B2cPusher;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0)
{
    PrintHelp();
    return 1;
}

var command = args[0].ToLowerInvariant();

if (command == "mock")
{
    await MockServer.RunAsync();
    return 0;
}

var cfg = AppConfig.Load("appsettings.json");
Console.WriteLine($"Target: {cfg.GatewayUrl}");

switch (command)
{
    case "push":
    {
        var file = Arg("--file");
        if (file is null || !File.Exists(file))
        {
            Console.WriteLine("Pakai: dotnet run -- push --file <path.json> [--dry-run]");
            return 1;
        }
        return await new Pusher(cfg).PushFileAsync(file, args.Contains("--dry-run"));
    }
    case "token":
    {
        // Tes koneksi: sama dengan request token di Postman. Token tidak ditampilkan utuh.
        Console.WriteLine($"Token URL: {cfg.TokenUrl}");
        Console.WriteLine($"Sumber   : {(string.IsNullOrWhiteSpace(cfg.StaticAccessToken) ? "appKey/appSecret (diminta otomatis)" : "YS_TOKEN (token statis dari Postman)")}");
        try
        {
            var client = new YonSuiteClient(cfg);
            var token = await client.GetTokenAsync();
            var masked = token.Length <= 8 ? "****" : token[..4] + "..." + token[^4..];
            Console.WriteLine($"Token OK : {masked} ({token.Length} karakter)");
            if (client.TokenRefreshAt is { } t)
                Console.WriteLine($"Diminta ulang otomatis sekitar {t:yyyy-MM-dd HH:mm}");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Console.WriteLine("Gagal ambil token: " + ex.Message);
            return 2;
        }
    }
    case "query":
    {
        // Cek satu order di YonSuite dan tampilkan respons mentah (untuk mencocokkan QueryBodyTemplate).
        var shop = Arg("--shop");
        var tid = Arg("--tid");
        if (shop is null || tid is null)
        {
            Console.WriteLine("Pakai: dotnet run -- query --shop E0001 --tid <tid>");
            return 1;
        }
        var client = new YonSuiteClient(cfg);
        Console.WriteLine("Body    : " + client.BuildQueryBody(shop, tid));
        var raw = await client.QueryRawAsync(shop, tid);
        if (raw.NoResponse || raw.NotSent)
        {
            Console.WriteLine("Gagal   : " + raw.Error);
            return 2;
        }
        Console.WriteLine($"HTTP    : {raw.Http}");
        var pretty = JsonHelpers.TryParse(raw.Text)?.ToJsonString(JsonHelpers.Pretty) ?? raw.Text;
        Console.WriteLine("Respons :");
        Console.WriteLine(JsonHelpers.Truncate(pretty, 4000));
        var q = await client.QueryAsync(shop, tid);
        Console.WriteLine(q.Error != null
            ? "Hasil   : query gagal - " + q.Error
            : q.Found ? $"Hasil   : DITEMUKAN, ID {q.Id}" : "Hasil   : TIDAK ditemukan");
        return 0;
    }
    case "transform":
    {
        var input = Arg("--in");
        if (input is null || (!File.Exists(input) && !Directory.Exists(input)))
        {
            Console.WriteLine("Pakai: dotnet run -- transform --in <folder atau file raw JSON> [--out payload.json] [--tid-prefix TEST-]");
            return 1;
        }
        var output = Arg("--out") ?? Path.Combine("data", "payload", $"payload_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        return new Transformer(cfg).Run(input, output, Arg("--tid-prefix") ?? "");
    }
    case "verify":
    {
        var sample = int.TryParse(Arg("--sample"), out var s) ? s : 0;
        return await new Pusher(cfg).VerifyAsync(sample);
    }
    case "report":
        return new Pusher(cfg).Report();
    default:
        PrintHelp();
        return 1;
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static void PrintHelp()
{
    Console.WriteLine("""
        B2cPusher - push order ke YonSuite tradevouch/import dengan ledger idempotensi

          dotnet run -- mock                              jalankan YonSuite palsu di localhost:5055
          dotnet run -- token                             tes ambil token YonSuite (seperti step 1 Postman)
          dotnet run -- query --shop E0001 --tid <tid>    cek satu order di YonSuite, tampilkan respons mentah
          dotnet run -- transform --in <folder/file>      raw JSON Ecolink -> payload YonSuite
                [--out file.json] [--tid-prefix TEST-]
          dotnet run -- push --file <file.json>           push semua order di file
          dotnet run -- push --file <file.json> --dry-run validasi + tulis payload, tanpa kirim
          dotnet run -- verify [--sample 5]               cek SENT_UNCONFIRMED (+ sampel SUCCESS) lewat query
          dotnet run -- report                            ringkasan ledger + ekspor CSV
        """);
}
