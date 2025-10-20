using ImageViewer.Domain.Entities;
using ImageViewer.Domain.ValueObjects;
using MongoDB.Bson;

namespace ImageViewer.Domain.Interfaces;

/// <summary>
/// Service for managing Redis-based sorted collection index for fast navigation and sibling queries.
/// Index structure:
/// - Sorted Sets: One per sort field (updatedAt, createdAt, name, imageCount, totalSize) x 2 directions (asc/desc)
/// - Hash: Collection summary data (id, name, firstImageId, firstImageThumbnailUrl, imageCount, etc.)
/// </summary>
public interface ICollectionIndexService
{
    /// <summary>
    /// Rebuild entire collection index from database.
    /// Called on startup or manually via API.
    /// Uses ChangedOnly mode by default for performance.
    /// </summary>
    Task RebuildIndexAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Rebuild collection index with specified mode and options.
    /// Supports smart incremental rebuilds for better performance.
    /// </summary>
    Task<RebuildStatistics> RebuildIndexAsync(
        RebuildMode mode,
        RebuildOptions? options = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Verify index consistency and optionally fix issues.
    /// Checks MongoDB vs Redis and removes orphaned entries.
    /// </summary>
    Task<VerifyResult> VerifyIndexAsync(
        bool dryRun = true,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Get collection index state (when it was last indexed, counts, etc.)
    /// </summary>
    Task<CollectionIndexState?> GetCollectionIndexStateAsync(
        ObjectId collectionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fix archive entry paths for collections with incorrect folder structure.
    /// Scans archive collections and corrects entryName/entryPath to include folder structure.
    /// </summary>
    Task<ArchiveEntryFixResult> FixArchiveEntryPathsAsync(
        bool dryRun = true,
        int? limit = null,
        string? collectionId = null,
        string? fixMode = null,
        bool onlyCorrupted = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Add or update a single collection in the index.
    /// Called when collection is created or updated.
    /// </summary>
    Task AddOrUpdateCollectionAsync(Collection collection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a collection from the index.
    /// Called when collection is deleted.
    /// </summary>
    Task RemoveCollectionAsync(ObjectId collectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get collection navigation (previous/next) with accurate position.
    /// Uses Redis sorted set ZRANK for O(1) position lookup.
    /// </summary>
    Task<CollectionNavigationResult> GetNavigationAsync(
        ObjectId collectionId, 
        string sortBy = "updatedAt", 
        string sortDirection = "desc", 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get collection siblings with pagination.
    /// Uses Redis sorted set ZRANGE for O(log(N)+M) sibling lookup.
    /// </summary>
    Task<CollectionSiblingsResult> GetSiblingsAsync(
        ObjectId collectionId, 
        int page = 1, 
        int pageSize = 20, 
        string sortBy = "updatedAt", 
        string sortDirection = "desc", 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if index exists and is up-to-date.
    /// </summary>
    Task<bool> IsIndexValidAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get index statistics (total collections, last rebuild time, etc.)
    /// </summary>
    Task<CollectionIndexStats> GetIndexStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get paginated collections (for collection list page).
    /// Uses Redis sorted set ZRANGE for fast pagination.
    /// </summary>
    Task<CollectionPageResult> GetCollectionPageAsync(
        int page,
        int pageSize,
        string sortBy = "updatedAt",
        string sortDirection = "desc",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Search collections by name/path with pagination and sorting.
    /// Searches ALL collections (not just current page) then applies sorting and pagination.
    /// </summary>
    Task<CollectionPageResult> SearchCollectionPageAsync(
        string searchQuery,
        int page,
        int pageSize,
        string sortBy = "updatedAt",
        string sortDirection = "desc",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get collections by library with pagination and sorting.
    /// Uses secondary index: collection_index:by_library:{libraryId}:sorted:{field}:{direction}
    /// </summary>
    Task<CollectionPageResult> GetCollectionsByLibraryAsync(
        ObjectId libraryId,
        int page,
        int pageSize,
        string sortBy = "updatedAt",
        string sortDirection = "desc",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get collections by type with pagination and sorting.
    /// Uses secondary index: collection_index:by_type:{type}:sorted:{field}:{direction}
    /// </summary>
    Task<CollectionPageResult> GetCollectionsByTypeAsync(
        int collectionType,
        int page,
        int pageSize,
        string sortBy = "updatedAt",
        string sortDirection = "desc",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get total collections count (O(1) operation).
    /// Uses Redis ZCARD for instant count.
    /// </summary>
    Task<int> GetTotalCollectionsCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get collections count by library (O(1) operation).
    /// </summary>
    Task<int> GetCollectionsCountByLibraryAsync(ObjectId libraryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get collections count by type (O(1) operation).
    /// </summary>
    Task<int> GetCollectionsCountByTypeAsync(int collectionType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get cached thumbnail for collection (WebP format).
    /// Returns null if not cached.
    /// </summary>
    Task<byte[]?> GetCachedThumbnailAsync(ObjectId collectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cache thumbnail for collection (WebP format).
    /// Sets expiration to 30 days by default.
    /// </summary>
    Task SetCachedThumbnailAsync(ObjectId collectionId, byte[] thumbnailData, TimeSpan? expiration = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch cache thumbnails for multiple collections.
    /// Used during index rebuild for performance.
    /// </summary>
    Task BatchCacheThumbnailsAsync(Dictionary<ObjectId, byte[]> thumbnails, TimeSpan? expiration = null, CancellationToken cancellationToken = default);

    #region Dashboard Statistics

    /// <summary>
    /// Get dashboard statistics from Redis cache (ultra-fast)
    /// </summary>
    Task<DashboardStatistics?> GetDashboardStatisticsAsync();

    /// <summary>
    /// Store dashboard statistics in Redis cache
    /// </summary>
    Task StoreDashboardStatisticsAsync(DashboardStatistics statistics);

    /// <summary>
    /// Update dashboard statistics incrementally
    /// </summary>
    Task UpdateDashboardStatisticsAsync(string updateType, object updateData);

    /// <summary>
    /// Get recent dashboard activity
    /// </summary>
    Task<List<object>> GetRecentDashboardActivityAsync(int limit = 10);

    /// <summary>
    /// Check if dashboard statistics are fresh (not expired)
    /// </summary>
    Task<bool> IsDashboardStatisticsFreshAsync();

    #endregion
}

/// <summary>
/// Result of navigation query
/// </summary>
public class CollectionNavigationResult
{
    public string? PreviousCollectionId { get; set; }
    public string? NextCollectionId { get; set; }
    public int CurrentPosition { get; set; }
    public int TotalCollections { get; set; }
    public bool HasPrevious { get; set; }
    public bool HasNext { get; set; }
}

/// <summary>
/// Result of siblings query with collection summaries
/// </summary>
public class CollectionSiblingsResult
{
    public List<CollectionSummary> Siblings { get; set; } = new();
    public int CurrentPosition { get; set; }
    public int CurrentPage { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
}

/// <summary>
/// Result of paginated collection query
/// </summary>
public class CollectionPageResult
{
    public List<CollectionSummary> Collections { get; set; } = new();
    public int CurrentPage { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public bool HasNext { get; set; }
    public bool HasPrevious { get; set; }
}

/// <summary>
/// Lightweight collection summary stored in Redis hash
/// </summary>
public class CollectionSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? FirstImageId { get; set; }
    public string? FirstImageThumbnailUrl { get; set; }
    public int ImageCount { get; set; }
    public int ThumbnailCount { get; set; }
    public int CacheCount { get; set; }
    public long TotalSize { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // Additional fields for filtering and display
    public string LibraryId { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Type { get; set; } // CollectionType enum as int
    public List<string> Tags { get; set; } = new();
    public string Path { get; set; } = string.Empty;
    
    /// <summary>
    /// Pre-computed base64-encoded thumbnail data URL (e.g., data:image/jpeg;base64,...)
    /// Cached during index build for instant display without conversion overhead
    /// </summary>
    public string? ThumbnailBase64 { get; set; }
}

/// <summary>
/// Index statistics
/// </summary>
public class CollectionIndexStats
{
    public int TotalCollections { get; set; }
    public DateTime? LastRebuildTime { get; set; }
    public bool IsValid { get; set; }
    public Dictionary<string, int> SortedSetSizes { get; set; } = new();
}

/// <summary>
/// Archive entry fix result
/// </summary>
public class ArchiveEntryFixResult
{
    public int TotalCollectionsScanned { get; set; }
    public int CollectionsWithIssues { get; set; }
    public int ImagesFixed { get; set; }
    public List<string> FixedCollectionIds { get; set; } = new();
    public List<string> ErrorMessages { get; set; } = new();
    public TimeSpan Duration { get; set; }
    public bool DryRun { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
}

/// <summary>
/// Rebuild mode selection
/// </summary>
public enum RebuildMode
{
    /// <summary>
    /// Only rebuild collections where UpdatedAt > IndexedAt (DEFAULT, fastest for daily use)
    /// </summary>
    ChangedOnly,
    
    /// <summary>
    /// Verify consistency between MongoDB and Redis, fix issues (cleanup orphaned entries)
    /// </summary>
    Verify,
    
    /// <summary>
    /// Clear ALL Redis data and rebuild everything from scratch
    /// </summary>
    Full,
    
    /// <summary>
    /// Rebuild ALL collections without clearing Redis (for schema changes)
    /// </summary>
    ForceRebuildAll
}

/// <summary>
/// Options for rebuild operation
/// </summary>
public class RebuildOptions
{
    /// <summary>
    /// Skip loading thumbnail files and converting to base64 (faster, but collection cards won't show thumbnails)
    /// </summary>
    public bool SkipThumbnailCaching { get; set; } = false;
    
    /// <summary>
    /// Only analyze and report what would be rebuilt, don't actually rebuild
    /// </summary>
    public bool DryRun { get; set; } = false;
}

/// <summary>
/// Statistics from rebuild operation
/// </summary>
public class RebuildStatistics
{
    public RebuildMode Mode { get; set; }
    public int TotalCollections { get; set; }
    public int SkippedCollections { get; set; }
    public int RebuiltCollections { get; set; }
    public TimeSpan Duration { get; set; }
    public long MemoryPeakMB { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
}

/// <summary>
/// Result from verify operation
/// </summary>
public class VerifyResult
{
    public bool IsConsistent { get; set; }
    public int TotalInMongoDB { get; set; }
    public int TotalInRedis { get; set; }
    public int ToAdd { get; set; }
    public int ToUpdate { get; set; }
    public int ToRemove { get; set; }
    public List<string> MissingInRedis { get; set; } = new();
    public List<string> OutdatedInRedis { get; set; } = new();
    public List<string> MissingThumbnails { get; set; } = new();
    public List<string> OrphanedInRedis { get; set; } = new();
    public TimeSpan Duration { get; set; }
    public bool DryRun { get; set; }
}

/// <summary>
/// Collection index state - tracks when collection was last indexed and its state
/// Stored in Redis with key: collection_index:state:{collectionId}
/// </summary>
public class CollectionIndexState
{
    public string CollectionId { get; set; } = string.Empty;
    public DateTime IndexedAt { get; set; }
    public DateTime CollectionUpdatedAt { get; set; }
    
    // Statistics (used by other screens, lightweight to include)
    public int ImageCount { get; set; }
    public int ThumbnailCount { get; set; }
    public int CacheCount { get; set; }
    
    // First thumbnail tracking (for collection card display)
    public bool HasFirstThumbnail { get; set; }
    public string? FirstThumbnailPath { get; set; }
    
    public string IndexVersion { get; set; } = "v1.0";
}

