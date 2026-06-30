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

        var inferred = InferredBaseUrl(configurationManager, requestBaseUrl);
        if (string.IsNullOrWhiteSpace(config.JellyfinBaseUrl))
        {
            if (!string.IsNullOrWhiteSpace(inferred))
            {
                config.JellyfinBaseUrl = inferred;
                changed = true;
            }
        }
        else if (ShouldReplace(config.JellyfinBaseUrl, inferred))
        {
            config.JellyfinBaseUrl = inferred!;
            changed = true;
        }

        return changed;
    }

    public static string? NormalizeBaseUrl(string? value)
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

    private static string? InferredBaseUrl(IConfigurationManager configurationManager, string? requestBaseUrl)
    {
        var network = configurationManager.GetNetworkConfiguration();
        var candidates = (network.PublishedServerUriBySubnet ?? [])
            .Select(ExtractPublishedUri)
            .Append(NormalizeBaseUrl(requestBaseUrl))
            .Append(NormalizeBaseUrl(network.BaseUrl))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return candidates.Where(IsHttpUrl).FirstOrDefault(IsPublicHost) ??
               candidates.Where(IsHttpUrl).FirstOrDefault(candidate => !IsIpHost(candidate)) ??
               candidates.FirstOrDefault(IsHttpUrl) ??
               candidates.FirstOrDefault(IsPublicHost) ??
               candidates.FirstOrDefault(candidate => !IsIpHost(candidate)) ??
               candidates.FirstOrDefault();
    }

    private static bool ShouldReplace(string current, string? inferred)
    {
        if (string.IsNullOrWhiteSpace(inferred))
        {
            return false;
        }

        if (IsPublicHost(current))
        {
            return false;
        }

        if (IsHttpUrl(current) && !IsHttpUrl(inferred))
        {
            return false;
        }

        return IsPublicHost(inferred) || (IsIpHost(current) && !IsIpHost(inferred));
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttp;

    private static bool IsIpHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return System.Net.IPAddress.TryParse(uri.Host, out _);
    }

    private static bool IsPublicHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return !System.Net.IPAddress.TryParse(uri.Host, out _) &&
               !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
               uri.Host.Contains('.', StringComparison.Ordinal);
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
}
