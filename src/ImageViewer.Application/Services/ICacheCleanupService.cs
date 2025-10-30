namespace ImageViewer.Application.Services;

/// <summary>
/// Service for cleaning up orphaned cache and thumbnail files
/// 缓存清理服务 - Dịch vụ dọn dẹp cache
/// </summary>
public interface ICacheCleanupService
{
    /// <summary>
    /// Cleanup orphaned cache files (files on disk but not in database)
    /// 清理孤立的缓存文件 - Dọn dẹp file cache mồ côi
    /// </summary>
    Task<int> CleanupOrphanedCacheFilesAsync(string cacheFolderPath, int olderThanDays = 7);
    
    /// <summary>
    /// Cleanup orphaned thumbnail files
    /// 清理孤立的缩略图文件 - Dọn dẹp file thumbnail mồ côi
    /// </summary>
    Task<int> CleanupOrphanedThumbnailFilesAsync(string cacheFolderPath, int olderThanDays = 7);
    
    /// <summary>
    /// Cleanup all orphaned files in a cache folder
    /// 清理缓存文件夹中的所有孤立文件 - Dọn dẹp tất cả file mồ côi
    /// </summary>
    Task<(int cacheFiles, int thumbnailFiles)> CleanupOrphanedFilesAsync(string cacheFolderPath, int olderThanDays = 7);
    
    /// <summary>
    /// Reconcile cache folder statistics with actual disk usage
    /// 核对缓存文件夹统计信息与实际磁盘使用 - Đối chiếu thống kê với dung lượng thực tế
    /// </summary>
    Task ReconcileCacheFolderStatisticsAsync(string cacheFolderId);

    /// <summary>
    /// Deduplicate thumbnails and cache entries for a specific collection
    /// 去重特定集合的缩略图和缓存条目 - Khử trùng lặp thumbnail và cache cho collection cụ thể
    /// </summary>
    Task<(int removedThumbnails, int removedCacheImages)> DeduplicateCollectionAsync(MongoDB.Bson.ObjectId collectionId);

    /// <summary>
    /// Deduplicate thumbnails and cache entries across all collections
    /// 去重所有集合的缩略图和缓存条目 - Khử trùng lặp thumbnail và cache cho tất cả collection
    /// </summary>
    Task<ImageViewer.Application.DTOs.Maintenance.DeduplicationSummaryDto> DeduplicateAllCollectionsAsync();
}

