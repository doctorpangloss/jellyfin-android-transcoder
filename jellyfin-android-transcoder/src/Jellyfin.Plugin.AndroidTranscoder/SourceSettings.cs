using System.Security.Cryptography;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.AndroidTranscoder;

internal static class SourceSettings
{
    public static bool Ensure(PluginConfiguration config, IConfigurationManager configurationManager, string? requestBaseUrl = null)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(config.SourceSecret))
        {
            config.SourceSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(config.JellyfinBaseUrl))
        {
            var inferred = PublishedServerUrl(configurationManager) ?? NormalizeBaseUrl(requestBaseUrl);
            if (!string.IsNullOrWhiteSpace(inferred))
            {
                config.JellyfinBaseUrl = inferred;
                changed = true;
            }
        }

        return changed;
    }

    private static string? PublishedServerUrl(IConfigurationManager configurationManager)
    {
        var network = configurationManager.GetNetworkConfiguration();
        foreach (var raw in network.PublishedServerUriBySubnet ?? [])
        {
            var candidate = ExtractPublishedUri(raw);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return NormalizeBaseUrl(network.BaseUrl);
    }

    private static string? ExtractPublishedUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        var separator = value.LastIndexOf('=');
        if (separator >= 0 && separator < value.Length - 1)
        {
            value = value[(separator + 1)..].Trim();
        }

        return NormalizeBaseUrl(value);
    }

    private static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return trimmed;
    }
}
