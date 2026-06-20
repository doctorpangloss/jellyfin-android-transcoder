using JellyfinAndroidTranscoder.Shim;

namespace JellyfinAndroidTranscoder.Shim.Tests;

public sealed class FfmpegCommandTests
{
    [Fact]
    public void ParsesJellyfinTranscodeCommand()
    {
        string[] args =
        [
            "-analyzeduration", "200M", "-probesize", "1G", "-i",
            "file:/media/movies/The Fall (2006)/The Fall (2006) Remux-2160p.mkv",
            "-map_metadata", "-1", "-map_chapters", "-1", "-threads", "0",
            "-map", "0:0", "-map", "0:1", "-map", "-0:s",
            "-codec:v:0", "libx264", "-preset", "veryfast", "-crf", "23",
            "-maxrate", "5616000", "-bufsize", "11232000", "-profile:v:0", "high",
            "-level", "51", "-force_key_frames:0", "expr:gte(t,n_forced*3)",
            "-sc_threshold:v:0", "0", "-vf",
            @"setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale=trunc(min(max(iw\,ih*a)\,1920)/2)*2:trunc(ow/a/2)*2,tonemapx=t=bt709",
            "-codec:a:0", "libfdk_aac", "-ac", "2", "-ab", "256000", "-af", "volume=2",
            "-copyts", "-avoid_negative_ts", "disabled", "-max_muxing_queue_size", "2048",
            "-f", "hls", "-hls_time", "3", "-hls_segment_type", "fmp4",
            "-hls_segment_filename", "/cache/transcodes/id%d.mp4", "-y", "/cache/transcodes/id.m3u8"
        ];

        var command = FfmpegCommand.Parse(args);

        Assert.True(command.CanConsiderRouting);
        Assert.Equal("/media/movies/The Fall (2006)/The Fall (2006) Remux-2160p.mkv", command.InputPath);
        Assert.Equal("/cache/transcodes/id.m3u8", command.OutputPath);
        Assert.Equal(5_616_000, command.MaxRate);
        Assert.Equal(11_232_000, command.BufSize);
        Assert.Equal(1920, command.Width);
        Assert.Equal(1080, command.Height);
        Assert.Equal(3, command.GopSeconds);
        Assert.Equal("libfdk_aac", command.AudioCodec);
        Assert.Equal("256000", command.AudioBitrate);
        Assert.Equal("2", command.AudioChannels);
        Assert.Equal("volume=2", command.AudioFilter);
    }

    [Fact]
    public void RejectsCopyRemuxCommands()
    {
        var args = Split("""-i file:/media/tv/show.mkv -map 0:0 -codec:v:0 copy -codec:a:0 copy -f hls -y /cache/transcodes/id.m3u8""");

        var command = FfmpegCommand.Parse(args);

        Assert.False(command.CanConsiderRouting);
    }

    [Fact]
    public void RoutesHevcAndAv1Only()
    {
        var path = Path.GetTempFileName();
        try
        {
            var command = FfmpegCommand.Parse(Split($"""-i file:{path} -codec:v:0 libx264 -f hls -y /cache/transcodes/id.m3u8"""));

            Assert.True(RouteDecision.Decide(command, new MediaProbe("hevc", "yuv420p10le", 3840, 2160, 7200, "bt2020nc", "smpte2084", "bt2020")).Route);
            Assert.True(RouteDecision.Decide(command, new MediaProbe("av1", "yuv420p10le", 3840, 2160, 7200, "bt2020nc", "smpte2084", "bt2020")).Route);
            Assert.False(RouteDecision.Decide(command, new MediaProbe("h264", "yuv420p", 1920, 1080, 7200, "bt709", "bt709", "bt709")).Route);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string[] Split(string command)
    {
        var result = new List<string>();
        var current = "";
        var escaped = false;
        foreach (var c in command)
        {
            if (escaped)
            {
                current += c;
                escaped = false;
                continue;
            }
            if (c == '\\')
            {
                escaped = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    result.Add(current);
                    current = "";
                }
            }
            else
            {
                current += c;
            }
        }
        if (current.Length > 0)
        {
            result.Add(current);
        }
        return result.ToArray();
    }
}
