using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AndroidTranscoder;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; }

    public string AndroidBaseUrl { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public string RealFfmpegPath { get; set; } = "/usr/lib/jellyfin-ffmpeg/ffmpeg";

    public string RealFfprobePath { get; set; } = "/usr/lib/jellyfin-ffmpeg/ffprobe";

    public string ShimPath { get; set; } = "/config/plugins/Jellyfin.Plugin.AndroidTranscoder/shim/jfat-ffmpeg";

    public int MaxBitrate { get; set; } = 6_000_000;

    public bool UseHardwareCodecs { get; set; } = true;

    public string JellyfinBaseUrl { get; set; } = string.Empty;

    public string SourceSecret { get; set; } = string.Empty;

    public string[] AllowedSourceRoots { get; set; } = [];

    public string PairingCode { get; set; } = string.Empty;

    public DateTime PairingCodeExpiresUtc { get; set; }
}
