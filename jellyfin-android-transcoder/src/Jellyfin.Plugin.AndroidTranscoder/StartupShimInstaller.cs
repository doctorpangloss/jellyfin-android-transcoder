using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AndroidTranscoder;

public sealed class StartupShimInstaller : IHostedService
{
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IConfiguration _startupConfiguration;
    private readonly ILogger<StartupShimInstaller> _logger;

    public StartupShimInstaller(
        IServerConfigurationManager configurationManager,
        IConfiguration startupConfiguration,
        ILogger<StartupShimInstaller> logger)
    {
        _configurationManager = configurationManager;
        _startupConfiguration = startupConfiguration;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogWarning("Android Transcoder startup skipped because the plugin instance is unavailable.");
            return Task.CompletedTask;
        }

        var config = plugin.Configuration;

        try
        {
            ShimInstaller.Install(config);

            var options = _configurationManager.GetEncodingOptions();
            options.EncoderAppPath = config.ShimPath;
            options.EncoderAppPathDisplay = config.ShimPath;
            _configurationManager.SaveConfiguration("encoding", options);

            _startupConfiguration[MediaBrowser.Controller.Extensions.ConfigurationExtensions.FfmpegPathKey] = string.Empty;
            _logger.LogInformation("Android Transcoder configured Jellyfin FFmpeg path to {ShimPath}", config.ShimPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Android Transcoder failed to install or configure the FFmpeg shim.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
