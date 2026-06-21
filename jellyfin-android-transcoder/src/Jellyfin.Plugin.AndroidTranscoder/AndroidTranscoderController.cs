using System.Net.Http.Headers;
using System.Security.Cryptography;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AndroidTranscoder;

[ApiController]
[Route("AndroidTranscoder")]
public sealed class AndroidTranscoderController : ControllerBase
{
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IHttpClientFactory _httpClientFactory;

    public AndroidTranscoderController(
        IServerConfigurationManager configurationManager,
        IMediaEncoder mediaEncoder,
        IHttpClientFactory httpClientFactory)
    {
        _configurationManager = configurationManager;
        _mediaEncoder = mediaEncoder;
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet("Configuration")]
    public ActionResult<PluginConfiguration> GetConfiguration()
    {
        return Plugin.Instance?.Configuration ?? new PluginConfiguration();
    }

    [HttpPost("Configuration")]
    public ActionResult<PluginConfiguration> SaveConfiguration([FromBody] PluginConfiguration config)
    {
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
        plugin.SaveConfiguration(config);
        ShimInstaller.WriteShimConfig(config);
        return config;
    }

    [HttpPost("Pairing")]
    public ActionResult<object> GeneratePairing()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin is not initialized");
        var config = plugin.Configuration;
        if (string.IsNullOrWhiteSpace(config.Token))
        {
            config.Token = NewToken();
        }
        config.PairingCode = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        config.PairingCodeExpiresUtc = DateTime.UtcNow.AddMinutes(15);
        plugin.SaveConfiguration(config);

        return new
        {
            code = config.PairingCode,
            expiresUtc = config.PairingCodeExpiresUtc,
            path = $"/AndroidTranscoder/Pair/{config.PairingCode}",
            startCommand = "adb shell am start-foreground-service -n com.hiddenswitch.androidtranscoder/.TranscoderService -e pairUrl \"JELLYFIN_URL/AndroidTranscoder/Pair/" + config.PairingCode + "\" --ez startOnBoot true --ez keepAwake true"
        };
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
            return Unauthorized(new { ok = false, error = "pairing_code_invalid_or_expired" });
        }

        if (string.IsNullOrWhiteSpace(config.Token))
        {
            config.Token = NewToken();
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

        var reachable = await FirstReachableAndroidUrl(candidates.Distinct(StringComparer.OrdinalIgnoreCase), config.Token);
        if (reachable is null)
        {
            return BadRequest(new { ok = false, error = "no_posted_android_url_reachable_from_jellyfin", candidates });
        }

        config.AndroidBaseUrl = reachable;
        config.Enabled = true;
        config.PairingCode = string.Empty;
        config.PairingCodeExpiresUtc = default;
        if (posted.MaxBitrate is > 0)
        {
            config.MaxBitrate = posted.MaxBitrate.Value;
        }

        plugin.SaveConfiguration(config);
        ShimInstaller.WriteShimConfig(config);

        return new
        {
            ok = true,
            token = config.Token,
            androidBaseUrl = config.AndroidBaseUrl,
            maxBitrate = config.MaxBitrate,
            enabled = config.Enabled
        };
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
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/status");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.IsSuccessStatusCode)
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

    private static string NewToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    }

}
