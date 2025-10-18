using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace ImageViewer.Application.Services;

/// <summary>
/// Implementation of cache folder selection service with smart distribution
/// 中文：缓存文件夹选择服务实现
/// Tiếng Việt: Triển khai dịch vụ chọn thư mục bộ nhớ cache
/// </summary>
public class CacheFolderSelectionService : ICacheFolderSelectionService
{
    private readonly ICacheService _cacheService;
    private readonly ILogger<CacheFolderSelectionService> _logger;

    public CacheFolderSelectionService(
        ICacheService cacheService,
        ILogger<CacheFolderSelectionService> logger)
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> SelectCacheFolderForCacheAsync(ObjectId collectionId, string imageId, int cacheWidth, int cacheHeight, string format)
    {
        try
        {
            _logger.LogDebug("🔧 CacheFolderSelectionService: Received format: '{Format}' for image {ImageId}", format, imageId);
            
            // Determine file extension based on format
            var extension = format.ToLowerInvariant() switch
            {
                "jpeg" => ".jpg",
                "jpg" => ".jpg",
                "png" => ".png",
                "webp" => ".webp",
                _ => ".jpg" // Default fallback
            };
            
            _logger.LogDebug("🔧 CacheFolderSelectionService: Generated extension: '{Extension}' for format: '{Format}'", extension, format);

            // Get all cache folders
            var cacheFolders = await _cacheService.GetCacheFoldersAsync();
            
            // CRITICAL: Sort by Id to ensure consistent ordering across all calls
            // Without sorting, MongoDB's natural order can change, causing same collection
            // to be assigned to different folders after DB restart/rebuild
            var cacheFoldersList = cacheFolders
                .Where(cf => cf.IsActive)
                .OrderBy(cf => cf.Id) // ← STABLE ORDERING
                .ToList();

            if (cacheFoldersList.Count == 0)
            {
                _logger.LogWarning("⚠️ No active cache folders configured, using default cache directory");
                return Path.Combine("cache", $"{imageId}_cache_{cacheWidth}x{cacheHeight}{extension}");
            }

            // Use hash-based distribution to select cache folder
            // This ensures the same collection always goes to the same cache folder
            // NOTE: Use string hash of ObjectId instead of ObjectId.GetHashCode()
            // because ObjectId.GetHashCode() has poor distribution (clusters around certain values)
            var hash = Math.Abs(collectionId.ToString().GetHashCode());
            var selectedIndex = hash % cacheFoldersList.Count;
            var selectedCacheFolder = cacheFoldersList[selectedIndex];

            // Create proper folder structure: CacheFolder/cache/CollectionId/ImageId_CacheWidthxCacheHeight.{ext}
            var collectionIdStr = collectionId.ToString();
            var cacheDir = Path.Combine(selectedCacheFolder.Path, "cache", collectionIdStr);
            var fileName = $"{imageId}_cache_{cacheWidth}x{cacheHeight}{extension}";

            var fullPath = Path.Combine(cacheDir, fileName);

            _logger.LogDebug("📁 Selected cache folder {CacheFolderName} for collection {CollectionId}, image {ImageId} (format: {Format})",
                selectedCacheFolder.Name, collectionIdStr, imageId, format);

            return fullPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error selecting cache folder for image {ImageId}", imageId);
            return null;
        }
    }

    public async Task<string?> SelectCacheFolderForThumbnailAsync(ObjectId collectionId, string imageId, int width, int height, string format)
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
                _ => ".jpg" // Default fallback
            };

            // Get all cache folders
            var cacheFolders = await _cacheService.GetCacheFoldersAsync();
            
            // CRITICAL: Sort by Id to ensure consistent ordering across all calls
            // Without sorting, MongoDB's natural order can change, causing same collection
            // to be assigned to different folders after DB restart/rebuild
            var cacheFoldersList = cacheFolders
                .Where(cf => cf.IsActive)
                .OrderBy(cf => cf.Id) // ← STABLE ORDERING
                .ToList();

            if (cacheFoldersList.Count == 0)
            {
                _logger.LogWarning("⚠️ No active cache folders configured");
                return null;
            }

            // Use hash-based distribution to select cache folder
            // NOTE: Use string hash of ObjectId instead of ObjectId.GetHashCode()
            // because ObjectId.GetHashCode() has poor distribution (clusters around certain values)
            var hash = Math.Abs(collectionId.ToString().GetHashCode());
            var selectedIndex = hash % cacheFoldersList.Count;
            var selectedCacheFolder = cacheFoldersList[selectedIndex];

            // Create proper folder structure: CacheFolder/thumbnails/CollectionId/ImageId_WidthxHeight.{ext}
            var collectionIdStr = collectionId.ToString();
            var thumbnailDir = Path.Combine(selectedCacheFolder.Path, "thumbnails", collectionIdStr);
            var thumbnailFileName = $"{imageId}_{width}x{height}{extension}";

            var fullPath = Path.Combine(thumbnailDir, thumbnailFileName);

            _logger.LogDebug("📁 Selected cache folder {CacheFolderName} for thumbnail (format: {Format})",
                selectedCacheFolder.Name, format);

            return fullPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error selecting cache folder for thumbnail {ImageId}", imageId);
            return null;
        }
    }
}

