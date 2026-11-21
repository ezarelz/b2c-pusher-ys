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
          dotnet run -- push --file <file.json>           push semua order di file
          dotnet run -- push --file <file.json> --dry-run validasi + tulis payload, tanpa kirim
          dotnet run -- verify [--sample 5]               cek SENT_UNCONFIRMED (+ sampel SUCCESS) lewat query
          dotnet run -- report                            ringkasan ledger + ekspor CSV
        """);
}
