using System.Text.Json;
using SharpCompress.Archives;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using ImageViewer.Domain.Events;
using ImageViewer.Domain.Interfaces;
using ImageViewer.Application.Services;
using ImageViewer.Domain.Enums;
using ImageViewer.Infrastructure.Data;
using ImageViewer.Application.Helpers;
using MongoDB.Bson;
using ImageViewer.Domain.ValueObjects;

namespace ImageViewer.Worker.Services;

/// <summary>
/// Consumer for collection scan messages
/// </summary>
public class CollectionScanConsumer : BaseMessageConsumer
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public CollectionScanConsumer(
        IConnection connection,
        IOptions<RabbitMQOptions> options,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<CollectionScanConsumer> logger)
        : base(connection, options, logger, "collection.scan", "collection-scan-consumer")
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    protected override async Task ProcessMessageAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("🔍 Received collection scan message: {Message}", message);
            
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
            var scanMessage = JsonSerializer.Deserialize<CollectionScanMessage>(message, options);
            if (scanMessage == null)
            {
                _logger.LogWarning("❌ Failed to deserialize CollectionScanMessage from: {Message}", message);
                return;
            }

            _logger.LogInformation("🔍 Processing collection scan for collection {CollectionId} at path {Path}", 
                scanMessage.CollectionId, scanMessage.CollectionPath);

            // Try to create scope, handle disposal gracefully
            IServiceScope? scope = null;
            try
            {
                scope = _serviceScopeFactory.CreateScope();
            }
            catch (ObjectDisposedException)
            {
                _logger.LogWarning("⚠️ Service provider disposed, worker is shutting down. Skipping collection scan.");
                return;
            }

            using (scope)
            {
            var collectionService = scope.ServiceProvider.GetRequiredService<ICollectionService>();
            var messageQueueService = scope.ServiceProvider.GetRequiredService<IMessageQueueService>();
            var backgroundJobService = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            var libraryRepository = scope.ServiceProvider.GetRequiredService<ILibraryRepository>();

            // Get the collection (convert string CollectionId back to ObjectId)
            var collectionId = ObjectId.Parse(scanMessage.CollectionId);
            var collection = await collectionService.GetCollectionByIdAsync(collectionId);
            if (collection == null)
            {
                _logger.LogWarning("❌ Collection {CollectionId} not found, skipping scan", scanMessage.CollectionId);
                
                // Update job status to failed if JobId exists
                if (!string.IsNullOrEmpty(scanMessage.JobId))
                {
                    try
                    {
                        await backgroundJobService.UpdateJobStatusAsync(ObjectId.Parse(scanMessage.JobId), "Failed", $"Collection {scanMessage.CollectionId} not found");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to update job status for {JobId}", scanMessage.JobId);
                    }
                }
                return;
            }
            
            // Update job stage to InProgress - SCAN stage
            if (!string.IsNullOrEmpty(scanMessage.JobId))
            {
                try
                {
                    await backgroundJobService.UpdateJobStageAsync(
                        ObjectId.Parse(scanMessage.JobId), 
                        "scan", 
                        "InProgress", 
                        0, 
                        0, 
                        $"Scanning collection {collection.Name}");
                    _logger.LogDebug("Updated job {JobId} scan stage to InProgress", scanMessage.JobId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update job stage for {JobId}", scanMessage.JobId);
                }
            }

            // Check if collection path exists (directory for Folder type, file for Zip type)
            bool pathExists = collection.Type == CollectionType.Folder 
                ? Directory.Exists(collection.Path) 
                : File.Exists(collection.Path);
            
            if (!pathExists)
            {
                _logger.LogWarning("❌ Collection path {Path} does not exist, skipping scan", collection.Path);
                return;
            }

            // If ForceRescan is true, clear existing image arrays
            if (scanMessage.ForceRescan)
            {
                _logger.LogWarning("🔥 ForceRescan=true: Clearing existing image arrays for collection {CollectionId}", collection.Id);
                
                var collectionRepository = scope.ServiceProvider.GetRequiredService<ICollectionRepository>();
                await collectionRepository.ClearImageArraysAsync(collection.Id);
                
                _logger.LogInformation("✅ Cleared image arrays (images, thumbnails, cache) for collection {CollectionId}", collection.Id);
            }

            // Scan the collection for media files
            var mediaFiles = ScanCollectionForMediaFiles(collection.Path, collection.Type);
            _logger.LogDebug("📁 Found {FileCount} media files in collection {CollectionId}", 
                mediaFiles.Count, collection.Id);

            // Check if direct file access mode is enabled and valid for this collection
            var useDirectAccess = scanMessage.UseDirectFileAccess && collection.Type == CollectionType.Folder;
            
            if (useDirectAccess)
            {
                _logger.LogInformation("🚀 Direct file access mode enabled for directory collection {Name} ({FileCount} files)", 
                    collection.Name, mediaFiles.Count);
                
                // Direct mode: Create image/thumbnail/cache references without queue processing
                await ProcessDirectFileAccessMode(collection, mediaFiles, scope, backgroundJobService, scanMessage.JobId);
            }
            else
            {
                // Standard mode: Queue image processing jobs
                if (collection.Type != CollectionType.Folder && scanMessage.UseDirectFileAccess)
                {
                    _logger.LogInformation("⚠️ Direct file access mode requested but collection {Name} is an archive ({Type}), using standard mode", 
                        collection.Name, collection.Type);
                }
                
                // Create image processing jobs for each media file
                foreach (var mediaFile in mediaFiles)
                {
                    try
                    {
                        // Extract basic metadata for the image processing message
                        var archiveEntry = ArchiveEntryInfo.FromCollection(
                            collection.Path, 
                            collection.Type, 
                            mediaFile.FileName, 
                            mediaFile.FileSize);

                        var (width, height) = await ExtractImageDimensions(archiveEntry);
                        
                        var imageProcessingMessage = new ImageProcessingMessage
                        {
                            ImageId = ObjectId.GenerateNewId().ToString(), // Will be set when image is created, convert to string
                            CollectionId = collection.Id.ToString(), // Convert ObjectId to string
                            //ImagePath = mediaFile.FullPath,
                            ArchiveEntry = archiveEntry,
                            ImageFormat = mediaFile.Extension,
                            Width = width,
                            Height = height,
                            FileSize = mediaFile.FileSize,
                            GenerateThumbnail = true,
                            OptimizeImage = false,
                            CreatedBy = "CollectionScanConsumer",
                            CreatedBySystem = "ImageViewer.Worker",
                            ScanJobId = scanMessage.JobId // Pass scan job ID for tracking
                        };

                        // Queue the image processing job
                        await messageQueueService.PublishAsync(imageProcessingMessage, "image.processing");
                        _logger.LogDebug("📋 Queued image processing job for {ImagePath}", mediaFile.FullPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Failed to create image processing job for {ImagePath}", mediaFile.FullPath);
                    }
                }

                _logger.LogDebug("✅ Successfully processed collection scan for {CollectionId}, queued {JobCount} image processing jobs", 
                    collection.Id, mediaFiles.Count);
            }

            // Update library statistics if collection belongs to a library
            if (collection.LibraryId.HasValue && collection.LibraryId.Value != ObjectId.Empty)
            {
                try
                {
                    // Calculate total size of discovered media files
                    var totalSize = mediaFiles.Sum(f => f.FileSize);
                    
                    await libraryRepository.IncrementLibraryStatisticsAsync(
                        collection.LibraryId.Value,
                        collectionCount: 0, // Already incremented during library scan
                        mediaItemCount: mediaFiles.Count,
                        sizeBytes: totalSize);
                    
                    _logger.LogInformation("📊 Updated library {LibraryId} statistics: +{Count} media items, +{Size} bytes", 
                        collection.LibraryId.Value, mediaFiles.Count, totalSize);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Failed to update library statistics for collection {CollectionId}", collection.Id);
                    // Don't throw - collection scan completed successfully, this is just metadata
                }
            }
            
            // Update SCAN stage to Completed and initialize THUMBNAIL and CACHE stages
            if (!string.IsNullOrEmpty(scanMessage.JobId))
            {
                try
                {
                    // Complete scan stage
                    await backgroundJobService.UpdateJobStageAsync(
                        ObjectId.Parse(scanMessage.JobId), 
                        "scan", 
                        "Completed", 
                        mediaFiles.Count, 
                        mediaFiles.Count, 
                        $"Found {mediaFiles.Count} media files");
                    
                    _logger.LogInformation("✅ Scan stage completed for job {JobId}: {Count} files found", scanMessage.JobId, mediaFiles.Count);
                    
                    if (useDirectAccess)
                    {
                        // Direct mode: Mark thumbnail/cache stages as completed immediately
                        await backgroundJobService.UpdateJobStageAsync(
                            ObjectId.Parse(scanMessage.JobId), 
                            "thumbnail", 
                            "Completed", 
                            mediaFiles.Count, 
                            mediaFiles.Count, 
                            $"Using direct file access (no generation needed)");
                        
                        await backgroundJobService.UpdateJobStageAsync(
                            ObjectId.Parse(scanMessage.JobId), 
                            "cache", 
                            "Completed", 
                            mediaFiles.Count, 
                            mediaFiles.Count, 
                            $"Using direct file access (no generation needed)");
                        
                        _logger.LogInformation("✅ Direct mode: All stages completed for job {JobId}", scanMessage.JobId);
                    }
                    else
                    {
                        // Standard mode: Initialize thumbnail/cache stages as Pending
                        await backgroundJobService.UpdateJobStageAsync(
                            ObjectId.Parse(scanMessage.JobId), 
                            "thumbnail", 
                            "Pending", 
                            0, 
                            mediaFiles.Count, 
                            $"Waiting to generate {mediaFiles.Count} thumbnails");
                        
                        await backgroundJobService.UpdateJobStageAsync(
                            ObjectId.Parse(scanMessage.JobId), 
                            "cache", 
                            "Pending", 
                            0, 
                            mediaFiles.Count, 
                            $"Waiting to generate {mediaFiles.Count} cache files");
                        
                        _logger.LogInformation("📝 Initialized stages for job {JobId} - Centralized JobMonitoringService will track completion", scanMessage.JobId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update job stages for {JobId}", scanMessage.JobId);
                }
            }
            } // Close using (scope) block
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error processing collection scan message: {Message}", message);
            
            // Log detailed exception information for debugging
            _logger.LogError("Exception Type: {ExceptionType}", ex.GetType().Name);
            _logger.LogError("Exception Message: {ExceptionMessage}", ex.Message);
            _logger.LogError("Stack Trace: {StackTrace}", ex.StackTrace);
            
            if (ex.InnerException != null)
            {
                _logger.LogError("Inner Exception: {InnerException}", ex.InnerException.Message);
            }
            
            // Don't re-throw - let the message be acknowledged as processed
            // This prevents message requeuing and connection corruption
            _logger.LogWarning("⚠️ Collection scan failed but message will be acknowledged to prevent requeuing");
        }
    }

    private List<MediaFileInfo> ScanCollectionForMediaFiles(string collectionPath, CollectionType collectionType)
    {
        var mediaFiles = new List<MediaFileInfo>();
        
        try
        {
            if (collectionType == CollectionType.Folder)
            {
                // Handle regular directories
                ScanDirectory(collectionPath, mediaFiles);
            }
            else
            {
                // Handle all compressed archive types (ZIP, 7Z, RAR, TAR, etc.)
                ScanCompressedArchive(collectionPath, mediaFiles);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error scanning collection path {Path}", collectionPath);
        }

        return mediaFiles;
    }

    private void ScanCompressedArchive(string archivePath, List<MediaFileInfo> mediaFiles)
    {
        try
        {
            // Use SharpCompress to support ZIP, 7Z, RAR, TAR, CBZ, CBR, and more
            using var archive = ArchiveFactory.Open(archivePath);
            foreach (var entry in archive.Entries)
            {
                // CRITICAL FIX: Filter out __MACOSX metadata entries to prevent broken collections
                if (!entry.IsDirectory && MacOSXFilterHelper.IsSafeToProcess(entry.Key, "collection scanning") && IsMediaFile(entry.Key))
                {
                    mediaFiles.Add(new MediaFileInfo
                    {
                        FullPath = $"{archivePath}#{entry.Key}",
                        RelativePath = entry.Key,
                        FileName = Path.GetFileName(entry.Key),
                        Extension = Path.GetExtension(entry.Key).ToLowerInvariant(),
                        FileSize = entry.Size
                    });
                }
            }
            
            _logger.LogDebug("📦 Scanned archive {Archive}: found {Count} media files", 
                Path.GetFileName(archivePath), mediaFiles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error scanning compressed archive {ArchivePath}", archivePath);
        }
    }

    private void ScanDirectory(string directoryPath, List<MediaFileInfo> mediaFiles)
    {
        try
        {
            // REQUIREMENT: Only scan DIRECT files in the collection folder
            // DO NOT include files from subfolders or zip files
            // Subfolders and zip files are separate collections
            var files = Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                // Skip zip files - they are standalone collections, not part of this folder collection
                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension == ".zip" || extension == ".cbz" || extension == ".cbr" || 
                    extension == ".7z" || extension == ".rar" || extension == ".tar")
                {
                    _logger.LogDebug("Skipping zip file {File} - it's a standalone collection", file);
                    continue;
                }
                
                if (IsMediaFile(file))
                {
                    var fileInfo = new FileInfo(file);
                    mediaFiles.Add(new MediaFileInfo
                    {
                        FullPath = file,
                        RelativePath = Path.GetRelativePath(directoryPath, file),
                        FileName = fileInfo.Name,
                        Extension = fileInfo.Extension.ToLowerInvariant(),
                        FileSize = fileInfo.Length
                    });
                }
            }
            
            _logger.LogDebug("📁 Scanned directory {Directory}: found {Count} direct media files (excluding subfolders and zip files)", 
                directoryPath, mediaFiles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error scanning directory {DirectoryPath}", directoryPath);
        }
    }

    private static bool IsMediaFile(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".webp", ".svg" };
        return supportedExtensions.Contains(extension);
    }

    private async Task<(int width, int height)> ExtractImageDimensions(ArchiveEntryInfo archiveEntry)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var imageProcessingService = scope.ServiceProvider.GetRequiredService<IImageProcessingService>();
            
            // For ZIP files, we can't easily extract dimensions without extracting the file
            if (!archiveEntry.IsDirectory)
            {
                _logger.LogDebug("📦 Archive entry detected, skipping dimension extraction for {Path}#{Entry}", archiveEntry.ArchivePath, archiveEntry.EntryName);
                return (0, 0); // Will be extracted during image processing
            }
            
            // For regular files, extract dimensions using IImageProcessingService
            if (File.Exists(archiveEntry.GetPhysicalFileFullPath()))
            {
                var dimensions = await imageProcessingService.GetImageDimensionsAsync(archiveEntry);
                if (dimensions != null && dimensions.Width > 0 && dimensions.Height > 0)
                {
                    _logger.LogDebug("📊 Extracted dimensions for {Path}: {Width}x{Height}", 
                        archiveEntry.GetPhysicalFileFullPath(), dimensions.Width, dimensions.Height);
                    return (dimensions.Width, dimensions.Height);
                }
            }
            
            return (0, 0); // Default to 0, will be determined during processing
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Failed to extract dimensions for {Path}#{Entry}, will be determined during processing", archiveEntry.ArchivePath, archiveEntry.EntryName);
            return (0, 0);
        }
    }

    // Monitoring is now handled by centralized JobMonitoringService
    // No need for per-job monitoring tasks that can fail or get disposed

    /// <summary>
    /// Process collection in direct file access mode (directory collections only)
    /// Creates image/thumbnail/cache entries that reference original files directly
    /// Skips cache/thumbnail generation entirely for massive speed and disk space savings
    /// </summary>
    private async Task ProcessDirectFileAccessMode(
        Domain.Entities.Collection collection,
        List<MediaFileInfo> mediaFiles,
        IServiceScope scope,
        IBackgroundJobService backgroundJobService,
        string? jobId)
    {
        try
        {
            _logger.LogInformation("📦 Processing {Count} files in direct file access mode for collection {Name}", 
                mediaFiles.Count, collection.Name);
            
            var collectionRepository = scope.ServiceProvider.GetRequiredService<ICollectionRepository>();
            var imagesToAdd = new List<ImageEmbedded>();
            var thumbnailsToAdd = new List<ThumbnailEmbedded>();
            var cacheImagesToAdd = new List<CacheImageEmbedded>();
            
            foreach (var mediaFile in mediaFiles)
            {
                try
                {
                    // Extract image dimensions
                    var archiveEntry = ArchiveEntryInfo.FromCollection(
                        collection.Path, 
                        collection.Type, 
                        mediaFile.FileName, 
                        mediaFile.FileSize);
                    var (width, height) = await ExtractImageDimensions(archiveEntry);
                    
                    // Create image embedded (no archiveEntry for directory files)
                    var image = new ImageEmbedded(
                        filename: mediaFile.FileName,
                        relativePath: mediaFile.RelativePath,
                        fileSize: mediaFile.FileSize,
                        width: width > 0 ? width : 0,
                        height: height > 0 ? height : 0,
                        format: mediaFile.Extension.TrimStart('.'));
                    
                    imagesToAdd.Add(image);
                    
                    // Get the generated image ID after adding to list
                    var imageId = image.Id;
                    var fullPath = Path.Combine(collection.Path, mediaFile.FileName);
                    
                    // Create direct reference thumbnail (points to original file)
                    var thumbnail = ThumbnailEmbedded.CreateDirectReference(
                        imageId: imageId,
                        originalFilePath: fullPath,
                        width: width > 0 ? width : 1920, // Use actual width or default
                        height: height > 0 ? height : 1080,
                        fileSize: mediaFile.FileSize,
                        format: mediaFile.Extension.TrimStart('.').ToLowerInvariant());
                    
                    thumbnailsToAdd.Add(thumbnail);
                    
                    // Create direct reference cache (points to original file)
                    var cacheImage = CacheImageEmbedded.CreateDirectReference(
                        imageId: imageId,
                        originalFilePath: fullPath,
                        width: width > 0 ? width : 1920,
                        height: height > 0 ? height : 1080,
                        fileSize: mediaFile.FileSize,
                        format: mediaFile.Extension.TrimStart('.').ToLowerInvariant());
                    
                    cacheImagesToAdd.Add(cacheImage);
                    
                    _logger.LogDebug("✅ Created direct references for {FileName} → {ImageId}", 
                        mediaFile.FileName, imageId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create direct references for {FileName}, skipping", 
                        mediaFile.FileName);
                }
            }
            
            // Atomically add all images, thumbnails, and cache references to collection
            _logger.LogInformation("💾 Adding {Count} images with direct references to collection {CollectionId}", 
                imagesToAdd.Count, collection.Id);
            
            // Add images one by one (no batch method available)
            foreach (var image in imagesToAdd)
            {
                await collectionRepository.AtomicAddImageAsync(collection.Id, image);
            }
            
            // Add thumbnails and cache images in batch
            await collectionRepository.AtomicAddThumbnailsAsync(collection.Id, thumbnailsToAdd);
            await collectionRepository.AtomicAddCacheImagesAsync(collection.Id, cacheImagesToAdd);
            
            _logger.LogInformation("✅ Direct mode complete: Added {ImageCount} images, {ThumbnailCount} thumbnails, {CacheCount} cache references to collection {Name}", 
                imagesToAdd.Count, thumbnailsToAdd.Count, cacheImagesToAdd.Count, collection.Name);
            _logger.LogInformation("💾 Disk space saved: ~{SavedGB:F2} GB (no cache/thumbnail copies generated)", 
                (imagesToAdd.Sum(i => i.FileSize) * 0.4) / 1024.0 / 1024.0 / 1024.0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error processing direct file access mode for collection {CollectionId}", collection.Id);
            throw;
        }
    }

    private class MediaFileInfo
    {
        public string FullPath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public long FileSize { get; set; }
    }
}