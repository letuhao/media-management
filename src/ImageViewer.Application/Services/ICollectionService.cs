using MongoDB.Bson;
using ImageViewer.Domain.Entities;
using ImageViewer.Domain.ValueObjects;
using ImageViewer.Domain.Enums;

namespace ImageViewer.Application.Services;

/// <summary>
/// Service interface for Collection operations
/// </summary>
public interface ICollectionService
{
    #region Collection Management
    
    Task<Collection> CreateCollectionAsync(ObjectId? libraryId, string name, string path, CollectionType type, string? description = null, string? createdBy = null, string? createdBySystem = null);
    Task<Collection?> GetCollectionByIdAsync(ObjectId collectionId);
    Task<Collection?> GetCollectionByPathAsync(string path);
    Task<IEnumerable<Collection>> GetCollectionsByLibraryIdAsync(ObjectId libraryId);
    Task<IEnumerable<Collection>> GetCollectionsAsync(int page = 1, int pageSize = 20, string sortBy = "updatedAt", string sortDirection = "desc");
    Task<long> GetTotalCollectionsCountAsync();
    Task<Collection> UpdateCollectionAsync(ObjectId collectionId, UpdateCollectionRequest request);
    Task DeleteCollectionAsync(ObjectId collectionId);
    
    #endregion
    
    #region Collection Settings Management
    
    Task<Collection> UpdateSettingsAsync(ObjectId collectionId, UpdateCollectionSettingsRequest request, bool triggerScan = true, bool forceRescan = false);
    Task<Collection> UpdateMetadataAsync(ObjectId collectionId, UpdateCollectionMetadataRequest request);
    Task<Collection> UpdateStatisticsAsync(ObjectId collectionId, UpdateCollectionStatisticsRequest request);
    Task RecalculateCollectionStatisticsAsync(ObjectId collectionId);
    Task RecalculateAllCollectionStatisticsAsync();
    
    #endregion
    
    #region Collection Status Management
    
    Task<Collection> ActivateCollectionAsync(ObjectId collectionId);
    Task<Collection> DeactivateCollectionAsync(ObjectId collectionId);
    
    #endregion
    
    #region Collection Watching Management
    
    Task<Collection> EnableWatchingAsync(ObjectId collectionId);
    Task<Collection> DisableWatchingAsync(ObjectId collectionId);
    Task<Collection> UpdateWatchSettingsAsync(ObjectId collectionId, UpdateWatchSettingsRequest request);
    
    #endregion
    
    #region Collection Search and Filtering
    
    Task<IEnumerable<Collection>> SearchCollectionsAsync(string query, int page = 1, int pageSize = 20);
    Task<IEnumerable<Collection>> GetCollectionsByFilterAsync(CollectionFilterRequest filter, int page = 1, int pageSize = 20);
    Task<IEnumerable<Collection>> GetCollectionsByLibraryAsync(ObjectId libraryId, int page = 1, int pageSize = 20);
    
    #endregion
    
    #region Collection Statistics
    
    Task<CollectionStatistics> GetCollectionStatisticsAsync();
    Task<IEnumerable<Collection>> GetTopCollectionsByActivityAsync(int limit = 10);
    Task<IEnumerable<Collection>> GetRecentCollectionsAsync(int limit = 10);
    Task<IEnumerable<Collection>> GetCollectionsByTypeAsync(CollectionType type, int page = 1, int pageSize = 20);
    
    #endregion
    
    #region Collection Navigation
    
    /// <summary>
    /// Get navigation info for a collection (previous/next collection IDs and position)
    /// </summary>
    Task<DTOs.Collections.CollectionNavigationDto> GetCollectionNavigationAsync(ObjectId collectionId, string sortBy = "updatedAt", string sortDirection = "desc");
    
    /// <summary>
    /// Get sibling collections (all collections in the same sorted list)
    /// </summary>
    Task<DTOs.Collections.CollectionSiblingsDto> GetCollectionSiblingsAsync(ObjectId collectionId, int page = 1, int pageSize = 20, string sortBy = "updatedAt", string sortDirection = "desc");
    
    /// <summary>
    /// Get sorted collections with default system sorting
    /// </summary>
    Task<IEnumerable<Collection>> GetSortedCollectionsAsync(string sortBy = "updatedAt", string sortDirection = "desc", int? limit = null);
    
    #endregion
    
    #region Collection Cleanup
    
    /// <summary>
    /// Clean up collections that no longer exist on disk
    /// </summary>
    Task<CollectionCleanupResult> CleanupNonExistentCollectionsAsync();
    
    #endregion
}

/// <summary>
/// Request model for updating collection information
/// </summary>
public class UpdateCollectionRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Path { get; set; }
    public CollectionType? Type { get; set; }
}

/// <summary>
/// Request model for updating collection settings
/// </summary>
public class UpdateCollectionSettingsRequest
{
    public bool? Enabled { get; set; }
    public bool? AutoScan { get; set; }
    public bool? GenerateThumbnails { get; set; }
    public bool? GenerateCache { get; set; }
    public bool? EnableWatching { get; set; }
    public int? ScanInterval { get; set; }
    public long? MaxFileSize { get; set; }
    public List<string>? AllowedFormats { get; set; }
    public List<string>? ExcludedPaths { get; set; }
}

/// <summary>
/// Request model for updating collection metadata
/// </summary>
public class UpdateCollectionMetadataRequest
{
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }
    public List<string>? Categories { get; set; }
    public Dictionary<string, object>? CustomFields { get; set; }
    public string? Version { get; set; }
    public string? CreatedBy { get; set; }
    public string? ModifiedBy { get; set; }
}

/// <summary>
/// Request model for updating collection statistics
/// </summary>
public class UpdateCollectionStatisticsRequest
{
    public long? TotalItems { get; set; }
    public long? TotalSize { get; set; }
    public long? TotalViews { get; set; }
    public long? TotalDownloads { get; set; }
    public long? TotalShares { get; set; }
    public long? TotalLikes { get; set; }
    public long? TotalComments { get; set; }
    public DateTime? LastScanDate { get; set; }
    public long? ScanCount { get; set; }
    public DateTime? LastActivity { get; set; }
}

/// <summary>
/// Request model for filtering collections
/// </summary>
public class CollectionFilterRequest
{
    public ObjectId? LibraryId { get; set; }
    public CollectionType? Type { get; set; }
    public bool? IsActive { get; set; }
    public DateTime? CreatedAfter { get; set; }
    public DateTime? CreatedBefore { get; set; }
    public DateTime? LastActivityAfter { get; set; }
    public DateTime? LastActivityBefore { get; set; }
    public string? Path { get; set; }
    public List<string>? Tags { get; set; }
    public List<string>? Categories { get; set; }
}

/// <summary>
/// Result model for collection cleanup operation
/// </summary>
public class CollectionCleanupResult
{
    public int TotalCollectionsChecked { get; set; }
    public int NonExistentCollectionsFound { get; set; }
    public int CollectionsDeleted { get; set; }
    public int Errors { get; set; }
    public List<string> DeletedCollectionPaths { get; set; } = new();
    public List<string> ErrorMessages { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public TimeSpan Duration => CompletedAt - StartedAt;
}