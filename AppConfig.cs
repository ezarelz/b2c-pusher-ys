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

    // --- Transformer (raw Jubelio -> payload YonSuite)
    public string RootOrgCodePath { get; set; } = "partParam.orgCode";
    public string OrgCode { get; set; } = "ecolink";
    /// <summary>"zero" = sub_order_tax_fee 0 (seperti payload batch B/C yang sukses); "inclusive11" = payment x 11/111.</summary>
    public string TaxMode { get; set; } = "zero";
    public string PhonePlaceholder { get; set; } = "081234567890";
    public string NermanKeyword { get; set; } = "NERMAN";
    public List<StoreMapEntry> StoreMap { get; set; } = new();
    public Dictionary<string, string> WarehouseMap { get; set; } = new();

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
        // Untuk mencoba format body query tanpa mengedit appsettings.json.
        cfg.QueryBodyTemplate = Env("YS_QUERY_BODY") ?? cfg.QueryBodyTemplate;

        // YS_TOKEN_URL = nilai {{tokenUrl}} di environment Postman (tanpa path).
        var tokenBase = Env("YS_TOKEN_URL");
        if (tokenBase != null) cfg.TokenUrl = tokenBase;
        if (!cfg.TokenUrl.Contains("/open-auth/", StringComparison.OrdinalIgnoreCase))
            cfg.TokenUrl = cfg.TokenUrl.TrimEnd('/') + "/open-auth/selfAppAuth/getAccessToken";
        return cfg;
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
