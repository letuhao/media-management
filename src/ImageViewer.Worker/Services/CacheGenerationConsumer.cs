using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using ImageViewer.Domain.Events;
using ImageViewer.Domain.Entities;
using ImageViewer.Domain.Interfaces;
using ImageViewer.Domain.ValueObjects;
using ImageViewer.Infrastructure.Data;
using ImageViewer.Application.Services;
using ImageViewer.Application.DTOs.Cache;
using MongoDB.Bson;

namespace ImageViewer.Worker.Services;

/// <summary>
/// Consumer for cache generation messages
/// </summary>
public class CacheGenerationConsumer : BaseMessageConsumer
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly RabbitMQOptions _rabbitMQOptions;
    private int _processedCount = 0;
    private readonly object _counterLock = new object();

    public CacheGenerationConsumer(
        IConnection connection,
        IOptions<RabbitMQOptions> options,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<CacheGenerationConsumer> logger)
        : base(connection, options, logger, "cache.generation", "cache-generation-consumer")
    {
        _serviceScopeFactory = serviceScopeFactory;
        _rabbitMQOptions = options.Value;
    }

    protected override async Task ProcessMessageAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            // Check if cancellation requested
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("⚠️ Cancellation requested, skipping cache generation");
                return;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
            var cacheMessage = JsonSerializer.Deserialize<CacheGenerationMessage>(message, options);
            if (cacheMessage == null)
            {
                _logger.LogWarning("Failed to deserialize CacheGenerationMessage");
                return;
            }

            _logger.LogDebug("Processing cache generation for image {ImageId} ({Path}#{Entry})", 
                cacheMessage.ImageId, cacheMessage.ArchiveEntry.ArchivePath, cacheMessage.ArchiveEntry.EntryName);

            // Try to create scope, handle disposal gracefully
            IServiceScope? scope = null;
            try
            {
                scope = _serviceScopeFactory.CreateScope();
            }
            catch (ObjectDisposedException)
            {
                _logger.LogWarning("⚠️ Service provider disposed, worker is shutting down. Skipping cache generation.");
                return;
            }

            using (scope)
            {
            var imageProcessingService = scope.ServiceProvider.GetRequiredService<IImageProcessingService>();
            var cacheService = scope.ServiceProvider.GetRequiredService<ICacheService>();
            var collectionRepository = scope.ServiceProvider.GetRequiredService<ICollectionRepository>();
            var settingsService = scope.ServiceProvider.GetRequiredService<IImageProcessingSettingsService>();
            
            // Update progress heartbeat to show job is actively processing
            if (!string.IsNullOrEmpty(cacheMessage.JobId))
            {
                try
                {
                    var jobStateRepository = scope.ServiceProvider.GetRequiredService<IFileProcessingJobStateRepository>();
                    await jobStateRepository.UpdateStatusAsync(cacheMessage.JobId, "Running");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to update progress heartbeat for job {JobId}", cacheMessage.JobId);
                }
            }
            
            // Get format from settings
            var format = await settingsService.GetCacheFormatAsync();

            // Use PRE-DETERMINED cache path from message (set during job creation for distribution)
            // OR fallback to dynamic determination if not set
            var cachePath = cacheMessage.CachePath;
            
            if (string.IsNullOrEmpty(cachePath))
            {
                _logger.LogDebug("Cache path not pre-determined, selecting cache folder now for image {ImageId}", cacheMessage.ImageId);
                // Fallback: Determine cache path dynamically (old behavior)
                cachePath = await DetermineCachePath(cacheMessage, cacheService, format);
                
                if (string.IsNullOrEmpty(cachePath))
                {
                    _logger.LogWarning("❌ Could not determine cache path for image {ImageId}", cacheMessage.ImageId);
                    return;
                }
            }
            else
            {
                _logger.LogDebug("✅ Using pre-determined cache path: {CachePath}", cachePath);
            }

            // Validate source image file size (prevent OOM on huge images)
            long fileSize = 0;
            long maxSize = 0;
            
            if (!cacheMessage.ArchiveEntry.IsDirectory)
            {
                // ZIP entry - get uncompressed size without extraction
                fileSize = ArchiveFileHelper.GetArchiveEntrySize(cacheMessage.ArchiveEntry, _logger);
                maxSize = _rabbitMQOptions.MaxZipEntrySizeBytes; // 20GB for ZIP entries
                
                if (fileSize > maxSize)
                {
                    _logger.LogWarning("⚠️ ZIP entry too large ({SizeGB}GB), skipping cache generation for {ImageId}", 
                        fileSize / 1024.0 / 1024.0 / 1024.0, cacheMessage.ImageId);
                    
                    if (!string.IsNullOrEmpty(cacheMessage.JobId))
                    {
                        var jobStateRepository = scope.ServiceProvider.GetRequiredService<IFileProcessingJobStateRepository>();
                        _logger.LogError("ZIP entry too large: {SizeGB}GB (max {MaxGB}GB) for {ImageId}", 
                            fileSize / 1024.0 / 1024.0 / 1024.0, maxSize / 1024.0 / 1024.0 / 1024.0, cacheMessage.ImageId);
                        await jobStateRepository.AtomicIncrementFailedAsync(cacheMessage.JobId, cacheMessage.ImageId);
                    }
                    
                    return;
                }
            }
            else
            {
                // Regular file - check file size on disk
                var imageFile = new FileInfo(cacheMessage.ArchiveEntry.GetPhysicalFileFullPath());
                fileSize = imageFile.Exists ? imageFile.Length : 0;
                maxSize = _rabbitMQOptions.MaxImageSizeBytes; // 500MB for regular files
                
                if (fileSize > maxSize)
                {
                    _logger.LogWarning("⚠️ Image file too large ({SizeMB}MB), skipping cache generation for {ImageId}", 
                        fileSize / 1024.0 / 1024.0, cacheMessage.ImageId);
                    
                    if (!string.IsNullOrEmpty(cacheMessage.JobId))
                    {
                        var jobStateRepository = scope.ServiceProvider.GetRequiredService<IFileProcessingJobStateRepository>();
                        _logger.LogError("Image file too large: {SizeMB}MB (max {MaxMB}MB) for {ImageId}", 
                            fileSize / 1024.0 / 1024.0, maxSize / 1024.0 / 1024.0, cacheMessage.ImageId);
                        await jobStateRepository.AtomicIncrementFailedAsync(cacheMessage.JobId, cacheMessage.ImageId);
                    }
                    
                    return;
                }
            }

            // Check if cache already exists on disk and force regeneration is disabled
            if (!cacheMessage.ForceRegenerate && File.Exists(cachePath))
            {
                _logger.LogDebug("📁 Cache file already exists on disk for image {ImageId}, re-adding to collection without regeneration", cacheMessage.ImageId);
                
                // CRITICAL: Even if file exists on disk, we need to add it to collection if it's missing from the array
                // This handles Resume Incomplete scenario where cache array was cleared but disk files still exist
                var collectionId = ObjectId.Parse(cacheMessage.CollectionId);
                var collection = await collectionRepository.GetByIdAsync(collectionId);
                
                if (collection != null)
                {
                    // Check if cache entry already exists in collection
                    var existingCache = collection.CacheImages?.FirstOrDefault(c => c.ImageId == cacheMessage.ImageId);
                    
                    if (existingCache == null)
                    {
                        // Cache file exists on disk but not in collection - add the entry
                        _logger.LogInformation("📝 Re-adding existing cache file to collection for image {ImageId}", cacheMessage.ImageId);
                        
                        var fileInfo = new FileInfo(cachePath);
                        var cacheImage = new CacheImageEmbedded(
                            cacheMessage.ImageId,
                            cachePath,
                            cacheMessage.CacheWidth,
                            cacheMessage.CacheHeight,
                            fileInfo.Length,
                            fileInfo.Extension.TrimStart('.').ToUpperInvariant(),
                            cacheMessage.Quality
                        );
                        
                        await collectionRepository.AtomicAddCacheImageAsync(collectionId, cacheImage);
                    }
                    else
                    {
                        _logger.LogDebug("Cache entry already exists in collection for image {ImageId}, true skip", cacheMessage.ImageId);
                    }
                }
                
                // Track as skipped in FileProcessingJobState
                if (!string.IsNullOrEmpty(cacheMessage.JobId))
                {
                    var jobStateRepository = scope.ServiceProvider.GetRequiredService<IFileProcessingJobStateRepository>();
                    await jobStateRepository.AtomicIncrementSkippedAsync(cacheMessage.JobId, cacheMessage.ImageId);
                }
                
                // CRITICAL: Update job stage progress for skipped files
                if (!string.IsNullOrEmpty(cacheMessage.ScanJobId))
                {
                    try
                    {
                        var bgJobService = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
                        await bgJobService.IncrementJobStageProgressAsync(
                            ObjectId.Parse(cacheMessage.ScanJobId),
                            "cache",
                            incrementBy: 1);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to update job stage for skipped cache {ImageId}", cacheMessage.ImageId);
                    }
                }
                
                return;
            }

            // Smart quality adjustment: avoid degrading low-quality source images
            int adjustedQuality = await DetermineOptimalCacheQuality(
                cacheMessage, 
                imageProcessingService, 
                cancellationToken);
            
            if (adjustedQuality != cacheMessage.Quality)
            {
                _logger.LogDebug("🎨 Adjusted cache quality from {RequestedQuality} to {AdjustedQuality} based on source image analysis", 
                    cacheMessage.Quality, adjustedQuality);
            }
            
            // Generate cache image
            byte[] cacheImageData;
            
            // Check if we should preserve original (no resize)
            if (cacheMessage.PreserveOriginal || cacheMessage.Format == "original")
            {
                _logger.LogDebug("Preserving original quality for image {ImageId} (no resize)", cacheMessage.ImageId);
                
                // Handle ZIP entries - extract bytes
                if (!cacheMessage.ArchiveEntry.IsDirectory)
                {
                    var imageBytes = await ArchiveFileHelper.ExtractZipEntryBytes(cacheMessage.ArchiveEntry, null, cancellationToken);
                    if (imageBytes == null || imageBytes.Length == 0)
                    {
                        _logger.LogWarning("❌ Failed to extract ZIP entry for cache: {Path}#{Entry}", cacheMessage.ArchiveEntry.ArchivePath, cacheMessage.ArchiveEntry.EntryName);
                        return;
                    }
                    cacheImageData = imageBytes; // Use original bytes, no resize
                }
                else
                {
                    // Regular file - read original file
                    cacheImageData = await File.ReadAllBytesAsync(cacheMessage.ArchiveEntry.GetPhysicalFileFullPath(), cancellationToken);
                }
            }
            else
            {
                // Format already retrieved earlier from settingsService
                _logger.LogDebug("🎨 Using cache format: {Format}, quality: {Quality}", format, adjustedQuality);
                
                // Resize to cache dimensions with smart quality
                // Handle ZIP entries
                if (!cacheMessage.ArchiveEntry.IsDirectory)
                {
                    // Extract image bytes from ZIP
                    var imageBytes = await ArchiveFileHelper.ExtractZipEntryBytes(cacheMessage.ArchiveEntry, null, cancellationToken);
                    if (imageBytes == null || imageBytes.Length == 0)
                    {
                        _logger.LogWarning("❌ Failed to extract ZIP entry for cache: {Path}#{Entry}", cacheMessage.ArchiveEntry.ArchivePath, cacheMessage.ArchiveEntry.EntryName);
                        return;
                    }
                    
                    cacheImageData = await imageProcessingService.ResizeImageFromBytesAsync(
                        imageBytes,
                        cacheMessage.CacheWidth,
                        cacheMessage.CacheHeight,
                        format, // Use format from settings!
                        adjustedQuality, // Use adjusted quality!
                        cancellationToken);
                }
                else
                {
                    // Regular file
                    cacheImageData = await imageProcessingService.ResizeImageAsync(
                cacheMessage.ArchiveEntry,
                cacheMessage.CacheWidth,
                cacheMessage.CacheHeight,
                        format, // Use format from settings!
                        adjustedQuality, // Use adjusted quality!
                cancellationToken);
                }
            }

            // Ensure cache directory exists
            var cacheDir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            // Save cache image to file system
            await File.WriteAllBytesAsync(cachePath, cacheImageData, cancellationToken);

            // ATOMIC UPDATE: Increment cache folder size and file count to prevent race conditions
            var collectionObjectId = ObjectId.Parse(cacheMessage.CollectionId);
            await UpdateCacheFolderSizeAsync(cachePath, cacheImageData.Length, collectionObjectId);

            // Update cache info in database
            var backgroundJobService = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            await UpdateCacheInfoInDatabase(cacheMessage, cachePath, collectionRepository, backgroundJobService);
            
            // Track progress in FileProcessingJobState if jobId is present
            if (!string.IsNullOrEmpty(cacheMessage.JobId))
            {
                var jobStateRepository = scope.ServiceProvider.GetRequiredService<IFileProcessingJobStateRepository>();
                await jobStateRepository.AtomicIncrementCompletedAsync(
                    cacheMessage.JobId, 
                    cacheMessage.ImageId, 
                    cacheImageData.Length);
                
                // Check if job is complete and mark it as finished
                await CheckAndMarkJobComplete(cacheMessage.JobId, jobStateRepository);
            }

            _logger.LogDebug("✅ Cache generated for image {ImageId} at path {CachePath} with dimensions {Width}x{Height}", 
                cacheMessage.ImageId, cachePath, cacheMessage.CacheWidth, cacheMessage.CacheHeight);

            // Batched logging - log every 50 files
            int currentCount;
            lock (_counterLock)
            {
                _processedCount++;
                currentCount = _processedCount;
            }

            if (currentCount % 50 == 0)
            {
                _logger.LogInformation("✅ Generated {Count} cache files (latest: {ImageId})", currentCount, cacheMessage.ImageId);
            }
            else
            {
                _logger.LogDebug("Successfully generated cache for image {ImageId}", cacheMessage.ImageId);
            }
            } // Close using (scope) block
        }
        catch (Exception ex)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
            var cacheMsg = JsonSerializer.Deserialize<CacheGenerationMessage>(message, options);
            
            // Check if this is a corrupted/unsupported file error (should skip, not retry)
            bool isSkippableError = (ex is InvalidOperationException && (ex.Message.Contains("Failed to decode image") || 
                                                                        ex.Message.Contains("Failed to decode") ||
                                                                        ex.Message.Contains("Unable to decode") ||
                                                                        ex.Message.Contains("Cannot decode") ||
                                                                        ex.Message.Contains("corrupted") ||
                                                                        ex.Message.Contains("invalid image"))) ||
                                   (ex is DirectoryNotFoundException) ||
                                   (ex is FileNotFoundException) ||
                                   (ex is UnauthorizedAccessException) ||
                                   (ex is PathTooLongException) ||
                                   (ex is ArgumentException && ex.Message.Contains("Path")) ||
                                   (ex is NotSupportedException && ex.Message.Contains("format")) ||
                                   (ex is BadImageFormatException) ||
                                   (ex is InvalidDataException && ex.Message.Contains("archive"));
            
            if (isSkippableError)
            {
                _logger.LogWarning(ex, "⚠️ Skipping corrupted/unsupported image file: {Path}#{Entry}. This message will NOT be retried.", cacheMsg?.ArchiveEntry.ArchivePath, cacheMsg?.ArchiveEntry.EntryName);
                
                // Create dummy cache entry for failed processing
                if (!string.IsNullOrEmpty(cacheMsg?.JobId) && !string.IsNullOrEmpty(cacheMsg?.ImageId))
                {
                    try
                    {
                        using var scope = _serviceScopeFactory.CreateScope();
                        var collectionRepository = scope.ServiceProvider.GetRequiredService<ICollectionRepository>();
                        var jobStateRepository = scope.ServiceProvider.GetRequiredService<IFileProcessingJobStateRepository>();
                        
                        // Create dummy cache entry
                        var dummyCache = CacheImageEmbedded.CreateDummy(
                            cacheMsg.ImageId,
                            ex.Message,
                            ex.GetType().Name
                        );
                        
                        // Add dummy entry to collection
                        var collectionId = ObjectId.Parse(cacheMsg.CollectionId);
                        await collectionRepository.AtomicAddCacheImageAsync(collectionId, dummyCache);
                        
                        // Track as completed (not failed) since we handled it
                        await jobStateRepository.AtomicIncrementCompletedAsync(cacheMsg.JobId, cacheMsg.ImageId, 0);
                        
                        // Track the error type for statistics
                        await jobStateRepository.TrackErrorAsync(cacheMsg.JobId, ex.GetType().Name);
                        
                        await CheckAndMarkJobComplete(cacheMsg.JobId, jobStateRepository);
                        
                        _logger.LogInformation("✅ Created dummy cache entry for failed image {ImageId}: {Error}", 
                            cacheMsg.ImageId, ex.Message);
                    }
                    catch (Exception trackEx)
                    {
                        _logger.LogWarning(trackEx, "Failed to create dummy cache for image {ImageId} in job {JobId}", 
                            cacheMsg?.ImageId, cacheMsg?.JobId);
                    }
                }
            }
            else
            {
                _logger.LogError(ex, "❌ Error processing cache generation message: {Message}", message);
                
                // Log detailed exception information for debugging
                _logger.LogError("Exception Type: {ExceptionType}", ex.GetType().Name);
                _logger.LogError("Exception Message: {ExceptionMessage}", ex.Message);
                _logger.LogError("Stack Trace: {StackTrace}", ex.StackTrace);
                
                if (ex.InnerException != null)
                {
                    _logger.LogError("Inner Exception: {InnerException}", ex.InnerException.Message);
                }
            }
            
            // Track as failed in FileProcessingJobState
            if (cacheMsg != null && !string.IsNullOrEmpty(cacheMsg.JobId))
            {
                try
                {
                    using var errorScope = _serviceScopeFactory.CreateScope();
                    var jobStateRepository = errorScope.ServiceProvider.GetRequiredService<IFileProcessingJobStateRepository>();
                    await jobStateRepository.AtomicIncrementFailedAsync(cacheMsg.JobId, cacheMsg.ImageId);
                    
                    // Check failure threshold and alert if needed (every 10 failures)
                    var jobState = await jobStateRepository.GetByJobIdAsync(cacheMsg.JobId);
                    if (jobState != null && jobState.FailedImages % 10 == 0 && jobState.FailedImages > 0)
                    {
                        var alertService = errorScope.ServiceProvider.GetService<IJobFailureAlertService>();
                        if (alertService != null)
                        {
                            await alertService.CheckAndAlertAsync(cacheMsg.JobId, failureThreshold: 0.1);
                        }
                    }
                }
                catch (Exception trackEx)
                {
                    _logger.LogWarning(trackEx, "Failed to track error for image {ImageId} in job {JobId}", 
                        cacheMsg.ImageId, cacheMsg.JobId);
                }
            }
            
            // CRITICAL: Skip corrupted files (don't retry), but throw for other errors (network, disk, etc.)
            if (!isSkippableError)
            {
                throw;
            }
            
            // For skippable errors, we return without throwing = ACK the message (don't retry)
        }
    }

    private async Task<string?> DetermineCachePath(CacheGenerationMessage cacheMessage, ICacheService cacheService, string format)
    {
        try
        {
            // Determine file extension based on format
            var extension = format.ToLowerInvariant() switch
            {
                "jpeg" => ".jpg",
                "jpg" => ".jpg",
                "png" => ".png",
                "webp" => ".webp",
                "gif" => ".gif",
                "apng" => ".apng",
                "mp4" => ".mp4",
                "avi" => ".avi",
                "mov" => ".mov",
                "wmv" => ".wmv",
                "flv" => ".flv",
                "mkv" => ".mkv",
                "webm" => ".webm",
                "original" => Path.GetExtension(cacheMessage.ArchiveEntry.EntryName), // Preserve original extension
                _ => ".jpg" // Default fallback
            };
            
            // Use cache service to determine the proper cache path
            var cacheFolders = await cacheService.GetCacheFoldersAsync();
            if (!cacheFolders.Any())
            {
                _logger.LogWarning("⚠️ No cache folders configured, using default cache directory");
                return Path.Combine("cache", $"{cacheMessage.ImageId}_cache_{cacheMessage.CacheWidth}x{cacheMessage.CacheHeight}{extension}");
            }

            // Select cache folder using hash-based distribution for equal load balancing
            var collectionId = ObjectId.Parse(cacheMessage.CollectionId); // Convert string back to ObjectId
            var cacheFolder = SelectCacheFolderForEqualDistribution(cacheFolders, collectionId);
            
            // Create proper folder structure: CacheFolder/cache/CollectionId/ImageId_CacheWidthxCacheHeight.{ext}
            var collectionIdStr = cacheMessage.CollectionId; // Already a string
            var cacheDir = Path.Combine(cacheFolder.Path, "cache", collectionIdStr);
            var fileName = $"{cacheMessage.ImageId}_cache_{cacheMessage.CacheWidth}x{cacheMessage.CacheHeight}{extension}";
            
            _logger.LogDebug("📁 Selected cache folder {CacheFolderName} for collection {CollectionId}, image {ImageId} (format: {Format})", 
                cacheFolder.Name, collectionIdStr, cacheMessage.ImageId, format);
            return Path.Combine(cacheDir, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error determining cache path for image {ImageId}", cacheMessage.ImageId);
            return null;
        }
    }

    private CacheFolderDto SelectCacheFolderForEqualDistribution(IEnumerable<CacheFolderDto> cacheFolders, ObjectId collectionId)
    {
        // CRITICAL: Sort by Id to ensure consistent ordering across all calls
        // Without sorting, MongoDB's natural order can change, causing same collection
        // to be assigned to different folders after DB restart/rebuild
        var activeCacheFolders = cacheFolders
            .Where(cf => cf.IsActive)
            .OrderBy(cf => cf.Id) // ← STABLE ORDERING
            .ToList();
        
        if (!activeCacheFolders.Any())
        {
            throw new InvalidOperationException("No active cache folders available");
        }

        // Use hash-based distribution to ensure equal distribution across cache folders
        // This ensures the same collection always goes to the same cache folder (for consistency)
        // while distributing collections evenly across all available cache folders
        // NOTE: Use string hash of ObjectId instead of ObjectId.GetHashCode()
        // because ObjectId.GetHashCode() has poor distribution (clusters around certain values)
        var hash = Math.Abs(collectionId.ToString().GetHashCode());
        var selectedIndex = hash % activeCacheFolders.Count;
        var selectedFolder = activeCacheFolders[selectedIndex];
        
        _logger.LogDebug("🎯 Hash-based cache folder selection: CollectionId={CollectionId}, Hash={Hash}, Index={Index}, SelectedFolder={FolderName}", 
            collectionId, hash, selectedIndex, selectedFolder.Name);
        
        return selectedFolder;
    }

    private async Task UpdateCacheInfoInDatabase(CacheGenerationMessage cacheMessage, string cachePath, ICollectionRepository collectionRepository, IBackgroundJobService backgroundJobService)
    {
        try
        {
            _logger.LogDebug("📝 Updating cache info in database for image {ImageId}", cacheMessage.ImageId);
            
            // Convert string back to ObjectId for database operations
            var collectionId = ObjectId.Parse(cacheMessage.CollectionId);
            
            // Get the collection
            var collection = await collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                throw new InvalidOperationException($"Collection {collectionId} not found");
            }
            
            // Check if cache already exists for this image (prevent duplicates)
            var existingCache = collection.CacheImages?.FirstOrDefault(c => c.ImageId == cacheMessage.ImageId);
            if (existingCache != null)
            {
                _logger.LogDebug("Cache already exists for image {ImageId}, skipping", cacheMessage.ImageId);
                return;
            }
            
            // Create cache image embedded object
            var fileInfo = new FileInfo(cachePath);
            var cacheImage = new CacheImageEmbedded(
                cacheMessage.ImageId,
                cachePath,
                cacheMessage.CacheWidth,
                cacheMessage.CacheHeight,
                fileInfo.Length,
                fileInfo.Extension.TrimStart('.').ToUpperInvariant(),
                cacheMessage.Quality
            );
            
            // Atomically add cache image to collection (thread-safe, prevents race conditions!)
            var added = await collectionRepository.AtomicAddCacheImageAsync(collectionId, cacheImage);
            if (!added)
            {
                _logger.LogWarning("Failed to add cache image to collection {CollectionId} - collection might not exist", collectionId);
                return;
            }
            
            // REAL-TIME JOB TRACKING: Update job stage immediately after each cache
            if (!string.IsNullOrEmpty(cacheMessage.ScanJobId))
            {
                try
                {
                    await backgroundJobService.IncrementJobStageProgressAsync(
                        ObjectId.Parse(cacheMessage.ScanJobId),
                        "cache",
                        incrementBy: 1);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update job stage for {JobId}, fallback monitor will handle it", cacheMessage.ScanJobId);
                }
            }
            
            _logger.LogDebug("✅ Cache info updated for image {ImageId}: {CachePath}", 
                cacheMessage.ImageId, cachePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error updating cache info in database for image {ImageId}", cacheMessage.ImageId);
            throw;
        }
    }

    /// <summary>
    /// Determines optimal cache quality to avoid degrading low-quality source images
    /// </summary>
    private async Task<int> DetermineOptimalCacheQuality(
        CacheGenerationMessage cacheMessage, 
        IImageProcessingService imageProcessingService,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Extract image bytes for analysis
            byte[]? imageBytes = null;
            long fileSize = 0;
            int requestedQuality = cacheMessage.Quality;
            
            if (!cacheMessage.ArchiveEntry.IsDirectory)
            {
                imageBytes = await ArchiveFileHelper.ExtractZipEntryBytes(cacheMessage.ArchiveEntry, null, cancellationToken);
                fileSize = imageBytes?.Length ?? 0;
            }
            else if (File.Exists(cacheMessage.ArchiveEntry.GetPhysicalFileFullPath()))
            {
                var fileInfo = new FileInfo(cacheMessage.ArchiveEntry.GetPhysicalFileFullPath());
                fileSize = fileInfo.Length;
            }
            
            // Get image dimensions from metadata (we should have this from ImageProcessingConsumer)
            // For now, use file size as a proxy for quality estimation
            
            // Rule 1: If source is very small (likely low quality or highly compressed), don't use high quality
            // File size per pixel ratio estimation
            if (fileSize > 0 && imageBytes != null)
            {
                // Use SkiaSharp to analyze the image
                using var skImage = SkiaSharp.SKBitmap.Decode(imageBytes);
                if (skImage != null)
                {
                    var totalPixels = skImage.Width * skImage.Height;
                    var bytesPerPixel = (double)fileSize / totalPixels;
                    
                    // Estimate source quality based on bytes per pixel
                    // High quality JPEGs: > 2 bytes/pixel
                    // Medium quality: 1-2 bytes/pixel
                    // Low quality: < 1 byte/pixel
                    // Very low quality: < 0.5 bytes/pixel
                    
                    int estimatedSourceQuality;
                    if (bytesPerPixel >= 2.0)
                    {
                        estimatedSourceQuality = 95; // High quality source
                    }
                    else if (bytesPerPixel >= 1.0)
                    {
                        estimatedSourceQuality = 85; // Medium-high quality
                    }
                    else if (bytesPerPixel >= 0.5)
                    {
                        estimatedSourceQuality = 75; // Medium quality
                    }
                    else
                    {
                        estimatedSourceQuality = 60; // Low quality source
                    }
                    
                    // Don't use cache quality higher than source quality
                    // (no point compressing at 95% when source is already 60%)
                    if (requestedQuality > estimatedSourceQuality)
                    {
                        _logger.LogDebug("Source image appears to be {EstimatedQuality}% quality ({BytesPerPixel:F2} bytes/pixel), " +
                            "adjusting cache quality from {RequestedQuality}% to {AdjustedQuality}%",
                            estimatedSourceQuality, bytesPerPixel, requestedQuality, estimatedSourceQuality);
                        return estimatedSourceQuality;
                    }
                    
                    // Rule 2: If image is already smaller than cache target, preserve original quality
                    if (skImage.Width <= cacheMessage.CacheWidth && skImage.Height <= cacheMessage.CacheHeight)
                    {
                        _logger.LogDebug("Source image ({Width}x{Height}) is smaller than cache target ({CacheWidth}x{CacheHeight}), " +
                            "using quality 100 to preserve original",
                            skImage.Width, skImage.Height, cacheMessage.CacheWidth, cacheMessage.CacheHeight);
                        return 100; // Preserve original quality for small images
                    }
                }
            }
            
            // Default: use requested quality
            return requestedQuality;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze image quality for {Path}#{Entry}, using requested quality {Quality}", 
                cacheMessage.ArchiveEntry.ArchivePath, cacheMessage.ArchiveEntry.EntryName, cacheMessage.Quality);
            return cacheMessage.Quality; // Fallback to requested quality
        }
    }

    /// <summary>
    /// Atomically update cache folder size after saving cache image
    /// Uses MongoDB $inc operator to prevent race conditions during concurrent operations
    /// </summary>
    private async Task UpdateCacheFolderSizeAsync(string cachePath, long fileSize, ObjectId collectionId)
    {
        try
        {
            // Determine which cache folder was used based on the path
            using var scope = _serviceScopeFactory.CreateScope();
            var cacheFolderRepository = scope.ServiceProvider.GetRequiredService<ICacheFolderRepository>();
            
            // Extract cache folder path from cache path
            // CachePath format: {CacheFolderPath}/cache/{CollectionId}/{FileName}
            var cacheFileDir = Path.GetDirectoryName(cachePath);
            if (string.IsNullOrEmpty(cacheFileDir))
            {
                _logger.LogWarning("⚠️ Cannot determine cache folder from path: {Path}", cachePath);
                return;
            }
            
            // Get parent directory (remove CollectionId folder)
            var cacheDir = Path.GetDirectoryName(cacheFileDir);
            if (string.IsNullOrEmpty(cacheDir))
            {
                _logger.LogWarning("⚠️ Cannot determine cache directory from path: {Path}", cachePath);
                return;
            }
            
            // Get cache folder root (remove "cache" folder)
            var cacheFolderPath = Path.GetDirectoryName(cacheDir);
            if (string.IsNullOrEmpty(cacheFolderPath))
            {
                _logger.LogWarning("⚠️ Cannot determine cache folder root from path: {Path}", cachePath);
                return;
            }
            
            // Find cache folder by path
            var cacheFolder = await cacheFolderRepository.GetByPathAsync(cacheFolderPath);
            if (cacheFolder == null)
            {
                _logger.LogWarning("⚠️ Cache folder not found for path: {Path}", cacheFolderPath);
                return;
            }
            
            // ATOMIC INCREMENT: Thread-safe update in SINGLE transaction
            await cacheFolderRepository.IncrementCacheStatisticsAsync(cacheFolder.Id, fileSize, 1);
            await cacheFolderRepository.AddCachedCollectionAsync(cacheFolder.Id, collectionId.ToString());
            
            _logger.LogDebug("📊 Atomically incremented cache folder {Name} size by {Size} bytes, file count by 1 (single transaction)", 
                cacheFolder.Name, fileSize);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Failed to update cache folder statistics for cache image: {Path}", cachePath);
            // Don't throw - cache is already saved, this is just statistics
        }
    }

    /// <summary>
    /// Check if job is complete and mark it as finished if so
    /// </summary>
    private async Task CheckAndMarkJobComplete(string jobId, IFileProcessingJobStateRepository jobStateRepository)
    {
        try
        {
            var jobState = await jobStateRepository.GetByJobIdAsync(jobId);
            if (jobState == null) return;

            // Check if all expected images have been processed (completed + failed)
            var totalProcessed = jobState.CompletedImages + jobState.FailedImages;
            var totalExpected = jobState.TotalImages;

            if (totalExpected > 0 && totalProcessed >= totalExpected)
            {
                // Since we now create dummy entries for failed images, 
                // all images are "completed" (either successful or dummy)
                await jobStateRepository.UpdateStatusAsync(jobId, "Completed");
                
                // Update the main BackgroundJob with error statistics
                await UpdateBackgroundJobErrorStats(jobId, jobState);
                
                _logger.LogInformation("✅ Job {JobId} marked as Completed - {Completed}/{Expected} processed (including dummy entries for failed images)", 
                    jobId, jobState.CompletedImages, totalExpected);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check/mark job completion for {JobId}", jobId);
        }
    }

    /// <summary>
    /// Update the main BackgroundJob with error statistics
    /// </summary>
    private async Task UpdateBackgroundJobErrorStats(string jobId, FileProcessingJobState jobState)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var backgroundJobService = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            
            var successCount = jobState.CompletedImages - jobState.DummyEntryCount;
            var errorCount = jobState.DummyEntryCount;
            
            if (errorCount > 0)
            {
                // Job completed with errors
                await backgroundJobService.UpdateJobErrorStatisticsAsync(
                    ObjectId.Parse(jobId), 
                    successCount, 
                    errorCount, 
                    jobState.ErrorSummary
                );
            }
            else
            {
                // Job completed without errors
                await backgroundJobService.UpdateJobErrorStatisticsAsync(
                    ObjectId.Parse(jobId), 
                    successCount, 
                    0, 
                    null
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update BackgroundJob error statistics for {JobId}", jobId);
        }
    }
}
