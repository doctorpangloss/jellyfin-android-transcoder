using System.Diagnostics;
using System.Text.Json;

namespace Jellyfin.Plugin.AndroidTranscoder;

internal static class ShimInstaller
{
    public static void Install(PluginConfiguration config, string? source = null)
    {
        source ??= GetShimPayloadPath();
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Shim payload not found at {source}. Publish JellyfinAndroidTranscoder.Shim and copy jfat-ffmpeg into shim-payload.", source);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(config.ShimPath)!);
        File.Copy(source, config.ShimPath, overwrite: true);
        MakeExecutable(config.ShimPath);
        InstallFfprobeWrapper(config);
        WriteShimConfig(config);
    }

    public static void WriteShimConfig(PluginConfiguration config)
    {
        var shimConfig = new ShimConfigFile(
            config.Enabled,
            config.AndroidBaseUrl,
            config.Token,
            config.RealFfmpegPath,
            config.RealFfprobePath,
            config.MaxBitrate,
            config.UseHardwareCodecs);
        var path = Path.Combine(Path.GetDirectoryName(config.ShimPath)!, "shim-config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(shimConfig, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string GetShimPayloadPath()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin is not initialized");
        return Path.Combine(Path.GetDirectoryName(plugin.AssemblyFilePath) ?? plugin.DataFolderPath, "shim-payload", "jfat-ffmpeg");
    }

    private static void InstallFfprobeWrapper(PluginConfiguration config)
    {
        var shimDirectory = Path.GetDirectoryName(config.ShimPath)
            ?? throw new InvalidOperationException("Shim path must include a directory");
        Directory.CreateDirectory(shimDirectory);

        var path = Path.Combine(shimDirectory, OperatingSystem.IsWindows() ? "ffprobe.cmd" : "ffprobe");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, $"@echo off\r\n\"{config.RealFfprobePath}\" %*\r\n");
            return;
        }

        var escapedFfprobe = config.RealFfprobePath.Replace("'", "'\"'\"'", StringComparison.Ordinal);
        File.WriteAllText(path, $"#!/usr/bin/env sh\nexec '{escapedFfprobe}' \"$@\"\n");
        MakeExecutable(path);
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return;
        }

        using var process = Process.Start("chmod", ["755", path]);
        process?.WaitForExit();
    }
}
