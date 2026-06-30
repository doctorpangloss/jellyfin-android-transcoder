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
        Assert.NotNull(command.Tonemapx);
        Assert.Equal("bt2390", command.Tonemapx.Algorithm);
        Assert.Equal("bt709", command.Tonemapx.Transfer);
        Assert.Equal("bt709", command.Tonemapx.Matrix);
        Assert.Equal("bt709", command.Tonemapx.Primaries);
        Assert.Equal("tv", command.Tonemapx.Range);
        Assert.Equal("same", command.Tonemapx.Format);
        Assert.Equal("0", command.Tonemapx.Desat);
        Assert.Null(command.Tonemapx.Peak);
    }

    [Fact]
    public void RejectsCopyRemuxCommands()
    {
        var args = Split("""-i file:/media/tv/show.mkv -map 0:0 -codec:v:0 copy -codec:a:0 copy -f hls -y /cache/transcodes/id.m3u8""");

        var command = FfmpegCommand.Parse(args);

        Assert.False(command.CanConsiderRouting);
    }

    [Fact]
    public void ParsesBrowserFmp4TranscodeCommand()
    {
        string[] args =
        [
            "-analyzeduration", "200M", "-probesize", "1G", "-i",
            "file:/media/movies/HEVC Feature (2026)/HEVC Feature Remux-2160p.mkv",
            "-map_metadata", "-1", "-map_chapters", "-1", "-threads", "0",
            "-map", "0:0", "-map", "-0:s", "-codec:v:0", "libx264",
            "-preset", "veryfast", "-crf", "23",
            "-maxrate", "63810668", "-bufsize", "127621336", "-profile:v:0", "high",
            "-level", "51", "-force_key_frames:0", "expr:gte(t,n_forced*3)",
            "-sc_threshold:v:0", "0", "-vf",
            @"setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale=trunc(min(max(iw\,ih*a)\,min(3840\,2160*a))/2)*2:trunc(min(max(iw/a\,ih)\,min(3840/a\,2160))/2)*2,tonemapx=t=bt709",
            "-copyts", "-avoid_negative_ts", "disabled", "-max_muxing_queue_size", "2048",
            "-f", "hls", "-max_delay", "5000000", "-hls_time", "3",
            "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "70e040ca627b1a5a2ecb0618aa77f67c-1.mp4",
            "-start_number", "0", "-hls_segment_filename", "/cache/transcodes/70e040ca627b1a5a2ecb0618aa77f67c%d.mp4",
            "-hls_playlist_type", "vod", "-hls_list_size", "0",
            "-hls_segment_options", "movflags=+frag_discont",
            "-y", "/cache/transcodes/70e040ca627b1a5a2ecb0618aa77f67c.m3u8"
        ];

        var command = FfmpegCommand.Parse(args);

        Assert.True(command.CanConsiderRouting);
        Assert.Equal("/media/movies/HEVC Feature (2026)/HEVC Feature Remux-2160p.mkv", command.InputPath);
        Assert.Equal("/cache/transcodes/70e040ca627b1a5a2ecb0618aa77f67c.m3u8", command.OutputPath);
        Assert.Equal(63_810_668, command.MaxRate);
        Assert.Equal(127_621_336, command.BufSize);
        Assert.Equal(3840, command.Width);
        Assert.Equal(2160, command.Height);
        Assert.Equal("fmp4", command.ValueAfter("-hls_segment_type"));
        Assert.Equal("70e040ca627b1a5a2ecb0618aa77f67c-1.mp4", command.ValueAfter("-hls_fmp4_init_filename"));
        Assert.Equal("movflags=+frag_discont", command.ValueAfter("-hls_segment_options"));
    }

    [Fact]
    public void ParsesForcedPgsOverlayCommandAsRoutable()
    {
        string[] args =
        [
            "-analyzeduration", "200M", "-probesize", "1G", "-i",
            "file:/media/movies/Feature (2026)/Feature (2026) Remux-2160p.mkv",
            "-map_metadata", "-1", "-map_chapters", "-1", "-threads", "0",
            "-map", "0:0", "-map", "0:1", "-map", "-0:0",
            "-codec:v:0", "libx264", "-preset", "veryfast", "-crf", "23",
            "-maxrate", "5616000", "-bufsize", "11232000", "-profile:v:0", "high",
            "-level", "51", "-force_key_frames:0", "expr:gte(t,n_forced*3)",
            "-sc_threshold:v:0", "0", "-filter_complex",
            @"[0:3]scale,scale=-1:1080:fast_bilinear,crop,pad=max(1920\,iw):max(1080\,ih):(ow-iw)/2:(oh-ih)/2:black@0,crop=1920:1080[sub];[0:0]setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale=trunc(min(max(iw\,ih*a)\,1920)/2)*2:trunc(ow/a/2)*2,tonemapx=tonemap=bt2390:desat=0:peak=100:t=bt709:m=bt709:p=bt709:format=yuv420p[main];[main][sub]overlay=eof_action=pass:repeatlast=0",
            "-start_at_zero", "-codec:a:0", "libfdk_aac", "-ac", "2", "-ab", "256000", "-af", "volume=2",
            "-copyts", "-avoid_negative_ts", "disabled", "-max_muxing_queue_size", "2048",
            "-f", "hls", "-max_delay", "5000000", "-hls_time", "3",
            "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "forcedpgs-1.mp4",
            "-start_number", "0", "-hls_segment_filename", "/cache/transcodes/forcedpgs%d.mp4",
            "-hls_playlist_type", "vod", "-hls_list_size", "0",
            "-hls_segment_options", "movflags=+frag_discont",
            "-y", "/cache/transcodes/forcedpgs.m3u8"
        ];

        var command = FfmpegCommand.Parse(args);

        Assert.True(command.CanConsiderRouting);
        Assert.Equal("/media/movies/Feature (2026)/Feature (2026) Remux-2160p.mkv", command.InputPath);
        Assert.Equal("/cache/transcodes/forcedpgs.m3u8", command.OutputPath);
        Assert.Equal(5_616_000, command.MaxRate);
        Assert.Equal("fmp4", command.ValueAfter("-hls_segment_type"));
        Assert.Contains("overlay=eof_action=pass", command.ValueAfter("-filter_complex"));
        Assert.NotNull(command.Tonemapx);
        Assert.Equal("bt2390", command.Tonemapx.Algorithm);
        Assert.Equal("0", command.Tonemapx.Desat);
        Assert.Equal("100", command.Tonemapx.Peak);
        Assert.Equal("bt709", command.Tonemapx.Transfer);
        Assert.Equal("bt709", command.Tonemapx.Matrix);
        Assert.Equal("bt709", command.Tonemapx.Primaries);
        Assert.Equal("yuv420p", command.Tonemapx.Format);
    }

    [Fact]
    public void NormalizesJellyfinSingleArgumentCommandLineBeforeRouting()
    {
        var raw = """
-analyzeduration 200M -probesize 1G -ss 01:43:15.000 -noaccurate_seek -i file:"/media/movies/HDR Feature (2026)/HDR Feature Remux-2160p.mkv" -map_metadata -1 -map_chapters -1 -threads 0 -map 0:0 -map 0:1 -map -0:0 -codec:v:0 libx264 -preset veryfast -crf 23 -maxrate 5616000 -bufsize 11232000 -profile:v:0 high -level 51 -x264opts:0 subme=0:me_range=16:rc_lookahead=10:me=hex:open_gop=0 -force_key_frames:0 "expr:gte(t,n_forced*3)" -sc_threshold:v:0 0 -filter_complex "[0:3]scale,scale=-1:1080:fast_bilinear,crop,pad=max(1920\,iw):max(1080\,ih):(ow-iw)/2:(oh-ih)/2:black@0,crop=1920:1080[sub];[0:0]setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale=trunc(min(max(iw\,ih*a)\,1920)/2)*2:trunc(ow/a/2)*2,tonemapx=tonemap=bt2390:desat=0:peak=100:t=bt709:m=bt709:p=bt709:format=yuv420p[main];[main][sub]overlay=eof_action=pass:repeatlast=0" -start_at_zero -codec:a:0 libfdk_aac -ac 2 -ab 256000 -af "volume=2" -copyts -avoid_negative_ts disabled -max_muxing_queue_size 2048 -f hls -max_delay 5000000 -hls_time 3 -hls_segment_type fmp4 -hls_fmp4_init_filename "618a0563fbc023d7d6eddb5710278d1d-1.mp4" -start_number 2065 -hls_segment_filename "/cache/transcodes/618a0563fbc023d7d6eddb5710278d1d%d.mp4" -hls_playlist_type vod -hls_list_size 0 -hls_segment_options movflags=+frag_discont -y "/cache/transcodes/618a0563fbc023d7d6eddb5710278d1d.m3u8"
""".Trim();

        var args = FfmpegArgumentParser.Normalize([raw]);
        var command = FfmpegCommand.Parse(args);

        Assert.True(command.CanConsiderRouting);
        Assert.Equal("/media/movies/HDR Feature (2026)/HDR Feature Remux-2160p.mkv", command.InputPath);
        Assert.Equal("01:43:15.000", command.SeekBeforeInput);
        Assert.Equal("/cache/transcodes/618a0563fbc023d7d6eddb5710278d1d.m3u8", command.OutputPath);
        Assert.Equal("fmp4", command.ValueAfter("-hls_segment_type"));
        Assert.Equal("2065", command.ValueAfter("-start_number"));
        Assert.Contains("overlay=eof_action=pass", command.ValueAfter("-filter_complex"));
        Assert.NotNull(command.Tonemapx);
        Assert.Equal("bt2390", command.Tonemapx.Algorithm);
        Assert.Equal("100", command.Tonemapx.Peak);
    }

    [Fact]
    public void ParsesJellyfinFollowupSegmentSeekBeforeInput()
    {
        string[] args =
        [
            "-analyzeduration", "200M", "-probesize", "1G", "-ss", "00:00:33.000", "-i",
            "file:/media/movies/HEVC Feature (2026)/HEVC Feature Remux-2160p.mkv",
            "-codec:v:0", "libx264", "-force_key_frames:0", "expr:gte(t,n_forced*3)",
            "-vf", @"scale=trunc(min(max(iw\,ih*a)\,960)/2)*2:trunc(ow/a/2)*2",
            "-f", "hls", "-hls_time", "3", "-start_number", "11",
            "-hls_segment_filename", "/cache/transcodes/id%d.ts",
            "-hls_playlist_type", "vod", "-hls_list_size", "0",
            "-y", "/cache/transcodes/id.m3u8"
        ];

        var command = FfmpegCommand.Parse(args);

        Assert.True(command.CanConsiderRouting);
        Assert.Equal("00:00:33.000", command.SeekBeforeInput);
        Assert.Equal("11", command.ValueAfter("-start_number"));
    }

    [Fact]
    public void RoutesHevcAndAv1Only()
    {
        var path = Path.GetTempFileName();
        try
        {
            var command = FfmpegCommand.Parse(Split($"""-i file:{path} -codec:v:0 libx264 -f hls -y /cache/transcodes/id.m3u8"""));

            Assert.True(RouteDecision.Decide(command, new MediaProbe("hevc", "yuv420p10le", 3840, 2160, 7200, 60_000_000, "bt2020nc", "smpte2084", "bt2020")).Route);
            Assert.True(RouteDecision.Decide(command, new MediaProbe("av1", "yuv420p10le", 3840, 2160, 7200, 60_000_000, "bt2020nc", "smpte2084", "bt2020")).Route);
            Assert.False(RouteDecision.Decide(command, new MediaProbe("h264", "yuv420p", 1920, 1080, 7200, 12_000_000, "bt709", "bt709", "bt709")).Route);
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
