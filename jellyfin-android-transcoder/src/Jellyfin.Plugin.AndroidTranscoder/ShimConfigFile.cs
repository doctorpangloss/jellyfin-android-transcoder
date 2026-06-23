namespace Jellyfin.Plugin.AndroidTranscoder;

public sealed record ShimConfigFile(
    bool Enabled,
    string AndroidBaseUrl,
    string Token,
    string RealFfmpegPath,
    string RealFfprobePath,
    int MaxBitrate,
    bool UseHardwareCodecs);
