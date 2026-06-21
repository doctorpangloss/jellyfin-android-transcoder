namespace Jellyfin.Plugin.AndroidTranscoder;

public sealed class AndroidConnectionConfig
{
    public string? BaseUrl { get; set; }

    public IReadOnlyList<string>? AllBaseUrls { get; set; }

    public string? Token { get; set; }

    public int? MaxBitrate { get; set; }
}
