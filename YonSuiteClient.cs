using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace B2cPusher;

public sealed class ImportResult
{
    /// <summary>True kalau tidak ada respons sama sekali (timeout, koneksi putus).</summary>
    public bool NoResponse { get; init; }
    /// <summary>True kalau request belum terkirim (misalnya gagal ambil token).</summary>
    public bool NotSent { get; init; }
    public string Error { get; init; } = "";
    public int Http { get; init; }
    public string Text { get; init; } = "";
}

public sealed class QueryResult
{
    public bool Found { get; init; }
    public string Id { get; init; } = "";
    /// <summary>Terisi kalau query sendiri gagal; artinya status order tetap tidak pasti.</summary>
    public string? Error { get; init; }
}

public sealed class YonSuiteClient
{
    private readonly AppConfig _cfg;
    private readonly HttpClient _http;
    private string? _token;
    private DateTimeOffset _tokenExpiry;

    public YonSuiteClient(AppConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds) };
    }

    public async Task<string> GetTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.StaticAccessToken)) return _cfg.StaticAccessToken;
        if (_token != null && DateTimeOffset.Now < _tokenExpiry) return _token;

        // Self-app auth YonBIP: signature = Base64(HmacSHA256("appKey{key}timestamp{ts}", secret))
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var toSign = $"appKey{_cfg.AppKey}timestamp{ts}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_cfg.AppSecret));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(toSign)));

        var url = $"{_cfg.TokenUrl}?appKey={Uri.EscapeDataString(_cfg.AppKey)}" +
                  $"&timestamp={ts}&signature={Uri.EscapeDataString(signature)}";

        var text = await _http.GetStringAsync(url);
        var json = JsonHelpers.TryParse(text) as JsonObject
                   ?? throw new InvalidOperationException("Respons token bukan JSON: " + JsonHelpers.Truncate(text, 300));

        var data = json["data"] as JsonObject;
        var token = JsonHelpers.AsText(data?["access_token"]);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Gagal ambil token: " + JsonHelpers.Truncate(text, 300));

        var expireSec = int.TryParse(JsonHelpers.AsText(data?["expire"]), out var e) ? e : 3600;
        _token = token;
        // Minta token baru 5 menit sebelum kedaluwarsa.
        _tokenExpiry = DateTimeOffset.Now.AddSeconds(Math.Max(60, expireSec - 300));
        return token;
    }

    public void InvalidateToken() => _token = null;

    /// <summary>Waktu token diminta ulang (5 menit sebelum kedaluwarsa). Null kalau memakai token statis.</summary>
    public DateTimeOffset? TokenRefreshAt => _token == null ? null : _tokenExpiry;

    public async Task<ImportResult> ImportAsync(string body)
    {
        var result = await PostAsync(_cfg.ImportPath, body);
        if (!result.NoResponse && !result.NotSent && result.Http == (int)HttpStatusCode.Unauthorized
            && string.IsNullOrWhiteSpace(_cfg.StaticAccessToken))
        {
            InvalidateToken();
            result = await PostAsync(_cfg.ImportPath, body);
        }
        return result;
    }

    public async Task<QueryResult> QueryAsync(string shopcode, string tid)
    {
        var body = _cfg.QueryBodyTemplate
            .Replace("{shopcode}", JsonEncodedText.Encode(shopcode).ToString())
            .Replace("{tid}", JsonEncodedText.Encode(tid).ToString());

        var r = await PostAsync(_cfg.QueryPath, body);
        if (r.NoResponse || r.NotSent) return new QueryResult { Error = "query gagal: " + r.Error };
        if (r.Http >= 400) return new QueryResult { Error = $"query HTTP {r.Http}: {JsonHelpers.Truncate(r.Text, 200)}" };

        var json = JsonHelpers.TryParse(r.Text) as JsonObject;
        if (json is null) return new QueryResult { Error = "respons query bukan JSON" };

        // Cari tid hanya di bagian "data", supaya parameter yang mungkin dipantulkan balik
        // (partParam) tidak terbaca sebagai "ditemukan".
        var match = JsonHelpers.FindObjectWith(json["data"], _cfg.TidField, tid);
        return match is null
            ? new QueryResult { Found = false }
            : new QueryResult { Found = true, Id = JsonHelpers.AsText(match["id"]) };
    }

    private async Task<ImportResult> PostAsync(string path, string body)
    {
        string token;
        try
        {
            token = await GetTokenAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Request utama belum terkirim sama sekali.
            return new ImportResult { NotSent = true, Error = "gagal ambil token: " + ex.Message };
        }

        try
        {
            var url = $"{_cfg.GatewayUrl.TrimEnd('/')}{path}?access_token={Uri.EscapeDataString(token)}";
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content);
            var text = await resp.Content.ReadAsStringAsync();
            return new ImportResult { Http = (int)resp.StatusCode, Text = text };
        }
        catch (TaskCanceledException)
        {
            return new ImportResult { NoResponse = true, Error = $"timeout {_cfg.TimeoutSeconds} detik" };
        }
        catch (HttpRequestException ex)
        {
            return new ImportResult { NoResponse = true, Error = ex.Message };
        }
    }
}
