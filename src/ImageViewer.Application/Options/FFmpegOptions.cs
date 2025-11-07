namespace ImageViewer.Application.Options;

/// <summary>
/// FFmpeg configuration options
/// </summary>
public class FFmpegOptions
{
    /// <summary>
    /// Path to FFmpeg binaries folder (contains ffmpeg.exe and ffprobe.exe)
    /// If empty or null, FFMpegCore will search in system PATH
    /// </summary>
    public string? BinPath { get; set; }
}

