using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
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
        WriteShimConfig(config);
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
        WriteShimConfig(config);
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
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin is not initialized");
        var source = Path.Combine(Path.GetDirectoryName(plugin.AssemblyFilePath) ?? plugin.DataFolderPath, "shim-payload", "jfat-ffmpeg");
        if (!System.IO.File.Exists(source))
        {
            return BadRequest(new { ok = false, error = $"Shim payload not found at {source}. Publish JellyfinAndroidTranscoder.Shim and copy jfat-ffmpeg into shim-payload." });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(config.ShimPath)!);
        System.IO.File.Copy(source, config.ShimPath, overwrite: true);
        MakeExecutable(config.ShimPath);
        WriteShimConfig(config);
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

    private static void WriteShimConfig(PluginConfiguration config)
    {
        var shimConfig = new ShimConfigFile(
            config.Enabled,
            config.AndroidBaseUrl,
            config.Token,
            config.RealFfmpegPath,
            config.RealFfprobePath,
            config.MaxBitrate);
        var path = Path.Combine(Path.GetDirectoryName(config.ShimPath)!, "shim-config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(shimConfig, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            System.IO.File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return;
        }

        using var process = Process.Start("chmod", ["755", path]);
        process?.WaitForExit();
    }
}
