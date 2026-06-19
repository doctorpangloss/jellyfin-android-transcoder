using System.Net.Http.Headers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
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

}
