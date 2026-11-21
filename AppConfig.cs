using System.Text.Json;

namespace B2cPusher;

public sealed class AppConfig
{
    public string GatewayUrl { get; set; } = "http://localhost:5055";
    public string TokenUrl { get; set; } = "http://localhost:5055/open-auth/selfAppAuth/getAccessToken";
    public string AppKey { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string StaticAccessToken { get; set; } = "";

    public string ImportPath { get; set; } = "/yonbip/sd/dst/tradevouch/import";
    public string QueryPath { get; set; } = "/yonbip/sd/dst/tradevouch/query";
    public string QueryBodyTemplate { get; set; } =
        "{\"pageIndex\":1,\"pageSize\":10,\"partParam\":{\"shopcode\":\"{shopcode}\",\"tid\":\"{tid}\"}}";

    public string OrdersArrayProperty { get; set; } = "data";
    public string TidField { get; set; } = "tid";
    public string ShopcodeField { get; set; } = "shopcode";
    public string OrgCodeField { get; set; } = "orgCode";
    public string SuccessMessage { get; set; } = "导入失败0单";

    public List<string> AllowedShopcodes { get; set; } = new();

    public int TimeoutSeconds { get; set; } = 15;
    public int DelayMsBetweenCalls { get; set; } = 300;

    public string LedgerPath { get; set; } = "data/ledger.json";
    public string LogDir { get; set; } = "data/logs";
    public string DryRunDir { get; set; } = "data/dryrun";
    public string CorrectionsPath { get; set; } = "corrections.csv";

    public static AppConfig Load(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var cfg = File.Exists(path)
            ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), options) ?? new AppConfig()
            : new AppConfig();

        // Secret sebaiknya lewat environment variable, bukan file.
        cfg.AppKey = Env("YS_APPKEY") ?? cfg.AppKey;
        cfg.AppSecret = Env("YS_APPSECRET") ?? cfg.AppSecret;
        cfg.StaticAccessToken = Env("YS_TOKEN") ?? cfg.StaticAccessToken;
        cfg.GatewayUrl = Env("YS_GATEWAY") ?? cfg.GatewayUrl;
        return cfg;
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
