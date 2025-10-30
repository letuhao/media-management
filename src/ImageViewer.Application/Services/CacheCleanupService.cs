using ImageViewer.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace ImageViewer.Application.Services;

/// <summary>
/// Implementation of cache cleanup service
/// </summary>
public class CacheCleanupService : ICacheCleanupService
{
    private readonly ICollectionRepository _collectionRepository;
    private readonly ICacheFolderRepository _cacheFolderRepository;
    private readonly ILogger<CacheCleanupService> _logger;

    public CacheCleanupService(
        ICollectionRepository collectionRepository,
        ICacheFolderRepository cacheFolderRepository,
        ILogger<CacheCleanupService> logger)
    {
        _collectionRepository = collectionRepository;
        _cacheFolderRepository = cacheFolderRepository;
        _logger = logger;
    }

    public async Task<int> CleanupOrphanedCacheFilesAsync(string cacheFolderPath, int olderThanDays = 7)
    {
        try
        {
            _logger.LogInformation("🧹 Cleaning up orphaned cache files in {CacheFolderPath} (older than {Days} days)", 
                cacheFolderPath, olderThanDays);

            var cacheDir = Path.Combine(cacheFolderPath, "cache");
            if (!Directory.Exists(cacheDir))
            {
                _logger.LogWarning("⚠️ Cache directory does not exist: {Path}", cacheDir);
                return 0;
            }

            var cutoffDate = DateTime.UtcNow.AddDays(-olderThanDays);
            var deletedCount = 0;
            var totalSize = 0L;

            // Get all collections to check against
            var collections = await _collectionRepository.GetAllAsync();
            var collectionsList = collections.ToList();

            // Scan all cache files
            var cacheFiles = Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories);
            
            foreach (var cacheFile in cacheFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(cacheFile);
                    
                    // Only cleanup old files
                    if (fileInfo.LastWriteTimeUtc > cutoffDate)
                    {
                        continue;
                    }

                    // Extract imageId from filename (format: {imageId}_cache_{width}x{height}.{ext})
                    var fileName = Path.GetFileNameWithoutExtension(cacheFile);
                    var parts = fileName.Split('_');
                    if (parts.Length < 2)
                    {
                        continue; // Invalid filename format
                    }

                    var imageId = parts[0];

                    // Check if this cache file exists in any collection's CacheImages
                    var isOrphaned = true;
                    foreach (var collection in collectionsList)
                    {
                        if (collection.CacheImages.Any(c => c.ImageId == imageId && c.CachePath == cacheFile))
                        {
                            isOrphaned = false;
                            break;
                        }
                    }

                    if (isOrphaned)
                    {
                        _logger.LogInformation("🗑️ Deleting orphaned cache file: {Path} (age: {Age} days)", 
                            cacheFile, (DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalDays);
                        
                        totalSize += fileInfo.Length;
                        File.Delete(cacheFile);
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "❌ Failed to check/delete cache file: {Path}", cacheFile);
                }
            }

            _logger.LogInformation("✅ Cleaned up {Count} orphaned cache files, freed {Size} bytes", 
                deletedCount, totalSize);

            return deletedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up orphaned cache files");
            return 0;
        }
    }

    public async Task<int> CleanupOrphanedThumbnailFilesAsync(string cacheFolderPath, int olderThanDays = 7)
    {
        try
        {
            _logger.LogInformation("🧹 Cleaning up orphaned thumbnail files in {CacheFolderPath} (older than {Days} days)", 
                cacheFolderPath, olderThanDays);

            var thumbnailDir = Path.Combine(cacheFolderPath, "thumbnails");
            if (!Directory.Exists(thumbnailDir))
            {
                _logger.LogWarning("⚠️ Thumbnail directory does not exist: {Path}", thumbnailDir);
                return 0;
            }

            var cutoffDate = DateTime.UtcNow.AddDays(-olderThanDays);
            var deletedCount = 0;
            var totalSize = 0L;

            // Get all collections to check against
            var collections = await _collectionRepository.GetAllAsync();
            var collectionsList = collections.ToList();

            // Scan all thumbnail files
            var thumbnailFiles = Directory.GetFiles(thumbnailDir, "*", SearchOption.AllDirectories);
            
            foreach (var thumbnailFile in thumbnailFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(thumbnailFile);
                    
                    // Only cleanup old files
                    if (fileInfo.LastWriteTimeUtc > cutoffDate)
                    {
                        continue;
                    }

                    // Extract imageId from filename (format: {imageId}_{width}x{height}.{ext})
                    var fileName = Path.GetFileNameWithoutExtension(thumbnailFile);
                    var parts = fileName.Split('_');
                    if (parts.Length < 2)
                    {
                        continue; // Invalid filename format
                    }

                    var imageId = parts[0];

                    // Check if this thumbnail exists in any collection's Thumbnails
                    var isOrphaned = true;
                    foreach (var collection in collectionsList)
                    {
                        if (collection.Thumbnails.Any(t => t.ImageId == imageId && t.ThumbnailPath == thumbnailFile))
                        {
                            isOrphaned = false;
                            break;
                        }
                    }

                    if (isOrphaned)
                    {
                        _logger.LogInformation("🗑️ Deleting orphaned thumbnail file: {Path} (age: {Age} days)", 
                            thumbnailFile, (DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalDays);
                        
                        totalSize += fileInfo.Length;
                        File.Delete(thumbnailFile);
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "❌ Failed to check/delete thumbnail file: {Path}", thumbnailFile);
                }
            }

            _logger.LogInformation("✅ Cleaned up {Count} orphaned thumbnail files, freed {Size} bytes", 
                deletedCount, totalSize);

            return deletedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up orphaned thumbnail files");
            return 0;
        }
    }

    public async Task<(int cacheFiles, int thumbnailFiles)> CleanupOrphanedFilesAsync(string cacheFolderPath, int olderThanDays = 7)
    {
        var cacheFiles = await CleanupOrphanedCacheFilesAsync(cacheFolderPath, olderThanDays);
        var thumbnailFiles = await CleanupOrphanedThumbnailFilesAsync(cacheFolderPath, olderThanDays);
        
        return (cacheFiles, thumbnailFiles);
    }

    public async Task ReconcileCacheFolderStatisticsAsync(string cacheFolderId)
    {
        try
        {
            _logger.LogInformation("🔄 Reconciling cache folder statistics for {CacheFolderId}", cacheFolderId);

            var cacheFolder = await _cacheFolderRepository.GetByIdAsync(ObjectId.Parse(cacheFolderId));
            if (cacheFolder == null)
            {
                _logger.LogWarning("⚠️ Cache folder {Id} not found", cacheFolderId);
                return;
            }

            // Scan actual disk usage
            var cacheDir = Path.Combine(cacheFolder.Path, "cache");
            var thumbnailDir = Path.Combine(cacheFolder.Path, "thumbnails");

            long actualSize = 0;
            int actualFileCount = 0;

            if (Directory.Exists(cacheDir))
            {
                var cacheFiles = Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories);
                foreach (var file in cacheFiles)
                {
                    var fileInfo = new FileInfo(file);
                    actualSize += fileInfo.Length;
                    actualFileCount++;
                }
            }

            if (Directory.Exists(thumbnailDir))
            {
                var thumbnailFiles = Directory.GetFiles(thumbnailDir, "*", SearchOption.AllDirectories);
                foreach (var file in thumbnailFiles)
                {
                    var fileInfo = new FileInfo(file);
                    actualSize += fileInfo.Length;
                    actualFileCount++;
                }
            }

            // Update statistics if discrepancy found
            if (actualSize != cacheFolder.CurrentSizeBytes || actualFileCount != cacheFolder.TotalFiles)
            {
                _logger.LogWarning("⚠️ Statistics mismatch for {Name}: " +
                    "Size (DB: {DbSize}, Disk: {ActualSize}), " +
                    "Files (DB: {DbFiles}, Disk: {ActualFiles})",
                    cacheFolder.Name,
                    cacheFolder.CurrentSizeBytes, actualSize,
                    cacheFolder.TotalFiles, actualFileCount);

                cacheFolder.UpdateStatistics(actualSize, actualFileCount);
                await _cacheFolderRepository.UpdateAsync(cacheFolder);

                _logger.LogInformation("✅ Reconciled cache folder {Name} statistics", cacheFolder.Name);
            }
            else
            {
                _logger.LogInformation("✅ Cache folder {Name} statistics are accurate", cacheFolder.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reconciling cache folder statistics");
        }
    }

    /// <summary>
    /// Deduplicate thumbnails and cacheImages arrays for a single collection by composite keys
    /// / Khử trùng lặp mảng thumbnails và cacheImages cho một collection bằng composite key
    /// </summary>
    public async Task<(int removedThumbnails, int removedCacheImages)> DeduplicateCollectionAsync(MongoDB.Bson.ObjectId collectionId)
    {
        var collection = await _collectionRepository.GetByIdAsync(collectionId);
        if (collection == null)
        {
            _logger.LogWarning("Collection {CollectionId} not found for deduplication", collectionId);
            return (0, 0);
        }

        var originalThumbs = collection.Thumbnails?.ToList() ?? new List<Domain.ValueObjects.ThumbnailEmbedded>();
        var originalCaches = collection.CacheImages?.ToList() ?? new List<Domain.ValueObjects.CacheImageEmbedded>();

        var distinctThumbs = originalThumbs
            .GroupBy(t => new { t.ImageId, t.Width, t.Height })
            .Select(g => g.First())
            .ToList();

        var distinctCaches = originalCaches
            .GroupBy(c => new { c.ImageId, c.Width, c.Height })
            .Select(g => g.First())
            .ToList();

        var removedThumbnails = originalThumbs.Count - distinctThumbs.Count;
        var removedCacheImages = originalCaches.Count - distinctCaches.Count;

        if (removedThumbnails > 0 || removedCacheImages > 0)
        {
            collection.Thumbnails = distinctThumbs;
            collection.CacheImages = distinctCaches;
            collection.UpdatedAt = DateTime.UtcNow;
            await _collectionRepository.UpdateAsync(collection);
            _logger.LogInformation("🧹 Deduplicated collection {CollectionId}: removed {Thumbs} thumbnails, {Caches} cache entries",
                collectionId, removedThumbnails, removedCacheImages);
        }

        return (removedThumbnails, removedCacheImages);
    }

    /// <summary>
    /// Deduplicate thumbnails and cacheImages across all collections.
    /// / Khử trùng lặp thumbnails và cacheImages trên tất cả collection
    /// </summary>
    public async Task<ImageViewer.Application.DTOs.Maintenance.DeduplicationSummaryDto> DeduplicateAllCollectionsAsync()
    {
        _logger.LogInformation("🧹 Starting global deduplication for thumbnails and cache images...");

        var collections = await _collectionRepository.GetAllAsync();
        var processed = 0;
        var totalThumbsRemoved = 0;
        var totalCachesRemoved = 0;
        var collectionsWithDuplicates = 0;
        var totalThumbDuplicatesFound = 0;
        var totalCacheDuplicatesFound = 0;
        var scanned = 0;

        foreach (var col in collections)
        {
            scanned++;
            // Compute duplicate counts prior to dedupe for reporting
            var thumbList = col.Thumbnails?.ToList() ?? new List<Domain.ValueObjects.ThumbnailEmbedded>();
            var cacheList = col.CacheImages?.ToList() ?? new List<Domain.ValueObjects.CacheImageEmbedded>();
            var thumbDistinct = thumbList
                .GroupBy(t => new { t.ImageId, t.Width, t.Height })
                .Select(g => g.First())
                .ToList();
            var cacheDistinct = cacheList
                .GroupBy(c => new { c.ImageId, c.Width, c.Height })
                .Select(g => g.First())
                .ToList();
            var thumbDupes = Math.Max(0, thumbList.Count - thumbDistinct.Count);
            var cacheDupes = Math.Max(0, cacheList.Count - cacheDistinct.Count);

            if (thumbDupes > 0 || cacheDupes > 0)
            {
                collectionsWithDuplicates++;
                totalThumbDuplicatesFound += thumbDupes;
                totalCacheDuplicatesFound += cacheDupes;
                _logger.LogInformation("📌 Duplicates detected in collection {CollectionId} ({Name}): thumbs={ThumbDupes}, cache={CacheDupes}", col.Id, col.Name, thumbDupes, cacheDupes);
            }

            var (rt, rc) = await DeduplicateCollectionAsync(col.Id);
            if (rt > 0 || rc > 0)
            {
                processed++;
                totalThumbsRemoved += rt;
                totalCachesRemoved += rc;
                _logger.LogInformation("🧹 Collection {CollectionId}: removed {Thumbs} thumbnails, {Caches} cache entries", col.Id, rt, rc);
            }
            else if (scanned % 100 == 0)
            {
                // Periodic heartbeat log for long runs
                _logger.LogInformation("⏱️ Deduplication progress: scanned {Scanned} collections, changes in {Processed}", scanned, processed);
            }
        }

        _logger.LogInformation("✅ Global deduplication complete: scanned={Scanned}, withDuplicates={WithDupes}, changed={Processed}, removed thumbs={Thumbs}, cache={Caches}",
            scanned, collectionsWithDuplicates, processed, totalThumbsRemoved, totalCachesRemoved);

        return new ImageViewer.Application.DTOs.Maintenance.DeduplicationSummaryDto
        {
            CollectionsScanned = scanned,
            CollectionsWithDuplicates = collectionsWithDuplicates,
            CollectionsProcessed = processed,
            TotalThumbDuplicatesFound = totalThumbDuplicatesFound,
            TotalCacheDuplicatesFound = totalCacheDuplicatesFound,
            TotalThumbsRemoved = totalThumbsRemoved,
            TotalCachesRemoved = totalCachesRemoved
        };
    }
}

