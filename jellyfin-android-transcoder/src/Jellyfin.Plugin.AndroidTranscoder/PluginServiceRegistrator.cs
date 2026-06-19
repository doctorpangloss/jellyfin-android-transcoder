using MediaBrowser.Controller;
using MediaBrowser.Controller.Extensions;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AndroidTranscoder;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        Environment.SetEnvironmentVariable("JELLYFIN_" + ConfigurationExtensions.FfmpegPathKey.ToUpperInvariant(), null);
        serviceCollection.AddHostedService<StartupShimInstaller>();
    }
}
