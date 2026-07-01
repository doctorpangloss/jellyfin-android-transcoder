using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AndroidTranscoder;

public sealed class AndroidConnectionConfig
{
    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("allBaseUrls")]
    public IReadOnlyList<string>? AllBaseUrls { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("maxBitrate")]
    public int? MaxBitrate { get; set; }
}
