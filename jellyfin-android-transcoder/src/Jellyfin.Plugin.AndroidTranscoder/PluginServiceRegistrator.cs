using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Extensions;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AndroidTranscoder;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        Environment.SetEnvironmentVariable("JELLYFIN_" + MediaBrowser.Controller.Extensions.ConfigurationExtensions.FfmpegPathKey.ToUpperInvariant(), null);
        ReplaceMediaEncoder(serviceCollection);
        serviceCollection.AddHostedService<StartupShimInstaller>();
    }

    private static void ReplaceMediaEncoder(IServiceCollection serviceCollection)
    {
        var descriptor = serviceCollection.LastOrDefault(static service => service.ServiceType == typeof(IMediaEncoder));
        var implementationType = descriptor?.ImplementationType;
        if (descriptor is null || implementationType is null)
        {
            return;
        }

        serviceCollection.Remove(descriptor);
        serviceCollection.AddSingleton(typeof(IMediaEncoder), serviceProvider =>
        {
            var startupConfiguration = serviceProvider.GetRequiredService<IConfiguration>();
            var configurationManager = serviceProvider.GetRequiredService<IServerConfigurationManager>();
            var logger = serviceProvider.GetRequiredService<ILogger<PluginServiceRegistrator>>();
            var config = new PluginConfiguration();

            try
            {
                var source = Path.Combine(
                    Path.GetDirectoryName(typeof(PluginServiceRegistrator).Assembly.Location) ?? config.ShimPath,
                    "shim-payload",
                    "jfat-ffmpeg");

                ShimInstaller.Install(config, source);

                var options = configurationManager.GetEncodingOptions();
                options.EncoderAppPath = config.ShimPath;
                options.EncoderAppPathDisplay = config.ShimPath;
                configurationManager.SaveConfiguration("encoding", options);

                startupConfiguration[MediaBrowser.Controller.Extensions.ConfigurationExtensions.FfmpegPathKey] = string.Empty;
                logger.LogInformation("Android Transcoder prepared Jellyfin FFmpeg path before MediaEncoder initialization: {ShimPath}", config.ShimPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Android Transcoder failed to prepare the FFmpeg shim before MediaEncoder initialization.");
            }

            return ActivatorUtilities.CreateInstance(serviceProvider, implementationType);
        });
    }
}
