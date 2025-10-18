using Microsoft.Extensions.Logging;
using ImageViewer.Domain.Exceptions;
using ImageViewer.Application.DTOs.Files;
using ImageViewer.Domain.Interfaces;
using ImageViewer.Domain.Entities;
using MongoDB.Bson;
using FFMpegCore;

namespace ImageViewer.Application.Services;

/// <summary>
/// Service implementation for Windows drive access and file operations
/// </summary>
public class WindowsDriveService : IWindowsDriveService
{
    private readonly ILogger<WindowsDriveService> _logger;
    private readonly ILibraryRepository _libraryRepository;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();

    public WindowsDriveService(ILogger<WindowsDriveService> logger, ILibraryRepository libraryRepository)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _libraryRepository = libraryRepository ?? throw new ArgumentNullException(nameof(libraryRepository));
    }

    public async Task<IEnumerable<DriveInfo>> GetAvailableDrivesAsync()
    {
        try
        {
            var drives = new List<DriveInfo>();
            
            // Check configured drives: D, I, J, K, L
            var configuredDrives = new[] { "D", "I", "J", "K", "L" };
            
            foreach (var driveLetter in configuredDrives)
            {
                var driveInfo = await GetDriveInfoAsync(driveLetter);
                if (driveInfo != null)
                {
                    drives.Add(driveInfo);
                }
            }
            
            _logger.LogInformation("Found {Count} available drives", drives.Count);
            return drives;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get available drives");
            throw new BusinessRuleException("Failed to get available drives", ex);
        }
    }

    public async Task<bool> IsDriveAccessibleAsync(string driveLetter)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(driveLetter))
                return false;

            var drivePath = $"{driveLetter}:\\";
            return await Task.FromResult(Directory.Exists(drivePath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check drive accessibility for {DriveLetter}", driveLetter);
            return false;
        }
    }

    public async Task<DriveInfo> GetDriveInfoAsync(string driveLetter)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(driveLetter))
                throw new ValidationException("Drive letter cannot be null or empty");

            var drivePath = $"{driveLetter}:\\";
            
            if (!Directory.Exists(drivePath))
            {
                _logger.LogWarning("Drive {DriveLetter} is not accessible", driveLetter);
                return null!;
            }

            var systemDrive = new System.IO.DriveInfo(driveLetter);
            
            var driveInfo = new DriveInfo
            {
                Letter = driveLetter,
                Label = systemDrive.VolumeLabel ?? $"Drive {driveLetter}",
                FileSystem = systemDrive.DriveFormat,
                TotalSize = systemDrive.TotalSize,
                FreeSpace = systemDrive.AvailableFreeSpace,
                UsedSpace = systemDrive.TotalSize - systemDrive.AvailableFreeSpace,
                IsReady = systemDrive.IsReady,
                DriveType = systemDrive.DriveType.ToString()
            };

            _logger.LogInformation("Retrieved drive info for {DriveLetter}: {Label}, {FileSystem}, {TotalSize} bytes", 
                driveLetter, driveInfo.Label, driveInfo.FileSystem, driveInfo.TotalSize);

            return await Task.FromResult(driveInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get drive info for {DriveLetter}", driveLetter);
            throw new BusinessRuleException($"Failed to get drive info for drive '{driveLetter}'", ex);
        }
    }

    public async Task<IEnumerable<MediaFileInfo>> ScanDriveForMediaAsync(string driveLetter, string[]? extensions = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(driveLetter))
                throw new ValidationException("Drive letter cannot be null or empty");

            var drivePath = $"{driveLetter}:\\";
            
            if (!Directory.Exists(drivePath))
            {
                _logger.LogWarning("Drive {DriveLetter} is not accessible for scanning", driveLetter);
                return new List<MediaFileInfo>();
            }

            var defaultExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".mp4", ".avi", ".mov", ".wmv" };
            var searchExtensions = extensions ?? defaultExtensions;

            var mediaFiles = new List<MediaFileInfo>();

            foreach (var extension in searchExtensions)
            {
                var searchPattern = $"*{extension}";
                var files = Directory.GetFiles(drivePath, searchPattern, SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        var mediaFile = new MediaFileInfo
                        {
                            FileName = fileInfo.Name,
                            FullPath = fileInfo.FullName,
                            Extension = fileInfo.Extension.ToLower(),
                            FileSize = fileInfo.Length,
                            CreatedDate = fileInfo.CreationTime,
                            ModifiedDate = fileInfo.LastWriteTime,
                            MimeType = GetMimeType(fileInfo.Extension)
                        };

                        // Try to get media-specific information
                        if (IsImageFile(mediaFile.Extension))
                        {
                            var imageInfo = GetImageInfo(file);
                            mediaFile.Width = imageInfo.Width;
                            mediaFile.Height = imageInfo.Height;
                        }
                        else if (IsVideoFile(mediaFile.Extension))
                        {
                            var videoInfo = GetVideoInfo(file);
                            mediaFile.Duration = videoInfo.Duration;
                            mediaFile.Width = videoInfo.Width;
                            mediaFile.Height = videoInfo.Height;
                        }

                        mediaFiles.Add(mediaFile);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process file {FilePath}", file);
                    }
                }
            }

            _logger.LogInformation("Scanned drive {DriveLetter} and found {Count} media files", driveLetter, mediaFiles.Count);
            return await Task.FromResult(mediaFiles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan drive {DriveLetter} for media files", driveLetter);
            throw new BusinessRuleException($"Failed to scan drive '{driveLetter}' for media files", ex);
        }
    }

    public async Task<IEnumerable<DirectoryInfoDto>> GetDirectoryStructureAsync(string driveLetter, string? path = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(driveLetter))
                throw new ValidationException("Drive letter cannot be null or empty");

            var basePath = string.IsNullOrWhiteSpace(path) ? $"{driveLetter}:\\" : path;
            
            if (!Directory.Exists(basePath))
            {
                _logger.LogWarning("Path {Path} does not exist", basePath);
                return new List<DirectoryInfoDto>();
            }

            var directories = new List<DirectoryInfoDto>();
            var dirs = Directory.GetDirectories(basePath, "*", SearchOption.TopDirectoryOnly);

            foreach (var dir in dirs)
            {
                try
                {
                    var dirInfo = new System.IO.DirectoryInfo(dir);
                    var directory = new DirectoryInfoDto
                    {
                        Name = dirInfo.Name,
                        FullPath = dirInfo.FullName,
                        ParentPath = dirInfo.Parent?.FullName ?? "",
                        CreatedDate = dirInfo.CreationTime,
                        ModifiedDate = dirInfo.LastWriteTime,
                        FileCount = dirInfo.GetFiles().Length,
                        SubdirectoryCount = dirInfo.GetDirectories().Length,
                        TotalSize = dirInfo.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                    };

                    directories.Add(directory);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process directory {DirectoryPath}", dir);
                }
            }

            _logger.LogInformation("Retrieved directory structure for {Path}: {Count} directories", basePath, directories.Count);
            return await Task.FromResult(directories);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get directory structure for {DriveLetter}:{Path}", driveLetter, path);
            throw new BusinessRuleException($"Failed to get directory structure for '{driveLetter}:{path}'", ex);
        }
    }

    public async Task<string> CreateLibraryFromDriveAsync(string driveLetter, string libraryName, string? description = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(driveLetter))
                throw new ValidationException("Drive letter cannot be null or empty");

            if (string.IsNullOrWhiteSpace(libraryName))
                throw new ValidationException("Library name cannot be null or empty");

            var drivePath = $"{driveLetter}:\\";
            
            if (!Directory.Exists(drivePath))
            {
                throw new ValidationException($"Drive {driveLetter} is not accessible");
            }

            // Create library record in the database
            var library = new Library(
                libraryName,
                drivePath,
                ObjectId.Empty, // TODO: Get actual owner ID from current user context
                description ?? $"Library created from drive {driveLetter}:");

            await _libraryRepository.CreateAsync(library);
            
            // TODO: Implement drive scanning for media files
            // This would typically involve:
            // 1. Scanning the drive for media files
            // 2. Creating collections based on directory structure
            // 3. Setting up file monitoring
            
            var libraryId = library.Id.ToString();
            
            _logger.LogInformation("Created library {LibraryName} from drive {DriveLetter} with ID {LibraryId}", 
                libraryName, driveLetter, libraryId);

            return await Task.FromResult(libraryId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create library from drive {DriveLetter}", driveLetter);
            throw new BusinessRuleException($"Failed to create library from drive '{driveLetter}'", ex);
        }
    }

    public async Task StartDriveMonitoringAsync(string driveLetter)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(driveLetter))
                throw new ValidationException("Drive letter cannot be null or empty");

            if (_watchers.ContainsKey(driveLetter))
            {
                _logger.LogWarning("Drive {DriveLetter} is already being monitored", driveLetter);
                return;
            }

            var drivePath = $"{driveLetter}:\\";
            
            if (!Directory.Exists(drivePath))
            {
                throw new ValidationException($"Drive {driveLetter} is not accessible");
            }

            var watcher = new FileSystemWatcher(drivePath)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
            };

            watcher.Created += (sender, e) => OnFileSystemEvent(driveLetter, "Created", e.FullPath);
            watcher.Deleted += (sender, e) => OnFileSystemEvent(driveLetter, "Deleted", e.FullPath);
            watcher.Changed += (sender, e) => OnFileSystemEvent(driveLetter, "Changed", e.FullPath);
            watcher.Renamed += (sender, e) => OnFileSystemEvent(driveLetter, "Renamed", e.FullPath, e.OldFullPath);

            _watchers[driveLetter] = watcher;
            
            _logger.LogInformation("Started monitoring drive {DriveLetter}", driveLetter);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start monitoring drive {DriveLetter}", driveLetter);
            throw new BusinessRuleException($"Failed to start monitoring drive '{driveLetter}'", ex);
        }
    }

    public async Task StopDriveMonitoringAsync(string driveLetter)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(driveLetter))
                throw new ValidationException("Drive letter cannot be null or empty");

            if (_watchers.TryGetValue(driveLetter, out var watcher))
            {
                watcher.Dispose();
                _watchers.Remove(driveLetter);
                
                _logger.LogInformation("Stopped monitoring drive {DriveLetter}", driveLetter);
            }
            else
            {
                _logger.LogWarning("Drive {DriveLetter} is not being monitored", driveLetter);
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop monitoring drive {DriveLetter}", driveLetter);
            throw new BusinessRuleException($"Failed to stop monitoring drive '{driveLetter}'", ex);
        }
    }

    public async Task<DriveStatistics> GetDriveStatisticsAsync(string driveLetter)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(driveLetter))
                throw new ValidationException("Drive letter cannot be null or empty");

            var drivePath = $"{driveLetter}:\\";
            
            if (!Directory.Exists(drivePath))
            {
                throw new ValidationException($"Drive {driveLetter} is not accessible");
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            var allFiles = Directory.GetFiles(drivePath, "*", SearchOption.AllDirectories);
            var allDirectories = Directory.GetDirectories(drivePath, "*", SearchOption.AllDirectories);
            
            var mediaFiles = allFiles.Where(f => IsMediaFile(Path.GetExtension(f))).ToArray();
            var imageFiles = allFiles.Where(f => IsImageFile(Path.GetExtension(f))).ToArray();
            var videoFiles = allFiles.Where(f => IsVideoFile(Path.GetExtension(f))).ToArray();
            
            var totalSize = allFiles.Sum(f => new FileInfo(f).Length);
            
            stopwatch.Stop();

            var statistics = new DriveStatistics
            {
                DriveLetter = driveLetter,
                TotalFiles = allFiles.Length,
                TotalDirectories = allDirectories.Length,
                TotalSize = totalSize,
                MediaFiles = mediaFiles.Length,
                ImageFiles = imageFiles.Length,
                VideoFiles = videoFiles.Length,
                LastScanned = DateTime.UtcNow,
                ScanDuration = stopwatch.Elapsed
            };

            _logger.LogInformation("Generated statistics for drive {DriveLetter}: {TotalFiles} files, {TotalSize} bytes", 
                driveLetter, statistics.TotalFiles, statistics.TotalSize);

            return await Task.FromResult(statistics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get statistics for drive {DriveLetter}", driveLetter);
            throw new BusinessRuleException($"Failed to get statistics for drive '{driveLetter}'", ex);
        }
    }

    private void OnFileSystemEvent(string driveLetter, string eventType, string fullPath, string? oldFullPath = null)
    {
        try
        {
            _logger.LogInformation("Drive {DriveLetter} - {EventType}: {FullPath}", driveLetter, eventType, fullPath);
            
            if (!string.IsNullOrEmpty(oldFullPath))
            {
                _logger.LogInformation("  Old path: {OldFullPath}", oldFullPath);
            }

            // Handle file system events
            // This implementation provides basic logging and could be extended to:
            // 1. Update the database with file changes
            // 2. Trigger thumbnail generation
            // 3. Update search indexes
            // 4. Notify connected clients
            
            var fileExtension = Path.GetExtension(fullPath).ToLowerInvariant();
            var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".mp4", ".avi", ".mov" };
            
            if (supportedExtensions.Contains(fileExtension))
            {
                _logger.LogInformation("Media file change detected: {EventType} - {FullPath}", eventType, fullPath);
                
                // TODO: Implement specific media file handling
                // This could involve updating collections, regenerating thumbnails, etc.
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file system event for drive {DriveLetter}", driveLetter);
        }
    }

    private string GetMimeType(string extension)
    {
        return extension.ToLower() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".avi" => "video/avi",
            ".mov" => "video/quicktime",
            ".wmv" => "video/x-ms-wmv",
            _ => "application/octet-stream"
        };
    }

    private bool IsMediaFile(string extension)
    {
        var mediaExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".mp4", ".avi", ".mov", ".wmv" };
        return mediaExtensions.Contains(extension.ToLower());
    }

    private bool IsImageFile(string extension)
    {
        var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
        return imageExtensions.Contains(extension.ToLower());
    }

    private bool IsVideoFile(string extension)
    {
        var videoExtensions = new[] { ".mp4", ".avi", ".mov", ".wmv" };
        return videoExtensions.Contains(extension.ToLower());
    }

    private (int Width, int Height) GetImageInfo(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return (0, 0);

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            // Check if it's a supported image format
            var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif" };
            if (!supportedExtensions.Contains(extension))
                return (0, 0);

            // Use ImageSharp to get actual image dimensions
            using var image = SixLabors.ImageSharp.Image.Load(filePath);
            return (image.Width, image.Height);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get image dimensions for file {FilePath}", filePath);
            return (0, 0);
        }
    }

    private (TimeSpan Duration, int Width, int Height) GetVideoInfo(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return (TimeSpan.Zero, 0, 0);

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            // Check if it's a supported video format
            var supportedExtensions = new[] { ".mp4", ".avi", ".mov", ".wmv", ".mkv", ".flv", ".webm", ".m4v", ".3gp", ".mpg", ".mpeg" };
            if (!supportedExtensions.Contains(extension))
                return (TimeSpan.Zero, 0, 0);

            // Use FFMpegCore to get actual video information
            var mediaInfo = FFProbe.Analyse(filePath);
            var videoStream = mediaInfo.VideoStreams.FirstOrDefault();
            
            if (videoStream != null)
            {
                var duration = mediaInfo.Duration;
                var width = videoStream.Width;
                var height = videoStream.Height;
                
                return (duration, width, height);
            }
            else
            {
                _logger.LogWarning("No video stream found in file {FilePath}", filePath);
                return (TimeSpan.Zero, 0, 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get video information for file {FilePath}", filePath);
            return (TimeSpan.Zero, 0, 0);
        }
    }
}
