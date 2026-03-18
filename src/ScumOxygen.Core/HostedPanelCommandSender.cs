using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScumOxygen.Core.Models;

namespace ScumOxygen.Core;

public sealed class HostedPanelCommandSender
{
    private static readonly Regex CsrfRegex = new("<meta name=\"CSRF_TOKEN\" content=\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Logger _log;
    private readonly HostedPanelCommandConfig _cfg;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private DateTimeOffset _authenticatedUntil = DateTimeOffset.MinValue;
    private string _csrfToken = string.Empty;

    public HostedPanelCommandSender(Logger log, HostedPanelCommandConfig cfg)
    {
        _log = log;
        _cfg = cfg;

        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = _cookies,
            AutomaticDecompression = DecompressionMethods.All
        };

        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _cfg.TimeoutSeconds))
        };
    }

    public bool PreferPanelTransport => _cfg.PreferPanelTransport;

    public async Task<CommandResult> ExecuteAsync(string command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
            return CommandResult.Fail("empty command", TimeSpan.Zero);

        try
        {
            await EnsureAuthenticatedAsync(ct);

            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("cmd", command)
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl($"/servers/control/sendconsole/{_cfg.ServerId}"));
            request.Headers.Add("X-CSRF-TOKEN", _csrfToken);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = content;

            using var response = await _client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.TryGetProperty("status", out var statusProp)
                ? statusProp.GetString()
                : null;

            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                var error = doc.RootElement.TryGetProperty("error", out var errorProp)
                    ? errorProp.GetString()
                    : "panel command rejected";
                return CommandResult.Fail(error ?? "panel command rejected", TimeSpan.Zero);
            }

            var success = doc.RootElement.TryGetProperty("success", out var successProp)
                ? successProp.GetString()
                : "hosted-panel";

            return CommandResult.Ok(success ?? "hosted-panel", TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(ex.GetBaseException().Message, TimeSpan.Zero);
        }
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _authenticatedUntil)
            return;

        await _authLock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow < _authenticatedUntil)
                return;

            var landingHtml = await _client.GetStringAsync(BuildUrl("/"), ct);
            var csrf = CsrfRegex.Match(landingHtml).Groups[1].Value;
            if (string.IsNullOrWhiteSpace(csrf))
                throw new InvalidOperationException("ARK panel CSRF token not found");
            _csrfToken = csrf;

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl("/account/login/ajax"));
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("email", _cfg.Email),
                new KeyValuePair<string, string>("password", _cfg.Password),
                new KeyValuePair<string, string>("google_code_auth", string.Empty),
                new KeyValuePair<string, string>("remember_auth_user", "on")
            });

            using var response = await _client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.TryGetProperty("status", out var statusProp)
                ? statusProp.GetString()
                : null;

            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                var error = doc.RootElement.TryGetProperty("error", out var errorProp)
                    ? errorProp.GetString()
                    : "ARK panel login failed";
                throw new InvalidOperationException(error ?? "ARK panel login failed");
            }

            _authenticatedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
            _log.Info($"[HostedPanel] Authenticated for server {_cfg.ServerId}");
        }
        finally
        {
            _authLock.Release();
        }
    }

    private string BuildUrl(string relativePath)
    {
        var baseUrl = _cfg.PanelBaseUrl?.TrimEnd('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Hosted panel base URL is empty");

        return baseUrl + relativePath;
    }
}

public sealed class HostedPanelCommandConfig
{
    public bool Enabled { get; set; } = false;
    public bool PreferPanelTransport { get; set; } = false;
    public string PanelBaseUrl { get; set; } = "https://panel.ark-hoster.ru";
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 20;

    public static HostedPanelCommandConfig Load(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            var cfg = new HostedPanelCommandConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            return cfg;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<HostedPanelCommandConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new HostedPanelCommandConfig();
    }
}
