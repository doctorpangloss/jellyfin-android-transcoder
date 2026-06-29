using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QRCoder;

namespace Jellyfin.Plugin.AndroidTranscoder;

[ApiController]
[Route("AndroidTranscoder")]
public sealed class AndroidTranscoderController : ControllerBase
{
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;

    public AndroidTranscoderController(
        IServerConfigurationManager configurationManager,
        IMediaEncoder mediaEncoder,
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager)
    {
        _configurationManager = configurationManager;
        _mediaEncoder = mediaEncoder;
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
    }

    [HttpGet("Configuration")]
    public ActionResult<PluginConfiguration> GetConfiguration()
    {
        return Plugin.Instance?.Configuration ?? new PluginConfiguration();
    }

    [HttpPost("Configuration")]
    public ActionResult<PluginConfiguration> SaveConfiguration([FromBody] PluginConfiguration config)
    {
        SourceSettings.Ensure(config, _configurationManager, RequestBaseUrl());
        Plugin.Instance?.SaveConfiguration(config);
        ShimInstaller.WriteShimConfig(config);
        return config;
    }

    [HttpPost("Connection")]
    public ActionResult<PluginConfiguration> SaveConnection([FromBody] AndroidConnectionConfig pasted)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin is not initialized");
        var config = plugin.Configuration;
        if (!string.IsNullOrWhiteSpace(pasted.BaseUrl))
        {
            config.AndroidBaseUrl = pasted.BaseUrl.TrimEnd('/');
        }
        if (!string.IsNullOrWhiteSpace(pasted.Token))
        {
            config.Token = pasted.Token;
        }
        if (pasted.MaxBitrate is > 0)
        {
            config.MaxBitrate = pasted.MaxBitrate.Value;
        }
        SourceSettings.Ensure(config, _configurationManager, RequestBaseUrl());
        plugin.SaveConfiguration(config);
        ShimInstaller.WriteShimConfig(config);
        return config;
    }

    [HttpPost("Pairing")]
    public ActionResult<object> GeneratePairing()
    {
        var config = RefreshPairing(force: true);

        return new
        {
            code = config.PairingCode,
            expiresUtc = config.PairingCodeExpiresUtc,
            path = $"/AndroidTranscoder/Pair/{config.PairingCode}"
        };
    }

    [HttpGet("NewPairing")]
    public IActionResult NewPairing()
    {
        _ = RefreshPairing(force: true);
        return Redirect("/AndroidTranscoder/Page");
    }

    [HttpPost("Options")]
    public IActionResult SaveOptions()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin is not initialized");
        var config = plugin.Configuration;
        config.Enabled = Request.Form.ContainsKey("enabled");
        config.UseHardwareCodecs = Request.Form.ContainsKey("hardware");
        SourceSettings.Ensure(config, _configurationManager, RequestBaseUrl());
        plugin.SaveConfiguration(config);
        ShimInstaller.WriteShimConfig(config);
        return Redirect("/AndroidTranscoder/Page");
    }

    [HttpPost("SetupUrl")]
    public IActionResult SaveSetupUrl()
    {
        var setupUrl = Request.Form["setupUrl"].ToString();
        if (!TryParseSetupUrl(setupUrl, out var androidBaseUrl, out var token))
        {
            return Redirect("/AndroidTranscoder/Page?setupError=1");
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin is not initialized");
        var config = plugin.Configuration;
        config.AndroidBaseUrl = androidBaseUrl;
        config.Token = token;
        config.Enabled = true;
        SourceSettings.Ensure(config, _configurationManager, RequestBaseUrl());
        plugin.SaveConfiguration(config);
        ShimInstaller.WriteShimConfig(config);
        return Redirect("/AndroidTranscoder/Page");
    }

    [HttpGet("Page")]
    public async Task<IActionResult> Page()
    {
        var config = RefreshPairing();
        var status = await ConnectionStatus(config);
        var pairingUrl = $"{RequestBaseUrl()}/AndroidTranscoder/Pair/{config.PairingCode}";
        var html = RenderPage(config, pairingUrl, status);
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        return Content(html, "text/html", Encoding.UTF8);
    }

    [HttpGet("PairingQr.svg")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PairingQr()
    {
        var config = RefreshPairing();
        var pairingUrl = $"{RequestBaseUrl()}/AndroidTranscoder/Pair/{config.PairingCode}";
        var status = await ConnectionStatus(config);

        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(pairingUrl, QRCodeGenerator.ECCLevel.Q);
        var svg = RenderPairingSvg(qrData, pairingUrl, config.PairingCode, status);
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        return Content(svg, "image/svg+xml", Encoding.UTF8);
    }

    [HttpGet("RefreshStatus")]
    public IActionResult RefreshStatus()
    {
        return Redirect("/AndroidTranscoder/Page");
    }

    [AllowAnonymous]
    [HttpPost("Pair/{code}")]
    public async Task<ActionResult<object>> Pair(string code, [FromBody] AndroidConnectionConfig posted)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin is not initialized");
        var config = plugin.Configuration;
        if (string.IsNullOrWhiteSpace(config.PairingCode) ||
            !string.Equals(config.PairingCode, code, StringComparison.Ordinal) ||
            config.PairingCodeExpiresUtc < DateTime.UtcNow)
        {
            var idempotent = await TryCompleteAlreadyPairedRequest(config, posted);
            if (idempotent is not null)
            {
                return idempotent;
            }

            return Unauthorized(new { ok = false, error = "pairing_code_invalid_or_expired" });
        }

        if (!string.IsNullOrWhiteSpace(posted.Token))
        {
            config.Token = posted.Token;
        }
        else if (string.IsNullOrWhiteSpace(config.Token))
        {
            config.Token = NewToken();
        }
        SourceSettings.Ensure(config, _configurationManager, RequestBaseUrl());

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(posted.BaseUrl))
        {
            candidates.Add(posted.BaseUrl);
        }
        if (posted.AllBaseUrls is not null)
        {
            candidates.AddRange(posted.AllBaseUrls.Where(url => !string.IsNullOrWhiteSpace(url)));
        }

        var reachable = await FirstReachableAndroidUrl(candidates.Distinct(StringComparer.OrdinalIgnoreCase), config.Token);
        if (reachable is null)
        {
            return BadRequest(new { ok = false, error = "no_posted_android_url_reachable_from_jellyfin", candidates });
        }

        config.AndroidBaseUrl = reachable;
        config.Enabled = true;
        if (posted.MaxBitrate is > 0)
        {
            config.MaxBitrate = posted.MaxBitrate.Value;
        }

        plugin.SaveConfiguration(config);
        ShimInstaller.WriteShimConfig(config);

        return PairingSuccess(config);
    }

    [AllowAnonymous]
    [HttpGet("Source/{ticket}")]
    public IActionResult Source(string ticket)
    {
        var config = CurrentConfig();
        if (string.IsNullOrWhiteSpace(config.SourceSecret))
        {
            return Unauthorized();
        }

        var source = TryVerifySourceTicket(ticket, config.SourceSecret);
        if (source is null || source.ExpiresUnixSeconds < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            return Unauthorized();
        }

        var path = Path.GetFullPath(source.Path);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }
        if (!IsAllowedSourcePath(path, config))
        {
            return Forbid();
        }

        return PhysicalFile(path, "application/octet-stream", enableRangeProcessing: true);
    }

    [HttpPost("Test")]
    public async Task<ActionResult<object>> Test()
    {
        var config = CurrentConfig();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{config.AndroidBaseUrl.TrimEnd('/')}/api/v1/status");
        if (!string.IsNullOrWhiteSpace(config.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        }
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();
        return new { ok = response.IsSuccessStatusCode, status = (int)response.StatusCode, body };
    }

    [HttpPost("InstallShim")]
    public ActionResult<object> InstallShim()
    {
        var config = CurrentConfig();
        ShimInstaller.Install(config);
        return new { ok = true, config.ShimPath };
    }

    [HttpPost("ApplyFfmpegPath")]
    public ActionResult<object> ApplyFfmpegPath()
    {
        var config = CurrentConfig();
        return SetFfmpegPath(config.ShimPath);
    }

    [HttpPost("RestoreFfmpegPath")]
    public ActionResult<object> RestoreFfmpegPath()
    {
        var config = CurrentConfig();
        return SetFfmpegPath(config.RealFfmpegPath);
    }

    private ActionResult<object> SetFfmpegPath(string path)
    {
        var options = _configurationManager.GetEncodingOptions();
        options.EncoderAppPath = path;
        options.EncoderAppPathDisplay = path;
        _configurationManager.SaveConfiguration("encoding", options);

        var active = _mediaEncoder.SetFFmpegPath();
        return new
        {
            ok = active,
            ffmpeg = path,
            activeFfmpeg = _mediaEncoder.EncoderPath,
            restartRequired = !active || !string.Equals(_mediaEncoder.EncoderPath, path, StringComparison.Ordinal)
        };
    }

    private PluginConfiguration CurrentConfig()
    {
        return Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin is not initialized");
    }

    private PluginConfiguration RefreshPairing(bool force = false)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin is not initialized");
        var config = plugin.Configuration;
        var changed = false;
        if (string.IsNullOrWhiteSpace(config.Token))
        {
            config.Token = NewToken();
            changed = true;
        }
        SourceSettings.Ensure(config, _configurationManager, RequestBaseUrl());
        if (force ||
            string.IsNullOrWhiteSpace(config.PairingCode) ||
            config.PairingCodeExpiresUtc < DateTime.UtcNow)
        {
            config.PairingCode = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            config.PairingCodeExpiresUtc = DateTime.UtcNow.AddMinutes(15);
            changed = true;
        }
        if (changed)
        {
            plugin.SaveConfiguration(config);
        }
        return config;
    }

    private bool IsAllowedSourcePath(string path, PluginConfiguration config)
    {
        var roots = config.AllowedSourceRoots
            .Concat(_libraryManager.GetVirtualFolders().SelectMany(folder => folder.Locations ?? []))
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return roots.Any(root => IsUnderRoot(path, root));
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
               string.Equals(path, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static SourceTicket? TryVerifySourceTicket(string ticket, string secret)
    {
        var parts = ticket.Split('.', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        try
        {
            var payload = Base64UrlDecode(parts[0]);
            var provided = Base64UrlDecode(parts[1]);
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
            if (!CryptographicOperations.FixedTimeEquals(provided, expected))
            {
                return null;
            }
            return JsonSerializer.Deserialize<SourceTicket>(payload);
        }
        catch
        {
            return null;
        }
    }

    private string RequestBaseUrl()
    {
        var pathBase = Request.PathBase.HasValue ? Request.PathBase.Value : string.Empty;
        return $"{Request.Scheme}://{Request.Host}{pathBase}".TrimEnd('/');
    }

    private async Task<string?> FirstReachableAndroidUrl(IEnumerable<string> urls, string token)
    {
        var client = _httpClientFactory.CreateClient();
        foreach (var raw in urls)
        {
            var baseUrl = raw.Trim().TrimEnd('/');
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var probe = await ProbeAndroid(client, baseUrl, token, timeout.Token);
                if (probe.Connected)
                {
                    return baseUrl;
                }
            }
            catch
            {
                // Try the next address advertised by the phone.
            }
        }

        return null;
    }

    private async Task<ActionResult<object>?> TryCompleteAlreadyPairedRequest(
        PluginConfiguration config,
        AndroidConnectionConfig posted)
    {
        if (!config.Enabled ||
            string.IsNullOrWhiteSpace(config.AndroidBaseUrl) ||
            string.IsNullOrWhiteSpace(config.Token) ||
            !string.Equals(posted.Token, config.Token, StringComparison.Ordinal))
        {
            return null;
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(posted.BaseUrl))
        {
            candidates.Add(posted.BaseUrl);
        }
        if (posted.AllBaseUrls is not null)
        {
            candidates.AddRange(posted.AllBaseUrls.Where(url => !string.IsNullOrWhiteSpace(url)));
        }
        if (candidates.Count == 0)
        {
            candidates.Add(config.AndroidBaseUrl);
        }

        var reachable = await FirstReachableAndroidUrl(candidates.Distinct(StringComparer.OrdinalIgnoreCase), config.Token);
        if (string.IsNullOrWhiteSpace(reachable) ||
            !string.Equals(reachable.TrimEnd('/'), config.AndroidBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return PairingSuccess(config);
    }

    private static object PairingSuccess(PluginConfiguration config)
    {
        return new
        {
            ok = true,
            token = config.Token,
            androidBaseUrl = config.AndroidBaseUrl,
            jellyfinBaseUrl = config.JellyfinBaseUrl,
            maxBitrate = config.MaxBitrate,
            enabled = config.Enabled
        };
    }

    private async Task<string> ConnectionStatus(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.AndroidBaseUrl))
        {
            return "Waiting for phone";
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var client = _httpClientFactory.CreateClient();
            var probe = await ProbeAndroid(client, config.AndroidBaseUrl.TrimEnd('/'), config.Token, timeout.Token);
            return probe.Connected ? "Connected" : probe.Message;
        }
        catch
        {
            return "Phone not reachable";
        }
    }

    private static async Task<AndroidProbeResult> ProbeAndroid(
        HttpClient client,
        string baseUrl,
        string token,
        CancellationToken cancellationToken)
    {
        using (var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v1/status"))
        {
            using var statusResponse = await client.SendAsync(statusRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!statusResponse.IsSuccessStatusCode)
            {
                return new AndroidProbeResult(false, $"Phone status returned {(int)statusResponse.StatusCode}");
            }
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return new AndroidProbeResult(false, "Android token missing");
        }

        using var authRequest = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl.TrimEnd('/')}/api/v1/remoteprocesses/jfat-token-probe");
        authRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var authResponse = await client.SendAsync(authRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return authResponse.StatusCode switch
        {
            HttpStatusCode.NotFound => new AndroidProbeResult(true, "Connected"),
            HttpStatusCode.Unauthorized => new AndroidProbeResult(false, "Phone auth failed (401)"),
            HttpStatusCode.Forbidden => new AndroidProbeResult(false, "Phone auth failed (403)"),
            _ when authResponse.IsSuccessStatusCode => new AndroidProbeResult(true, "Connected"),
            _ => new AndroidProbeResult(false, $"Phone API returned {(int)authResponse.StatusCode}")
        };
    }

    private static string RenderPage(PluginConfiguration config, string pairingUrl, string status)
    {
        var connected = string.Equals(status, "Connected", StringComparison.OrdinalIgnoreCase);
        var statusClass = connected ? "connected" : "error";
        var statusDetail = connected
            ? $"Jellyfin reached {config.AndroidBaseUrl.TrimEnd('/')} with a 1 second health check."
            : string.IsNullOrWhiteSpace(config.AndroidBaseUrl)
                ? "No Android device is configured yet. Scan the QR code with the app."
                : $"Jellyfin could not reach {config.AndroidBaseUrl.TrimEnd('/')} within 1 second.";
        var code = Html(config.PairingCode);
        var escapedStatus = Html(status);
        var escapedDetail = Html(statusDetail);
        var escapedPairingUrl = Html(pairingUrl);
        var escapedAndroidUrl = Html(config.AndroidBaseUrl);
        var enabledChecked = config.Enabled ? " checked" : string.Empty;
        var hardwareChecked = config.UseHardwareCodecs ? " checked" : string.Empty;

        return $$"""
<!DOCTYPE html>
<html>
<head>
  <title>Android Transcoder</title>
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <style>
    body { margin: 0; background: #101418; color: #f5f7fa; font-family: Arial, sans-serif; }
    main { max-width: 920px; margin: 0 auto; padding: 28px; }
    h1 { margin: 0 0 8px; font-size: 32px; }
    p { line-height: 1.45; color: #c8d2dc; }
    .panel { display: grid; grid-template-columns: minmax(240px, 320px) minmax(280px, 1fr); gap: 28px; align-items: center; margin-top: 24px; padding: 24px; border-radius: 8px; background: #1a222b; }
    .qr { width: 280px; max-width: 100%; background: #fff; padding: 12px; border-radius: 8px; box-sizing: border-box; }
    .status { margin: 18px 0; padding: 16px; border-radius: 8px; background: #7f1d1d; }
    .status.connected { background: #0f766e; }
    .status strong { display: block; margin-bottom: 4px; font-size: 22px; color: #fff; }
    .code { margin-top: 12px; font-size: 42px; font-weight: 700; letter-spacing: .08em; font-family: monospace; color: #fff; }
    .actions { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 16px; }
    button, .button { border: 0; border-radius: 8px; padding: 12px 18px; background: #0f766e; color: #fff; font-size: 16px; text-decoration: none; cursor: pointer; }
    .secondary { background: #334155; }
    .settings { margin-top: 24px; padding: 20px; border-radius: 8px; background: #1a222b; }
    label { display: block; margin: 12px 0; color: #dbe5ee; font-size: 17px; }
    input[type="url"] { width: 100%; max-width: 560px; box-sizing: border-box; margin: 8px 0 12px; padding: 12px; border: 1px solid #334155; border-radius: 8px; background: #0f172a; color: #fff; font-size: 16px; }
    input[type="checkbox"] { margin-right: 10px; transform: scale(1.15); }
    code { overflow-wrap: anywhere; }
    @media (max-width: 720px) { .panel { grid-template-columns: 1fr; } main { padding: 18px; } }
  </style>
</head>
<body>
  <main>
    <h1>Android Transcoder</h1>
    <p>Open Android Transcoder on the phone, tap <strong>Pair from QR</strong>, and scan this code. This page tests the configured Android device from Jellyfin each time it renders.</p>
    <section class="panel">
      <div>
        <img class="qr" src="/AndroidTranscoder/PairingQr.svg?code={{code}}" alt="Android Transcoder pairing QR code" />
        <div class="code">{{code}}</div>
      </div>
      <div>
        <div class="status {{statusClass}}">
          <strong>{{escapedStatus}}</strong>
          <span>{{escapedDetail}}</span>
        </div>
        <p>Pairing URL: <br /><code>{{escapedPairingUrl}}</code></p>
        <p>Configured phone: <br /><a href="{{escapedAndroidUrl}}">{{escapedAndroidUrl}}</a></p>
        <div class="actions">
          <form action="/AndroidTranscoder/Page" method="get"><button type="submit">Refresh status</button></form>
          <form action="/AndroidTranscoder/NewPairing" method="get"><button type="submit" class="secondary">New pairing code</button></form>
          <a class="button secondary" href="/web/#/dashboard">Back to Jellyfin</a>
        </div>
      </div>
    </section>
    <section class="settings">
      <h2>Manual setup</h2>
      <p>Paste the one setup URL from the Android app.</p>
      <form action="/AndroidTranscoder/SetupUrl" method="post">
        <input type="url" name="setupUrl" placeholder="http://10.2.0.87:8098/?token=1234" required />
        <button type="submit">Use setup URL</button>
      </form>
    </section>
    <section class="settings">
      <h2>Options</h2>
      <form action="/AndroidTranscoder/Options" method="post">
        <label><input type="checkbox" name="enabled" value="true"{{enabledChecked}} />Use this phone for video transcodes</label>
        <label><input type="checkbox" name="hardware" value="true"{{hardwareChecked}} />Use Android hardware codecs</label>
        <button type="submit">Save options</button>
      </form>
    </section>
  </main>
</body>
</html>
""";
    }

    private static bool TryParseSetupUrl(string value, out string androidBaseUrl, out string token)
    {
        androidBaseUrl = string.Empty;
        token = string.Empty;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        token = query.TryGetValue("token", out var tokenValues) ? tokenValues.ToString() : string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        androidBaseUrl = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }

    private static string RenderPairingSvg(QRCodeData qrData, string pairingUrl, string code, string status)
    {
        const int cell = 6;
        const int quiet = 4;
        var moduleCount = qrData.ModuleMatrix.Count;
        var qrSize = (moduleCount + quiet * 2) * cell;
        var height = qrSize + 76;
        var path = new StringBuilder();

        for (var y = 0; y < moduleCount; y++)
        {
            for (var x = 0; x < moduleCount; x++)
            {
                if (qrData.ModuleMatrix[y][x])
                {
                    path.Append("M")
                        .Append((x + quiet) * cell)
                        .Append(',')
                        .Append((y + quiet) * cell)
                        .Append('h')
                        .Append(cell)
                        .Append('v')
                        .Append(cell)
                        .Append("h-")
                        .Append(cell)
                        .Append('z');
                }
            }
        }

        var escapedUrl = System.Security.SecurityElement.Escape(pairingUrl) ?? string.Empty;
        var escapedCode = System.Security.SecurityElement.Escape(code) ?? string.Empty;
        var escapedStatus = System.Security.SecurityElement.Escape(status) ?? string.Empty;
        return $"""
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {qrSize} {height}" width="{qrSize}" height="{height}" shape-rendering="crispEdges" role="img">
  <title>{escapedUrl}</title>
  <rect width="100%" height="100%" fill="#ffffff"/>
  <path d="{path}" fill="#000000"/>
  <text x="50%" y="{qrSize + 28}" text-anchor="middle" font-family="monospace" font-size="24" font-weight="700" fill="#111827">{escapedCode}</text>
  <text x="50%" y="{qrSize + 54}" text-anchor="middle" font-family="Arial, sans-serif" font-size="16" fill="#374151">{escapedStatus}</text>
</svg>
""";
    }

    private static string NewToken()
    {
        return RandomNumberGenerator.GetInt32(0, 10_000).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Html(string? value)
    {
        return System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

}

public sealed record SourceTicket(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("exp")] long ExpiresUnixSeconds);

internal sealed record AndroidProbeResult(bool Connected, string Message);
