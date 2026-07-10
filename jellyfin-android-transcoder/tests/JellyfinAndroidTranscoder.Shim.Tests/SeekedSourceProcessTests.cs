using Jellyfin.Plugin.AndroidTranscoder;

namespace JellyfinAndroidTranscoder.Shim.Tests;

public sealed class SeekedSourceProcessTests
{
    [Fact]
    public void SeekedSourcePreservesOriginalTimelineForLocalAudioAlignment()
    {
        var args = AndroidTranscoderController.BuildSeekedSourceArguments(
            "/media/movies/Inception.mkv",
            "00:01:21.000");

        Assert.Contains("-copyts", args);
        AssertOption(args, "-ss", "00:01:21.000");
        AssertOption(args, "-avoid_negative_ts", "disabled");
        AssertOption(args, "-i", "file:/media/movies/Inception.mkv");
    }

    private static void AssertOption(IReadOnlyList<string> args, string option, string value)
    {
        var index = Array.IndexOf(args.ToArray(), option);
        Assert.True(index >= 0, $"Expected {option} in {string.Join(' ', args)}");
        Assert.True(index + 1 < args.Count, $"Expected a value after {option}");
        Assert.Equal(value, args[index + 1]);
    }
}
