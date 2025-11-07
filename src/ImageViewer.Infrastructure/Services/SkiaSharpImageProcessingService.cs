using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using ImageViewer.Domain.Interfaces;
using ImageViewer.Domain.ValueObjects;
using ImageViewer.Domain.Enums;
using SharpCompress.Archives;
using ImageViewer.Application.Helpers;
using FFMpegCore;
using ImageViewer.Application.Options;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ImageViewer.Infrastructure.Services;

/// <summary>
/// SkiaSharp-based image processing service
/// </summary>
public class SkiaSharpImageProcessingService : IImageProcessingService
{
    private readonly ILogger<SkiaSharpImageProcessingService> _logger;
    private readonly string? _ffmpegBinPath;

    public SkiaSharpImageProcessingService(ILogger<SkiaSharpImageProcessingService> logger, IOptions<FFmpegOptions> ffmpegOptions)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ffmpegBinPath = ffmpegOptions?.Value?.BinPath;
    }

    public Task<ImageMetadata> ExtractMetadataAsync(ArchiveEntryInfo archiveEntry, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Extracting metadata from {ImagePath}", archiveEntry.GetPhysicalFileFullPath());

            using var stream = File.OpenRead(archiveEntry.GetPhysicalFileFullPath());
            using var codec = SKCodec.Create(stream);
            using var image = SKImage.FromEncodedData(stream);

            var info = codec.Info;
            var metadata = new ImageMetadata(
                quality: 95,
                colorSpace: info.ColorSpace?.ToString(),
                compression: "Unknown", // SkiaSharp doesn't expose compression info directly
                createdDate: File.GetCreationTime(archiveEntry.GetPhysicalFileFullPath()),
                modifiedDate: File.GetLastWriteTime(archiveEntry.GetPhysicalFileFullPath())
            );

            _logger.LogDebug("Successfully extracted metadata from {ImagePath}", archiveEntry.GetPhysicalFileFullPath());
            return Task.FromResult(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting metadata from {ImagePath}", archiveEntry.GetPhysicalFileFullPath());
            throw;
        }
    }

    public Task<byte[]> GenerateThumbnailFromBytesAsync(byte[] imageData, int width, int height, string format = "jpeg", int quality = 90, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Generating thumbnail from bytes with size {Width}x{Height}, format {Format}, quality {Quality}", width, height, format, quality);

            using var data = SKData.CreateCopy(imageData);
            using var originalImage = SKImage.FromEncodedData(data);
            
            var originalInfo = originalImage.Info;
            var scaleX = (float)width / originalInfo.Width;
            var scaleY = (float)height / originalInfo.Height;
            var scale = Math.Min(scaleX, scaleY);

            var thumbnailWidth = (int)(originalInfo.Width * scale);
            var thumbnailHeight = (int)(originalInfo.Height * scale);

            var imageInfo = new SKImageInfo(thumbnailWidth, thumbnailHeight);
            using var surface = SKSurface.Create(imageInfo);
            using var canvas = surface.Canvas;
            using var paint = new SKPaint
            {
                FilterQuality = SKFilterQuality.High,
                IsAntialias = true
            };

            canvas.Clear(SKColors.Transparent);
            // DrawImage(image, source rect, dest rect, paint) - CORRECT ORDER!
            canvas.DrawImage(originalImage,
                new SKRect(0, 0, originalInfo.Width, originalInfo.Height), // Source: entire original
                new SKRect(0, 0, thumbnailWidth, thumbnailHeight), // Dest: thumbnail size
                paint);
            
            using var image = surface.Snapshot();
            var encodedFormat = ParseImageFormat(format);
            using var encoded = image.Encode(encodedFormat, quality);
            
            var result = encoded.ToArray();
            _logger.LogDebug("Generated thumbnail from bytes: {Size} bytes, format {Format}", result.Length, format);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating thumbnail from bytes");
            throw;
        }
    }

    public Task<byte[]> GenerateThumbnailAsync(ArchiveEntryInfo archiveEntry, int width, int height, string format = "jpeg", int quality = 95, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = archiveEntry.GetPhysicalFileFullPath();
            _logger.LogDebug("Generating thumbnail for {ImagePath} with size {Width}x{Height}, format {Format}, quality {Quality}", filePath, width, height, format, quality);

            // Check if this is a video file
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            if (IsVideoFile(extension))
            {
                _logger.LogDebug("🎬 Detected video file, extracting frame for thumbnail: {FilePath}", filePath);
                return GenerateVideoThumbnailAsync(filePath, width, height, format, quality, cancellationToken);
            }

            // Handle image files using SkiaSharp
            using var stream = File.OpenRead(filePath);
            using var originalImage = SKImage.FromEncodedData(stream);
            
            var originalInfo = originalImage.Info;
            var scaleX = (float)width / originalInfo.Width;
            var scaleY = (float)height / originalInfo.Height;
            var scale = Math.Min(scaleX, scaleY);

            var thumbnailWidth = (int)(originalInfo.Width * scale);
            var thumbnailHeight = (int)(originalInfo.Height * scale);

            using var thumbnailBitmap = new SKBitmap(thumbnailWidth, thumbnailHeight);
            using var canvas = new SKCanvas(thumbnailBitmap);
            
            // Use high-quality interpolation for better thumbnail quality
            using var paint = new SKPaint
            {
                FilterQuality = SKFilterQuality.High,
                IsAntialias = true
            };
            
            canvas.Clear(SKColors.White);
            // DrawImage(image, source rect, dest rect, paint) - CORRECT ORDER!
            canvas.DrawImage(originalImage,
                new SKRect(0, 0, originalInfo.Width, originalInfo.Height), // Source: entire original
                new SKRect(0, 0, thumbnailWidth, thumbnailHeight), // Dest: thumbnail size
                paint);

            using var thumbnailImage = SKImage.FromBitmap(thumbnailBitmap);
            var encodedFormat = ParseImageFormat(format);
            using var thumbnailStream = thumbnailImage.Encode(encodedFormat, quality);
            
            var result = thumbnailStream.ToArray();
            
            _logger.LogDebug("Successfully generated thumbnail for {ImagePath}, format {Format}", filePath, format);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating thumbnail for {ImagePath}", archiveEntry.GetPhysicalFileFullPath());
            throw;
        }
    }

    /// <summary>
    /// Generate thumbnail from video file by extracting a frame
    /// </summary>
    private async Task<byte[]> GenerateVideoThumbnailAsync(string videoPath, int width, int height, string format, int quality, CancellationToken cancellationToken)
    {
        try
        {
            // Create temporary file for the extracted frame
            var tempFramePath = Path.Combine(Path.GetTempPath(), $"video_frame_{Guid.NewGuid()}.jpg");
            
            try
            {
                // Extract a frame from the video (at 1 second or 10% of duration, whichever is smaller)
                var mediaInfo = FFProbe.Analyse(videoPath);
                var duration = mediaInfo.Duration;
                var videoStream = mediaInfo.VideoStreams.FirstOrDefault();
                
                if (videoStream == null)
                {
                    _logger.LogWarning("⚠️ No video stream found in {VideoPath}", videoPath);
                    throw new InvalidOperationException($"No video stream found in {videoPath}");
                }

                // Use 1 second or 10% of duration, whichever is smaller, but at least 0.1 seconds
                var seekTime = duration.TotalSeconds > 0 
                    ? Math.Min(1.0, Math.Max(0.1, duration.TotalSeconds * 0.1))
                    : 0.5;
                
                _logger.LogDebug("Extracting frame from {VideoPath} at {SeekTime} seconds to {TempPath}", videoPath, seekTime, tempFramePath);

                await ExtractVideoFrameAsync(videoPath, tempFramePath, seekTime, cancellationToken);

                if (!File.Exists(tempFramePath))
                {
                    _logger.LogError("❌ Frame file not created after FFmpeg execution. Video: {VideoPath}, TempPath: {TempPath}", videoPath, tempFramePath);
                    throw new FileNotFoundException($"Failed to extract frame from video: {videoPath}. Temp file not created: {tempFramePath}");
                }
                
                _logger.LogDebug("✅ Frame extracted successfully: {TempPath} ({Size} bytes)", tempFramePath, new FileInfo(tempFramePath).Length);

                // Load the extracted frame and resize it using SkiaSharp
                using var frameStream = File.OpenRead(tempFramePath);
                using var frameImage = SKImage.FromEncodedData(frameStream);
                
                var originalInfo = frameImage.Info;
                var scaleX = (float)width / originalInfo.Width;
                var scaleY = (float)height / originalInfo.Height;
                var scale = Math.Min(scaleX, scaleY);

                var thumbnailWidth = (int)(originalInfo.Width * scale);
                var thumbnailHeight = (int)(originalInfo.Height * scale);

                using var thumbnailBitmap = new SKBitmap(thumbnailWidth, thumbnailHeight);
                using var canvas = new SKCanvas(thumbnailBitmap);
                
                using var paint = new SKPaint
                {
                    FilterQuality = SKFilterQuality.High,
                    IsAntialias = true
                };
                
                canvas.Clear(SKColors.White);
                canvas.DrawImage(frameImage,
                    new SKRect(0, 0, originalInfo.Width, originalInfo.Height),
                    new SKRect(0, 0, thumbnailWidth, thumbnailHeight),
                    paint);

                using var thumbnailImage = SKImage.FromBitmap(thumbnailBitmap);
                var encodedFormat = ParseImageFormat(format);
                using var thumbnailStream = thumbnailImage.Encode(encodedFormat, quality);
                
                var result = thumbnailStream.ToArray();
                
                _logger.LogDebug("✅ Successfully generated video thumbnail: {Size} bytes, format {Format}", result.Length, format);
                return result;
            }
            catch (System.ComponentModel.Win32Exception winEx) when (winEx.NativeErrorCode == 2 || winEx.Message.Contains("ffprobe", StringComparison.OrdinalIgnoreCase))
            {
                // FFprobe not found in PATH
                _logger.LogWarning("⚠️ FFprobe not found in system PATH. Video thumbnail generation requires FFmpeg to be installed. Error: {ErrorMessage}", winEx.Message);
                throw new InvalidOperationException("FFprobe not found. Please install FFmpeg and ensure ffprobe.exe is in your system PATH, or configure FFMpegCore with the path to FFmpeg binaries.", winEx);
            }
            catch (Exception ex) when (ex.GetType().FullName?.Contains("InstanceFileNotFoundException") == true || 
                                        ex.Message.Contains("ffprobe", StringComparison.OrdinalIgnoreCase))
            {
                // FFMpegCore-specific exception for missing FFprobe
                _logger.LogWarning("⚠️ FFprobe not found. Video thumbnail generation requires FFmpeg to be installed. Error: {ErrorMessage}", ex.Message);
                throw new InvalidOperationException("FFprobe not found. Please install FFmpeg and ensure ffprobe.exe is in your system PATH, or configure FFMpegCore with the path to FFmpeg binaries.", ex);
            }
            finally
            {
                // Clean up temporary frame file
                try
                {
                    if (File.Exists(tempFramePath))
                    {
                        File.Delete(tempFramePath);
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to delete temporary frame file: {TempPath}", tempFramePath);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Re-throw InvalidOperationException (including our wrapped FFprobe errors) as-is
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error generating video thumbnail for {VideoPath}", videoPath);
            throw;
        }
    }

    private string ResolveFfmpegExecutablePath()
    {
        var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";

        if (!string.IsNullOrWhiteSpace(_ffmpegBinPath))
        {
            var configuredPath = Path.Combine(_ffmpegBinPath, executableName);
            if (File.Exists(configuredPath))
            {
                return configuredPath;
            }

            _logger.LogWarning("Configured FFmpeg binary not found at {ConfiguredPath}. Falling back to defaults.", configuredPath);
        }

        var globalFolder = GlobalFFOptions.Current?.BinaryFolder;
        if (!string.IsNullOrWhiteSpace(globalFolder))
        {
            var globalPath = Path.Combine(globalFolder, executableName);
            if (File.Exists(globalPath))
            {
                return globalPath;
            }
        }

        return executableName;
    }

    private async Task ExtractVideoFrameAsync(string videoPath, string outputPath, double seekTimeSeconds, CancellationToken cancellationToken)
    {
        var ffmpegExecutable = ResolveFfmpegExecutablePath();
        _logger.LogDebug("Using FFmpeg executable: {FfmpegPath}", ffmpegExecutable);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegExecutable,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add(seekTimeSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(videoPath);
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-update");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var errorBuilder = new StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                errorBuilder.AppendLine(e.Data);
            }
        };
        process.OutputDataReceived += (_, __) => { };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start FFmpeg process using executable '{ffmpegExecutable}'.");
            }

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception killEx)
                {
                    _logger.LogWarning(killEx, "Failed to kill FFmpeg process after cancellation.");
                }
            });

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                var errorOutput = errorBuilder.ToString().Trim();
                throw new InvalidOperationException($"FFmpeg exited with code {process.ExitCode}. {(string.IsNullOrEmpty(errorOutput) ? "" : errorOutput)}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            var errorOutput = errorBuilder.ToString().Trim();
            var message = string.IsNullOrEmpty(errorOutput)
                ? ex.Message
                : $"{ex.Message} | FFmpeg error: {errorOutput}";
            throw new InvalidOperationException(message, ex);
        }
    }

    /// <summary>
    /// Check if file extension is a video format
    /// </summary>
    private static bool IsVideoFile(string extension)
    {
        var videoExtensions = new[] { ".mp4", ".avi", ".mov", ".wmv", ".mkv", ".flv", ".webm", ".m4v", ".3gp", ".mpg", ".mpeg" };
        return videoExtensions.Contains(extension.ToLowerInvariant());
    }

    public Task<byte[]> ResizeImageFromBytesAsync(byte[] imageData, int width, int height, string format = "jpeg", int quality = 95, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Resizing image from bytes to {Width}x{Height} with format {Format}, quality {Quality}", width, height, format, quality);

            using var data = SKData.CreateCopy(imageData);
            using var originalImage = SKImage.FromEncodedData(data);
            
            if (originalImage == null)
            {
                _logger.LogError("Failed to decode image from byte array. Data may be corrupted or unsupported format. Size: {Size} bytes", imageData.Length);
                throw new InvalidOperationException($"Failed to decode image from byte array (size: {imageData.Length} bytes)");
            }
            
            var originalInfo = originalImage.Info;
            var scaleX = (float)width / originalInfo.Width;
            var scaleY = (float)height / originalInfo.Height;
            var scale = Math.Min(scaleX, scaleY);

            var newWidth = (int)(originalInfo.Width * scale);
            var newHeight = (int)(originalInfo.Height * scale);

            var imageInfo = new SKImageInfo(newWidth, newHeight);
            using var surface = SKSurface.Create(imageInfo);
            using var canvas = surface.Canvas;
            using var paint = new SKPaint
            {
                FilterQuality = SKFilterQuality.High,
                IsAntialias = true
            };

            canvas.Clear(SKColors.White);
            // DrawImage(image, source rect, dest rect, paint) - CORRECT ORDER!
            canvas.DrawImage(originalImage, 
                new SKRect(0, 0, originalInfo.Width, originalInfo.Height), // Source: entire original
                new SKRect(0, 0, newWidth, newHeight), // Dest: resized canvas
                paint);
            
            using var image = surface.Snapshot();
            var encodedFormat = ParseImageFormat(format);
            using var encoded = image.Encode(encodedFormat, quality);
            
            var result = encoded.ToArray();
            _logger.LogDebug("Resized image from bytes: {Size} bytes, format {Format}", result.Length, format);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resizing image from bytes");
            throw;
        }
    }

    public Task<byte[]> ResizeImageAsync(ArchiveEntryInfo archiveEntry, int width, int height, string format = "jpeg", int quality = 95, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Resizing image {ImagePath} to {Width}x{Height} with format {Format}, quality {Quality}", archiveEntry.GetPhysicalFileFullPath(), width, height, format, quality);

            using var stream = File.OpenRead(archiveEntry.GetPhysicalFileFullPath());
            using var originalImage = SKImage.FromEncodedData(stream);
            
            if (originalImage == null)
            {
                _logger.LogError("Failed to decode image: {ImagePath}. File may be corrupted or unsupported format.", archiveEntry.GetPhysicalFileFullPath());
                throw new InvalidOperationException($"Failed to decode image: {archiveEntry.GetPhysicalFileFullPath()}");
            }
            
            var originalInfo = originalImage.Info;
            var scaleX = (float)width / originalInfo.Width;
            var scaleY = (float)height / originalInfo.Height;
            var scale = Math.Min(scaleX, scaleY);

            var newWidth = (int)(originalInfo.Width * scale);
            var newHeight = (int)(originalInfo.Height * scale);

            using var resizedBitmap = new SKBitmap(newWidth, newHeight);
            using var canvas = new SKCanvas(resizedBitmap);
            using var paint = new SKPaint
            {
                FilterQuality = SKFilterQuality.High,
                IsAntialias = true
            };
            
            canvas.Clear(SKColors.White);
            // DrawImage(image, source rect, dest rect, paint) - CORRECT ORDER!
            canvas.DrawImage(originalImage, 
                new SKRect(0, 0, originalInfo.Width, originalInfo.Height), // Source: entire original
                new SKRect(0, 0, newWidth, newHeight), // Dest: resized canvas
                paint);

            using var resizedImage = SKImage.FromBitmap(resizedBitmap);
            var encodedFormat = ParseImageFormat(format);
            using var resizedStream = resizedImage.Encode(encodedFormat, quality);
            
            var result = resizedStream.ToArray();
            
            _logger.LogDebug("Successfully resized image {ImagePath}, format {Format}", archiveEntry.GetPhysicalFileFullPath(), format);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resizing image {ImagePath}", archiveEntry.GetPhysicalFileFullPath());
            throw;
        }
    }

    public Task<byte[]> ConvertImageFormatAsync(ArchiveEntryInfo archiveEntry, string targetFormat, int quality = 95, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Converting image {ImagePath} to format {TargetFormat}", archiveEntry.GetPhysicalFileFullPath(), targetFormat);

            using var stream = File.OpenRead(archiveEntry.GetPhysicalFileFullPath());
            using var originalImage = SKImage.FromEncodedData(stream);
            
            var format = targetFormat.ToLowerInvariant() switch
            {
                "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg,
                "png" => SKEncodedImageFormat.Png,
                "webp" => SKEncodedImageFormat.Webp,
                "bmp" => SKEncodedImageFormat.Bmp,
                _ => throw new ArgumentException($"Unsupported target format: {targetFormat}")
            };

            using var convertedStream = originalImage.Encode(format, quality);
            var result = convertedStream.ToArray();
            
            _logger.LogDebug("Successfully converted image {ImagePath} to {TargetFormat}", archiveEntry.GetPhysicalFileFullPath(), targetFormat);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting image {ImagePath} to {TargetFormat}", archiveEntry.GetPhysicalFileFullPath(), targetFormat);
            throw;
        }
    }

    public Task<bool> IsImageFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Checking if {FilePath} is an image file", filePath);

            if (!File.Exists(filePath))
            {
                return Task.FromResult(false);
            }

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif" };
            
            if (!supportedExtensions.Contains(extension))
            {
                return Task.FromResult(false);
            }

            // Try to decode the image to verify it's valid
            using var stream = File.OpenRead(filePath);
            using var codec = SKCodec.Create(stream);
            
            var result = codec != null;
            _logger.LogDebug("File {FilePath} is {Result} an image file", filePath, result ? "" : "not");
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking if {FilePath} is an image file", filePath);
            return Task.FromResult(false);
        }
    }

    public Task<string[]> GetSupportedFormatsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new[] { "jpg", "jpeg", "png", "gif", "bmp", "webp", "tiff" });
    }

    public async Task<ImageDimensions> GetImageDimensionsAsync(ArchiveEntryInfo archiveEntry, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting dimensions for {ImagePath}", archiveEntry.GetDisplayPath());

            byte[] imageBytes;
            
            // ✅ Handle archive entries using SharpCompress (same logic as ArchiveFileHelper)
            if (archiveEntry.FileType == ImageFileType.ArchiveEntry)
            {
                if (!File.Exists(archiveEntry.ArchivePath))
                {
                    throw new FileNotFoundException($"Archive not found: {archiveEntry.ArchivePath}");
                }

                using var archive = ArchiveFactory.Open(archiveEntry.ArchivePath);
                
                // Try exact match
                var entry = archive.Entries.FirstOrDefault(e => 
                    !e.IsDirectory && 
                    MacOSXFilterHelper.IsSafeToProcess(e.Key, "dimension extraction") &&
                    (e.Key == archiveEntry.EntryName || e.Key.Replace('\\', '/') == archiveEntry.EntryName.Replace('\\', '/')));
                
                // Fallback: filename only
                if (entry == null)
                {
                    var filename = Path.GetFileName(archiveEntry.EntryName);
                    entry = archive.Entries.FirstOrDefault(e => 
                        !e.IsDirectory && 
                        MacOSXFilterHelper.IsSafeToProcess(e.Key, "dimension extraction") &&
                        Path.GetFileName(e.Key).Equals(filename, StringComparison.OrdinalIgnoreCase));
                }
                
                if (entry == null)
                {
                    throw new FileNotFoundException($"Entry {archiveEntry.EntryName} not found in {archiveEntry.ArchivePath}");
                }

                using var entryStream = entry.OpenEntryStream();
                using var memoryStream = new MemoryStream();
                await entryStream.CopyToAsync(memoryStream, cancellationToken);
                imageBytes = memoryStream.ToArray();
            }
            else
            {
                // For regular files
                imageBytes = await File.ReadAllBytesAsync(archiveEntry.GetPhysicalFileFullPath(), cancellationToken);
            }

            // Create codec from bytes
            using var data = SKData.CreateCopy(imageBytes);
            using var codec = SKCodec.Create(data);
            
            if (codec == null)
            {
                throw new InvalidOperationException($"Failed to create codec for {archiveEntry.GetDisplayPath()}");
            }
            
            var info = codec.Info;
            var dimensions = new ImageDimensions(info.Width, info.Height);
            
            _logger.LogDebug("Image {ImagePath} dimensions: {Width}x{Height}", archiveEntry.GetDisplayPath(), dimensions.Width, dimensions.Height);
            return dimensions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dimensions for {ImagePath}", archiveEntry.GetDisplayPath());
            throw;
        }
    }

    public Task<ImageDimensions> GetImageDimensionsFromBytesAsync(byte[] imageData, CancellationToken cancellationToken = default)
    {
        try
        {
            using var data = SKData.CreateCopy(imageData);
            using var codec = SKCodec.Create(data);
            var info = codec.Info;
            return Task.FromResult(new ImageDimensions(info.Width, info.Height));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dimensions from image bytes");
            throw;
        }
    }

    public Task<long> GetImageFileSizeAsync(ArchiveEntryInfo archiveEntry, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting file size for {ImagePath}", archiveEntry.GetPhysicalFileFullPath());

            var fileInfo = new FileInfo(archiveEntry.GetPhysicalFileFullPath());
            var size = fileInfo.Length;
            
            _logger.LogDebug("Image {ImagePath} file size: {Size} bytes", archiveEntry.GetPhysicalFileFullPath(), size);
            return Task.FromResult(size);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting file size for {ImagePath}", archiveEntry.GetPhysicalFileFullPath());
            throw;
        }
    }

    public Task<byte[]> GenerateCacheAsync(ArchiveEntryInfo archiveEntry, int width, int height, string format = "jpeg", int quality = 95, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("🎨 Generating cache image from {ImagePath} ({Width}x{Height}, {Format}, Quality: {Quality})",
                archiveEntry.GetPhysicalFileFullPath(), width, height, format, quality);

            using var stream = File.OpenRead(archiveEntry.GetPhysicalFileFullPath());
            using var originalBitmap = SKBitmap.Decode(stream);
            
            if (originalBitmap == null)
            {
                _logger.LogWarning("⚠️ Failed to decode image from {ImagePath}", archiveEntry.GetPhysicalFileFullPath());
                return Task.FromResult(Array.Empty<byte>());
            }

            // Calculate scale factor to maintain aspect ratio
            var scaleX = (float)width / originalBitmap.Width;
            var scaleY = (float)height / originalBitmap.Height;
            var scale = Math.Min(scaleX, scaleY);
            
            var scaledWidth = (int)(originalBitmap.Width * scale);
            var scaledHeight = (int)(originalBitmap.Height * scale);
            
            // Create scaled bitmap
            using var scaledBitmap = new SKBitmap(scaledWidth, scaledHeight);
            using var canvas = new SKCanvas(scaledBitmap);
            
            // Draw scaled image
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(originalBitmap, new SKRect(0, 0, scaledWidth, scaledHeight));
            
            // Encode to desired format
            var imageFormat = ParseImageFormat(format);
            using var encodedData = scaledBitmap.Encode(imageFormat, quality);
            
            if (encodedData == null)
            {
                _logger.LogWarning("⚠️ Failed to encode cache image");
                return Task.FromResult(Array.Empty<byte>());
            }
            
            var result = encodedData.ToArray();
            _logger.LogDebug("✅ Generated cache image: {Size} bytes", result.Length);
            
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error generating cache from {ImagePath}", archiveEntry.GetPhysicalFileFullPath());
            return Task.FromResult(Array.Empty<byte>());
        }
    }

    public Task<byte[]> GenerateCacheFromBytesAsync(byte[] imageData, int width, int height, string format = "jpeg", int quality = 90, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("🎨 Generating cache image from bytes ({Width}x{Height}, {Format}, Quality: {Quality})", 
                width, height, format, quality);

            using var data = SKData.CreateCopy(imageData);
            using var originalBitmap = SKBitmap.Decode(data);
            
            if (originalBitmap == null)
            {
                _logger.LogWarning("⚠️ Failed to decode image from bytes");
                return Task.FromResult(Array.Empty<byte>());
            }

            // Calculate scale factor to maintain aspect ratio
            var scaleX = (float)width / originalBitmap.Width;
            var scaleY = (float)height / originalBitmap.Height;
            var scale = Math.Min(scaleX, scaleY);
            
            var scaledWidth = (int)(originalBitmap.Width * scale);
            var scaledHeight = (int)(originalBitmap.Height * scale);
            
            // Create scaled bitmap
            using var scaledBitmap = new SKBitmap(scaledWidth, scaledHeight);
            using var canvas = new SKCanvas(scaledBitmap);
            
            // Draw scaled image
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(originalBitmap, new SKRect(0, 0, scaledWidth, scaledHeight));
            
            // Encode to desired format
            var imageFormat = ParseImageFormat(format);
            using var encodedData = scaledBitmap.Encode(imageFormat, quality);
            
            if (encodedData == null)
            {
                _logger.LogWarning("⚠️ Failed to encode cache image");
                return Task.FromResult(Array.Empty<byte>());
            }
            
            var result = encodedData.ToArray();
            _logger.LogDebug("✅ Generated cache image: {Size} bytes", result.Length);
            
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error generating cache from bytes");
            return Task.FromResult(Array.Empty<byte>());
        }
    }

    /// <summary>
    /// Parse image format string to SKEncodedImageFormat enum
    /// </summary>
    private SKEncodedImageFormat ParseImageFormat(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg,
            "png" => SKEncodedImageFormat.Png,
            "webp" => SKEncodedImageFormat.Webp,
            "bmp" => SKEncodedImageFormat.Bmp,
            _ => SKEncodedImageFormat.Jpeg // Default to JPEG
        };
    }
}
