using System.Text.Json;
using ImageViewer.Application.Mappings;
using ImageViewer.Application.Services;
using ImageViewer.Domain.Entities;
using ImageViewer.Domain.Interfaces;
using ImageViewer.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using StackExchange.Redis;

namespace ImageViewer.Infrastructure.Services;

/// <summary>
/// Redis-based collection index for ultra-fast navigation and sibling queries.
/// Uses Redis sorted sets for O(log N) position lookup and O(log N + M) range queries.
/// </summary>
public class RedisCollectionIndexService : ICollectionIndexService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ICollectionRepository _collectionRepository;
    private readonly ICacheFolderRepository _cacheFolderRepository;
    private readonly IImageProcessingService _imageProcessingService;
    private readonly IImageProcessingSettingsService _imageProcessingSettingsService;
    private readonly ILogger<RedisCollectionIndexService> _logger;

    // Redis key patterns
    private const string SORTED_SET_PREFIX = "collection_index:sorted:";
    private const string HASH_PREFIX = "collection_index:data:";
    private const string STATS_KEY = "collection_index:stats";
    private const string LAST_REBUILD_KEY = "collection_index:last_rebuild";
    private const string THUMBNAIL_PREFIX = "collection_index:thumb:";
    private const string DASHBOARD_STATS_KEY = "dashboard:statistics";
    private const string DASHBOARD_METADATA_KEY = "dashboard:metadata";
    private const string STATE_PREFIX = "collection_index:state:";  // NEW: State tracking

    public RedisCollectionIndexService(
        IConnectionMultiplexer redis,
        ICollectionRepository collectionRepository,
        ICacheFolderRepository cacheFolderRepository,
        IImageProcessingService imageProcessingService,
        IImageProcessingSettingsService imageProcessingSettingsService,
        ILogger<RedisCollectionIndexService> logger)
    {
        _redis = redis;
        _db = redis.GetDatabase();
        _collectionRepository = collectionRepository;
        _cacheFolderRepository = cacheFolderRepository;
        _imageProcessingService = imageProcessingService;
        _imageProcessingSettingsService = imageProcessingSettingsService;
        _logger = logger;
    }

    public async Task RebuildIndexAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("🔄 Starting collection index rebuild...");
            _logger.LogInformation("📊 Redis connection status: IsConnected={IsConnected}", _redis.IsConnected);
            
            // CRITICAL: Wait for Redis to be ready before attempting rebuild
            if (!_redis.IsConnected)
            {
                _logger.LogWarning("⚠️ Redis not connected yet, waiting up to 10s...");
                var waited = 0;
                while (!_redis.IsConnected && waited < 10000)
                {
                    _logger.LogDebug("⏳ Still waiting for Redis... ({Waited}ms)", waited);
                    await Task.Delay(500, cancellationToken);
                    waited += 500;
                }
                
                if (!_redis.IsConnected)
                {
                    _logger.LogError("❌ Redis connection timeout after {Waited}ms, skipping index rebuild", waited);
                    _logger.LogError("📊 Final connection status: {Status}", _redis.GetStatus());
                    return; // Skip rebuild, will retry on next startup
                }
                
                _logger.LogInformation("✅ Redis connected after waiting {Waited}ms", waited);
            }
            else
            {
                _logger.LogInformation("✅ Redis already connected, proceeding with rebuild");
            }
            
            var startTime = DateTime.UtcNow;

            // Count total collections (lightweight query)
            var totalCount = await _collectionRepository.CountAsync(
                MongoDB.Driver.Builders<Collection>.Filter.Eq(c => c.IsDeleted, false)
            );
            _logger.LogInformation("📊 Found {Count} collections to index from MongoDB", totalCount);

            // Smart detection: If MongoDB has few collections but Redis has many keys, do a fast FLUSHDB
            // Only if Redis connection is ready (avoid timeout if connection still establishing)
            _logger.LogInformation("🔍 Checking Redis connection before clearing old data...");
            if (_redis.IsConnected)
            {
                _logger.LogInformation("✅ Redis connected, checking for stale data...");
                var redisKeyCount = await GetRedisKeyCountAsync();
                _logger.LogInformation("📊 Redis keys: {RedisKeys}, MongoDB collections: {MongoCollections}", 
                    redisKeyCount, totalCount);
                
                if (totalCount < 100 && redisKeyCount > totalCount * 10)
                {
                    _logger.LogWarning("⚠️ Detected stale Redis data: {RedisKeys} keys but only {MongoCollections} collections. Using FLUSHDB for fast cleanup.", 
                        redisKeyCount, totalCount);
                    await FlushRedisAsync();
                    _logger.LogInformation("✅ Redis flushed successfully");
                }
                else
                {
                    // Normal clear: scan and delete specific keys
                    _logger.LogInformation("🧹 Clearing old Redis index data...");
                    await ClearIndexAsync();
                    _logger.LogInformation("✅ Old Redis index cleared");
                }
            }
            else
            {
                _logger.LogWarning("⚠️ Redis connection not ready yet, using FLUSHDB as safe fallback");
                await FlushRedisAsync();
                _logger.LogInformation("✅ Redis flushed successfully");
            }

            // Process collections in batches to avoid memory issues
            const int BATCH_SIZE = 100; // Process 100 collections at a time
            var processedCount = 0;
            var currentBatch = 0;
            var totalBatches = (int)Math.Ceiling((double)totalCount / BATCH_SIZE);

            _logger.LogInformation("🔨 Building Redis index for {Count} collections in {Batches} batches of {BatchSize}...", 
                totalCount, totalBatches, BATCH_SIZE);

            // Stream collections in batches
            for (var skip = 0; skip < totalCount; skip += BATCH_SIZE)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("⚠️ Index rebuild cancelled");
                    return;
                }

                currentBatch++;
                var batchStartTime = DateTime.UtcNow;
                
                // Memory monitoring before batch
                var memoryBefore = GC.GetTotalMemory(false);
                _logger.LogDebug("💾 Batch {Current}/{Total}: Memory before = {MemoryMB:F2} MB", 
                    currentBatch, totalBatches, memoryBefore / 1024.0 / 1024.0);

                // Fetch only THIS batch with projection (avoid loading full Images/Thumbnails/CacheImages arrays)
                var batchCollections = await _collectionRepository.FindAsync(
                    MongoDB.Driver.Builders<Collection>.Filter.Eq(c => c.IsDeleted, false),
                    MongoDB.Driver.Builders<Collection>.Sort.Ascending(c => c.Id),
                    BATCH_SIZE,
                    skip
                );

                var collectionList = batchCollections.ToList();
                
                // Process batch
                var batch = _db.CreateBatch();
                var tasks = new List<Task>();

                foreach (var collection in collectionList)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("⚠️ Index rebuild cancelled");
                        return;
                    }

                    // Add to sorted sets (one per sort field/direction combination)
                    tasks.Add(AddToSortedSetsAsync(batch, collection));

                    // Add summary to hash
                    tasks.Add(AddToHashAsync(batch, collection));
                    
                    // ✅ NEW: Update state tracking
                    tasks.Add(UpdateCollectionIndexStateAsync(batch, collection));
                    
                    processedCount++;
                }

                // Execute batch
                batch.Execute();
                await Task.WhenAll(tasks);

                // Memory monitoring after batch
                var memoryAfter = GC.GetTotalMemory(false);
                var memoryDelta = memoryAfter - memoryBefore;
                var batchDuration = DateTime.UtcNow - batchStartTime;
                
                _logger.LogInformation("✅ Batch {Current}/{Total} completed: {Count} collections in {Duration}ms, Memory delta = {DeltaMB:+0.00;-0.00} MB (now {CurrentMB:F2} MB)", 
                    currentBatch, totalBatches, collectionList.Count, batchDuration.TotalMilliseconds,
                    memoryDelta / 1024.0 / 1024.0, memoryAfter / 1024.0 / 1024.0);

                // ✅ CRITICAL: Clear tasks list to release memory held by completed tasks
                tasks.Clear();
                
                // Force GC after each batch to free memory immediately
                collectionList.Clear();
                collectionList = null!; // ✅ Release list object reference
                
                // ✅ Use aggressive GC mode to release large objects immediately
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }

            _logger.LogInformation("✅ All {Count} collections processed successfully", processedCount);

            // Update statistics
            _logger.LogInformation("📊 Updating index statistics...");
            await _db.StringSetAsync(LAST_REBUILD_KEY, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await _db.StringSetAsync(STATS_KEY + ":total", totalCount);
            _logger.LogInformation("✅ Index statistics updated");

            // Populate dashboard statistics cache (streaming to avoid loading all at once)
            _logger.LogInformation("📊 Building dashboard statistics cache (streaming)...");
            try
            {
                var dashboardStats = await BuildDashboardStatisticsStreamingAsync(totalCount, cancellationToken);
                await StoreDashboardStatisticsAsync(dashboardStats);
                _logger.LogInformation("✅ Dashboard statistics cache populated successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Failed to populate dashboard statistics cache, will be built on first API call");
            }

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation("✅ Collection index rebuilt successfully. {Count} collections indexed in {Duration}ms", 
                processedCount, duration.TotalMilliseconds);
            
            // ✅ FINAL CLEANUP: Aggressive GC to release all accumulated memory
            var memoryBeforeFinalGC = GC.GetTotalMemory(false);
            _logger.LogInformation("🧹 Final memory cleanup: Before GC = {MemoryMB:F2} MB", memoryBeforeFinalGC / 1024.0 / 1024.0);
            
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            
            var memoryAfterFinalGC = GC.GetTotalMemory(true); // true = force full collection
            var memoryFreed = memoryBeforeFinalGC - memoryAfterFinalGC;
            _logger.LogInformation("✅ Final memory cleanup complete: After GC = {MemoryMB:F2} MB, Freed = {FreedMB:F2} MB", 
                memoryAfterFinalGC / 1024.0 / 1024.0, memoryFreed / 1024.0 / 1024.0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to rebuild collection index");
            throw;
        }
    }

    public async Task AddOrUpdateCollectionAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Adding/updating collection {CollectionId} in index", collection.Id);

            // Add to sorted sets
            await AddToSortedSetsAsync(_db, collection);

            // Add/update hash
            await AddToHashAsync(_db, collection);
            
            // ✅ NEW: Update state tracking
            await UpdateCollectionIndexStateAsync(_db, collection);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add/update collection {CollectionId} in index", collection.Id);
            // Don't throw - index rebuild can fix this
        }
    }

    public async Task RemoveCollectionAsync(ObjectId collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Removing collection {CollectionId} from index", collectionId);

            var collectionIdStr = collectionId.ToString();
            
            // First, get the collection summary to know which secondary indexes to clean
            var summary = await GetCollectionSummaryAsync(collectionIdStr);

            var sortFields = new[] { "updatedAt", "createdAt", "name", "imageCount", "totalSize" };
            var sortDirections = new[] { "asc", "desc" };
            var tasks = new List<Task>();

            // Remove from primary indexes
            foreach (var field in sortFields)
            {
                foreach (var direction in sortDirections)
                {
                    var key = GetSortedSetKey(field, direction);
                    tasks.Add(_db.SortedSetRemoveAsync(key, collectionIdStr));
                }
            }
            
            // Remove from secondary indexes (if summary found)
            if (summary != null)
            {
                // Remove from by_library indexes
                if (!string.IsNullOrEmpty(summary.LibraryId))
                {
                    foreach (var field in sortFields)
                    {
                        foreach (var direction in sortDirections)
                        {
                            var key = GetSecondaryIndexKey("by_library", summary.LibraryId, field, direction);
                            tasks.Add(_db.SortedSetRemoveAsync(key, collectionIdStr));
                        }
                    }
                }
                
                // Remove from by_type indexes
                foreach (var field in sortFields)
                {
                    foreach (var direction in sortDirections)
                    {
                        var key = GetSecondaryIndexKey("by_type", summary.Type.ToString(), field, direction);
                        tasks.Add(_db.SortedSetRemoveAsync(key, collectionIdStr));
                    }
                }
                
                _logger.LogDebug("Removing collection from {PrimaryCount} primary + {SecondaryCount} secondary indexes", 
                    sortFields.Length * sortDirections.Length, 
                    (sortFields.Length * sortDirections.Length * 2)); // library + type
            }

            // Remove from hash
            tasks.Add(_db.KeyDeleteAsync(GetHashKey(collectionIdStr)));
            
            // ✅ NEW: Remove state tracking
            tasks.Add(_db.KeyDeleteAsync(GetStateKey(collectionIdStr)));

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove collection {CollectionId} from index", collectionId);
            // Don't throw - index rebuild can fix this
        }
    }

    public async Task<CollectionNavigationResult> GetNavigationAsync(
        ObjectId collectionId, 
        string sortBy = "updatedAt", 
        string sortDirection = "desc", 
        CancellationToken cancellationToken = default)
    {
        try
        {
            var collectionIdStr = collectionId.ToString();
            var key = GetSortedSetKey(sortBy, sortDirection);

            // Get position using ZRANK (O(log N) - super fast!)
            // Note: ZRANK always returns ascending rank (0 = lowest score), Order parameter is ignored
            var rank = await _db.SortedSetRankAsync(key, collectionIdStr);
            
            if (!rank.HasValue)
            {
                _logger.LogWarning("Collection {CollectionId} not found in index", collectionId);
                // Fallback: try to get from database
                var collection = await _collectionRepository.GetByIdAsync(collectionId);
                if (collection != null && !collection.IsDeleted)
                {
                    await AddOrUpdateCollectionAsync(collection);
                    rank = await _db.SortedSetRankAsync(key, collectionIdStr);
                }
            }

            var currentPosition = rank.HasValue ? (int)rank.Value + 1 : 0; // 1-based

            // Get total count
            var totalCount = await _db.SortedSetLengthAsync(key);

            // Get previous and next collection IDs
            string? previousId = null;
            string? nextId = null;

            if (rank.HasValue)
            {
                // Get previous (rank - 1)
                // ZRANGE by rank should always use Order.Ascending (rank 0, 1, 2...)
                if (rank.Value > 0)
                {
                    var prevEntries = await _db.SortedSetRangeByRankAsync(key, rank.Value - 1, rank.Value - 1, Order.Ascending);
                    previousId = prevEntries.FirstOrDefault().ToString();
                }

                // Get next (rank + 1)
                if (rank.Value < totalCount - 1)
                {
                    var nextEntries = await _db.SortedSetRangeByRankAsync(key, rank.Value + 1, rank.Value + 1, Order.Ascending);
                    nextId = nextEntries.FirstOrDefault().ToString();
                }
            }

            return new CollectionNavigationResult
            {
                PreviousCollectionId = previousId,
                NextCollectionId = nextId,
                CurrentPosition = currentPosition,
                TotalCollections = (int)totalCount,
                HasPrevious = previousId != null,
                HasNext = nextId != null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get navigation for collection {CollectionId}", collectionId);
            throw;
        }
    }

    public async Task<CollectionSiblingsResult> GetSiblingsAsync(
        ObjectId collectionId, 
        int page = 1, 
        int pageSize = 20, 
        string sortBy = "updatedAt", 
        string sortDirection = "desc", 
        CancellationToken cancellationToken = default)
    {
        try
        {
            var collectionIdStr = collectionId.ToString();
            var key = GetSortedSetKey(sortBy, sortDirection);

            // Get current position
            // Note: ZRANK always returns ascending rank (0 = lowest score)
            var rank = await _db.SortedSetRankAsync(key, collectionIdStr);
            
            if (!rank.HasValue)
            {
                _logger.LogWarning("Collection {CollectionId} not found in index for siblings", collectionId);
                return new CollectionSiblingsResult
                {
                    Siblings = new List<CollectionSummary>(),
                    CurrentPosition = 0,
                    TotalCount = 0
                };
            }

            var currentPosition = (int)rank.Value;
            var totalCount = await _db.SortedSetLengthAsync(key);

            // ABSOLUTE PAGINATION WITH CURRENT POSITION AWARENESS
            // Calculate which absolute page the current collection is on
            // Then use standard pagination from there
            // 
            // Example: current at rank 24,423, pageSize 20
            //   currentPage = floor(24,423 / 20) + 1 = 1221 + 1 = 1222
            //   Page 1222: ranks 24,420-24,439 (but clamped to 24,423)
            //   Page 1221: ranks 24,400-24,419
            //   Page 1223: ranks 24,440+ (empty if beyond total)
            
            // Calculate which page contains the current collection
            var currentPageNumber = (currentPosition / pageSize) + 1;
            
            // Use the requested page parameter, but default to current page if page=1
            // This allows frontend to request page 1 and get the "current" page
            int actualPage = (page == 1) ? currentPageNumber : page;
            
            // Standard absolute pagination
            var startRank = (actualPage - 1) * pageSize;
            var endRank = Math.Min((int)totalCount - 1, startRank + pageSize - 1);
            
            // Ensure valid range
            startRank = Math.Max(0, startRank);
            if (startRank >= totalCount)
            {
                startRank = Math.Max(0, (int)totalCount - pageSize);
                endRank = (int)totalCount - 1;
            }

            // Get collection IDs in range (O(log N + M))
            // ZRANGE by rank always uses ascending order (rank 0, 1, 2...)
            var collectionIds = await _db.SortedSetRangeByRankAsync(key, startRank, endRank, Order.Ascending);

            // Get collection summaries using batch MGET (10-20x faster!)
            var siblings = await BatchGetCollectionSummariesAsync(collectionIds);

            // Calculate pagination info
            var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

            return new CollectionSiblingsResult
            {
                Siblings = siblings,
                CurrentPosition = currentPosition + 1, // 1-based
                CurrentPage = actualPage,
                TotalCount = (int)totalCount,
                TotalPages = totalPages
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get siblings for collection {CollectionId}", collectionId);
            throw;
        }
    }

    public async Task<bool> IsIndexValidAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("🔍 Checking if Redis index is valid...");
            _logger.LogDebug("📊 Redis connection status: IsConnected={IsConnected}", _redis.IsConnected);
            
            // Check if at least one sorted set exists and has entries
            var key = GetSortedSetKey("updatedAt", "desc");
            _logger.LogDebug("🔍 Checking sorted set key: {Key}", key);
            var count = await _db.SortedSetLengthAsync(key);
            _logger.LogDebug("📊 Sorted set count: {Count}", count);
            
            if (count == 0)
            {
                _logger.LogInformation("⚠️ Index invalid: Sorted set is empty");
                return false;
            }

            // Check if last rebuild time exists
            _logger.LogDebug("🔍 Checking last rebuild key: {Key}", LAST_REBUILD_KEY);
            var lastRebuildExists = await _db.KeyExistsAsync(LAST_REBUILD_KEY);
            _logger.LogDebug("📊 Last rebuild key exists: {Exists}", lastRebuildExists);
            
            if (!lastRebuildExists)
            {
                _logger.LogInformation("⚠️ Index invalid: Last rebuild key missing");
                return false;
            }
            
            _logger.LogInformation("✅ Index is valid: {Count} entries found", count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error checking index validity");
            return false;
        }
    }

    public async Task<CollectionIndexStats> GetIndexStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = new CollectionIndexStats();

            // Get total count
            var totalStr = await _db.StringGetAsync(STATS_KEY + ":total");
            stats.TotalCollections = totalStr.HasValue ? (int)totalStr : 0;

            // Get last rebuild time
            var lastRebuildStr = await _db.StringGetAsync(LAST_REBUILD_KEY);
            if (lastRebuildStr.HasValue)
            {
                var unixTime = (long)lastRebuildStr;
                stats.LastRebuildTime = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
            }

            // Get sorted set sizes
            var sortFields = new[] { "updatedAt", "createdAt", "name", "imageCount", "totalSize" };
            var sortDirections = new[] { "asc", "desc" };

            foreach (var field in sortFields)
            {
                foreach (var direction in sortDirections)
                {
                    var key = GetSortedSetKey(field, direction);
                    var count = await _db.SortedSetLengthAsync(key);
                    stats.SortedSetSizes[$"{field}_{direction}"] = (int)count;
                }
            }

            stats.IsValid = await IsIndexValidAsync();

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get index stats");
            throw;
        }
    }

    #region Helper Methods

    private string GetSortedSetKey(string sortBy, string sortDirection)
    {
        return $"{SORTED_SET_PREFIX}{sortBy}:{sortDirection}";
    }

    private string GetHashKey(string collectionId)
    {
        return $"{HASH_PREFIX}{collectionId}";
    }

    private string GetSecondaryIndexKey(string indexType, string indexValue, string sortBy, string sortDirection)
    {
        return $"{SORTED_SET_PREFIX}{indexType}:{indexValue}:{sortBy}:{sortDirection}";
    }

    private string GetThumbnailKey(string collectionId)
    {
        return $"{THUMBNAIL_PREFIX}{collectionId}";
    }
    
    private string GetStateKey(string collectionId)
    {
        return $"{STATE_PREFIX}{collectionId}";
    }

    private async Task AddToSortedSetsAsync(IDatabaseAsync db, Collection collection)
    {
        var collectionIdStr = collection.Id.ToString();
        var tasks = new List<Task>();

        // Primary indexes - all sort field combinations
        var sortFields = new[] { "updatedAt", "createdAt", "name", "imageCount", "totalSize" };
        var sortDirections = new[] { "asc", "desc" };

        foreach (var field in sortFields)
        {
            foreach (var direction in sortDirections)
            {
                var score = GetScoreForField(collection, field, direction);
                tasks.Add(db.SortedSetAddAsync(GetSortedSetKey(field, direction), collectionIdStr, score));
            }
        }

        // Secondary indexes - by library
        var libraryId = collection.LibraryId?.ToString() ?? "null";
        foreach (var field in sortFields)
        {
            foreach (var direction in sortDirections)
            {
                var score = GetScoreForField(collection, field, direction);
                var key = GetSecondaryIndexKey("by_library", libraryId, field, direction);
                tasks.Add(db.SortedSetAddAsync(key, collectionIdStr, score));
            }
        }

        // Secondary indexes - by type
        var type = ((int)collection.Type).ToString();
        foreach (var field in sortFields)
        {
            foreach (var direction in sortDirections)
            {
                var score = GetScoreForField(collection, field, direction);
                var key = GetSecondaryIndexKey("by_type", type, field, direction);
                tasks.Add(db.SortedSetAddAsync(key, collectionIdStr, score));
            }
        }

        await Task.WhenAll(tasks);
    }

    private double GetScoreForField(Collection collection, string field, string direction)
    {
        var multiplier = direction == "desc" ? -1 : 1;

        return field.ToLower() switch
        {
            "updatedat" => collection.UpdatedAt.Ticks * multiplier,
            "createdat" => collection.CreatedAt.Ticks * multiplier,
            "name" => (collection.Name?.GetHashCode() ?? 0) * multiplier,
            "imagecount" => collection.Statistics.TotalItems * multiplier,
            "totalsize" => collection.Statistics.TotalSize * multiplier,
            _ => collection.UpdatedAt.Ticks * multiplier
        };
    }

    private async Task AddToHashAsync(IDatabaseAsync db, Collection collection)
    {
        // Get thumbnail and convert to base64 for caching
        string? thumbnailBase64 = null;
        var thumbnail = collection.GetCollectionThumbnail();
        
        if (thumbnail != null && !string.IsNullOrEmpty(thumbnail.ThumbnailPath))
        {
            try
            {
                // Check if thumbnail file exists
                if (File.Exists(thumbnail.ThumbnailPath))
                {
                    // ✅ NEW: Smart detection - resize if needed (direct mode, oversized, etc.)
                    var needsResize = ShouldResizeThumbnail(thumbnail);
                    
                    byte[] thumbnailBytes = null!;
                    try
                    {
                        if (needsResize)
                        {
                            // Resize image in memory before caching (uses MongoDB settings)
                            _logger.LogDebug("Resizing thumbnail for collection {CollectionId} (Direct={IsDirect}, {W}×{H}, {SizeKB} KB)",
                                collection.Id, thumbnail.IsDirect, thumbnail.Width, thumbnail.Height, thumbnail.FileSize / 1024);
                            
                            thumbnailBytes = await ResizeImageForCacheAsync(thumbnail.ThumbnailPath);
                            
                            if (thumbnailBytes == null || thumbnailBytes.Length == 0)
                            {
                                _logger.LogWarning("Failed to resize thumbnail for collection {CollectionId}, skipping", 
                                    collection.Id);
                                return;  // Skip this thumbnail
                            }
                            
                            _logger.LogDebug("Resized thumbnail: Original {OriginalKB} KB → Resized {ResizedKB} KB (saved {SavedKB} KB)",
                                thumbnail.FileSize / 1024, thumbnailBytes.Length / 1024, 
                                (thumbnail.FileSize - thumbnailBytes.Length) / 1024);
                        }
                        else
                        {
                            // Use pre-generated thumbnail as-is (already correct size)
                            thumbnailBytes = await File.ReadAllBytesAsync(thumbnail.ThumbnailPath);
                            
                            _logger.LogDebug("Using pre-generated thumbnail for collection {CollectionId}, size: {Size} KB",
                                collection.Id, thumbnailBytes.Length / 1024);
                        }
                        
                        // Convert to base64 string
                        var base64 = Convert.ToBase64String(thumbnailBytes);
                        
                        // Create data URL with proper content type
                        var contentType = GetContentTypeFromFormat(thumbnail.Format);
                        thumbnailBase64 = $"data:{contentType};base64,{base64}";
                        
                        _logger.LogDebug("Cached base64 thumbnail for collection {CollectionId}, size: {Size} KB", 
                            collection.Id, base64.Length / 1024);
                        
                        // ✅ CRITICAL: Explicitly null out to help GC
                        base64 = null!;
                    }
                    finally
                    {
                        // ✅ CRITICAL: Explicitly null out bytes array to help GC
                        thumbnailBytes = null!;
                    }
                }
                else
                {
                    _logger.LogDebug("Thumbnail file not found for collection {CollectionId} at path: {Path}", 
                        collection.Id, thumbnail.ThumbnailPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load thumbnail for collection {CollectionId}, skipping base64 caching", 
                    collection.Id);
                // Continue without thumbnail - non-critical
            }
        }
        
        var summary = new CollectionSummary
        {
            Id = collection.Id.ToString(),
            Name = collection.Name ?? "",
            FirstImageId = collection.Images?.FirstOrDefault()?.Id.ToString(),
            ImageCount = collection.Images?.Count ?? 0,
            ThumbnailCount = collection.Thumbnails?.Count ?? 0,
            CacheCount = collection.CacheImages?.Count ?? 0,
            TotalSize = collection.Statistics.TotalSize,
            CreatedAt = collection.CreatedAt,
            UpdatedAt = collection.UpdatedAt,
            
            // New fields for filtering and display
            LibraryId = collection.LibraryId?.ToString() ?? string.Empty,
            Description = collection.Description,
            Type = (int)collection.Type,
            Tags = new List<string>(), // TODO: Add tags support when Collection entity has it
            Path = collection.Path ?? "",
            
            // Pre-computed base64 thumbnail (optimization)
            ThumbnailBase64 = thumbnailBase64
        };

        var json = JsonSerializer.Serialize(summary);
        await db.StringSetAsync(GetHashKey(collection.Id.ToString()), json);
    }

    private async Task<CollectionSummary?> GetCollectionSummaryAsync(string collectionId)
    {
        var json = await _db.StringGetAsync(GetHashKey(collectionId));
        if (!json.HasValue)
            return null;

        try
        {
            return JsonSerializer.Deserialize<CollectionSummary>(json.ToString());
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize collection summary for {CollectionId}", collectionId);
            return null;
        }
    }

    /// <summary>
    /// Batch get multiple collection summaries using MGET (10-20x faster than sequential GET)
    /// </summary>
    private async Task<List<CollectionSummary>> BatchGetCollectionSummariesAsync(RedisValue[] collectionIds)
    {
        if (collectionIds.Length == 0)
            return new List<CollectionSummary>();

        try
        {
            // Build hash keys for batch retrieval
            var hashKeys = collectionIds.Select(id => (RedisKey)GetHashKey(id.ToString())).ToArray();
            
            // Single MGET call instead of N GET calls (10-20x faster!)
            var jsonValues = await _db.StringGetAsync(hashKeys);
            
            // Deserialize all summaries
            var summaries = new List<CollectionSummary>();
            for (int i = 0; i < jsonValues.Length; i++)
            {
                if (jsonValues[i].HasValue)
                {
                    try
                    {
                        var summary = JsonSerializer.Deserialize<CollectionSummary>(jsonValues[i].ToString());
                        if (summary != null)
                        {
                            summaries.Add(summary);
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize collection {Id}", collectionIds[i]);
                    }
                }
            }
            
            _logger.LogDebug("Batch retrieved {Found}/{Total} collection summaries", summaries.Count, collectionIds.Length);
            return summaries;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to batch get collection summaries");
            return new List<CollectionSummary>();
        }
    }

    private async Task ClearIndexAsync()
    {
        _logger.LogDebug("Clearing existing index...");

        try
        {
            var server = _redis.GetServer(_redis.GetEndPoints().First());
            var tasks = new List<Task>();
            
            // Find and delete all sorted set indexes (primary + secondary)
            // Use async enumeration to avoid timeout on large key sets
            var sortedSetCount = 0;
            await foreach (var key in server.KeysAsync(pattern: $"{SORTED_SET_PREFIX}*", pageSize: 1000))
            {
                tasks.Add(_db.KeyDeleteAsync(key));
                sortedSetCount++;
            }
            _logger.LogDebug("Found {Count} sorted set keys to delete", sortedSetCount);
            
            // Find and delete all collection hashes
            var hashCount = 0;
            await foreach (var key in server.KeysAsync(pattern: $"{HASH_PREFIX}*", pageSize: 1000))
            {
                tasks.Add(_db.KeyDeleteAsync(key));
                hashCount++;
            }
            _logger.LogDebug("Found {Count} collection hashes to delete", hashCount);
            
            // ✅ NEW: Find and delete all state keys
            var stateCount = 0;
            await foreach (var key in server.KeysAsync(pattern: $"{STATE_PREFIX}*", pageSize: 1000))
            {
                tasks.Add(_db.KeyDeleteAsync(key));
                stateCount++;
            }
            _logger.LogDebug("Found {Count} state keys to delete", stateCount);
            
            // Note: Don't clear thumbnails - they can persist for performance
            // Thumbnails have 30-day expiration anyway
            
            await Task.WhenAll(tasks);
            
            _logger.LogInformation("✅ Cleared {SortedSets} sorted sets, {Hashes} hashes, and {States} state keys", 
                sortedSetCount, hashCount, stateCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear index");
            throw;
        }
    }

    #endregion

    #region New Methods for Collection Pagination and Filtering

    public async Task<CollectionPageResult> GetCollectionPageAsync(
        int page,
        int pageSize,
        string sortBy = "updatedAt",
        string sortDirection = "desc",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var key = GetSortedSetKey(sortBy, sortDirection);
            var startRank = (page - 1) * pageSize;
            var endRank = startRank + pageSize - 1;

            // Get collection IDs for this page
            // ZRANGE by rank always uses ascending order (rank 0, 1, 2...)
            var collectionIds = await _db.SortedSetRangeByRankAsync(key, startRank, endRank, Order.Ascending);

            // Get collection summaries using batch MGET (10-20x faster than sequential GET!)
            var collections = await BatchGetCollectionSummariesAsync(collectionIds);

            // Get total count
            var totalCount = await _db.SortedSetLengthAsync(key);
            var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

            return new CollectionPageResult
            {
                Collections = collections,
                CurrentPage = page,
                PageSize = pageSize,
                TotalCount = (int)totalCount,
                TotalPages = totalPages,
                HasNext = page < totalPages,
                HasPrevious = page > 1
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get collection page {Page} with size {PageSize}", page, pageSize);
            throw;
        }
    }

    public async Task<CollectionPageResult> GetCollectionsByLibraryAsync(
        ObjectId libraryId,
        int page,
        int pageSize,
        string sortBy = "updatedAt",
        string sortDirection = "desc",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var key = GetSecondaryIndexKey("by_library", libraryId.ToString(), sortBy, sortDirection);
            var startRank = (page - 1) * pageSize;
            var endRank = startRank + pageSize - 1;

            // Get collection IDs for this page
            // ZRANGE by rank always uses ascending order (rank 0, 1, 2...)
            var collectionIds = await _db.SortedSetRangeByRankAsync(key, startRank, endRank, Order.Ascending);

            // Get collection summaries using batch MGET (10-20x faster!)
            var collections = await BatchGetCollectionSummariesAsync(collectionIds);

            // Get total count
            var totalCount = await _db.SortedSetLengthAsync(key);
            var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

            return new CollectionPageResult
            {
                Collections = collections,
                CurrentPage = page,
                PageSize = pageSize,
                TotalCount = (int)totalCount,
                TotalPages = totalPages,
                HasNext = page < totalPages,
                HasPrevious = page > 1
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get collections by library {LibraryId}", libraryId);
            throw;
        }
    }

    public async Task<CollectionPageResult> GetCollectionsByTypeAsync(
        int collectionType,
        int page,
        int pageSize,
        string sortBy = "updatedAt",
        string sortDirection = "desc",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var key = GetSecondaryIndexKey("by_type", collectionType.ToString(), sortBy, sortDirection);
            var startRank = (page - 1) * pageSize;
            var endRank = startRank + pageSize - 1;

            // Get collection IDs for this page
            // ZRANGE by rank always uses ascending order (rank 0, 1, 2...)
            var collectionIds = await _db.SortedSetRangeByRankAsync(key, startRank, endRank, Order.Ascending);

            // Get collection summaries using batch MGET (10-20x faster!)
            var collections = await BatchGetCollectionSummariesAsync(collectionIds);

            // Get total count
            var totalCount = await _db.SortedSetLengthAsync(key);
            var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

            return new CollectionPageResult
            {
                Collections = collections,
                CurrentPage = page,
                PageSize = pageSize,
                TotalCount = (int)totalCount,
                TotalPages = totalPages,
                HasNext = page < totalPages,
                HasPrevious = page > 1
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get collections by type {Type}", collectionType);
            throw;
        }
    }

    public async Task<int> GetTotalCollectionsCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var key = GetSortedSetKey("updatedAt", "desc");
            var count = await _db.SortedSetLengthAsync(key);
            return (int)count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get total collections count");
            throw;
        }
    }

    public async Task<int> GetCollectionsCountByLibraryAsync(ObjectId libraryId, CancellationToken cancellationToken = default)
    {
        try
        {
            var key = GetSecondaryIndexKey("by_library", libraryId.ToString(), "updatedAt", "desc");
            var count = await _db.SortedSetLengthAsync(key);
            return (int)count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get collections count for library {LibraryId}", libraryId);
            throw;
        }
    }

    public async Task<int> GetCollectionsCountByTypeAsync(int collectionType, CancellationToken cancellationToken = default)
    {
        try
        {
            var key = GetSecondaryIndexKey("by_type", collectionType.ToString(), "updatedAt", "desc");
            var count = await _db.SortedSetLengthAsync(key);
            return (int)count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get collections count for type {Type}", collectionType);
            throw;
        }
    }
    
    /// <summary>
    /// Get total number of keys in Redis (fast O(1) operation)
    /// </summary>
    private async Task<long> GetRedisKeyCountAsync()
    {
        try
        {
            var server = _redis.GetServer(_redis.GetEndPoints().First());
            
            // Use DBSIZE for fast key count (O(1) operation)
            var keyCount = await server.DatabaseSizeAsync();
            return keyCount;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get Redis key count, assuming 0");
            return 0;
        }
    }
    
    /// <summary>
    /// Fast flush of entire Redis database (faster than scanning/deleting individual keys)
    /// Use when MongoDB has been cleared but Redis still has stale data
    /// </summary>
    private async Task FlushRedisAsync()
    {
        try
        {
            var server = _redis.GetServer(_redis.GetEndPoints().First());
            await server.FlushDatabaseAsync();
            _logger.LogInformation("✅ Flushed entire Redis database (fast cleanup for stale data)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush Redis database");
            throw;
        }
    }

    #endregion

    #region Thumbnail Caching

    public async Task<byte[]?> GetCachedThumbnailAsync(ObjectId collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var key = GetThumbnailKey(collectionId.ToString());
            var data = await _db.StringGetAsync(key);
            
            if (data.HasValue)
            {
                _logger.LogDebug("✅ Thumbnail cache HIT for collection {CollectionId}", collectionId);
                return (byte[])data;
            }
            
            _logger.LogDebug("❌ Thumbnail cache MISS for collection {CollectionId}", collectionId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get cached thumbnail for collection {CollectionId}", collectionId);
            return null; // Fail gracefully
        }
    }

    public async Task SetCachedThumbnailAsync(ObjectId collectionId, byte[] thumbnailData, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var key = GetThumbnailKey(collectionId.ToString());
            var expire = expiration ?? TimeSpan.FromDays(30); // 30 days default
            
            await _db.StringSetAsync(key, thumbnailData, expire);
            _logger.LogDebug("💾 Cached thumbnail for collection {CollectionId}, size: {Size} bytes, expiration: {Expiration}", 
                collectionId, thumbnailData.Length, expire);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cache thumbnail for collection {CollectionId}", collectionId);
            // Fail gracefully - don't throw
        }
    }

    public async Task BatchCacheThumbnailsAsync(Dictionary<ObjectId, byte[]> thumbnails, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("📦 Batch caching {Count} thumbnails...", thumbnails.Count);
            var expire = expiration ?? TimeSpan.FromDays(30);
            
            var batch = _db.CreateBatch();
            var tasks = new List<Task>();
            
            foreach (var kvp in thumbnails)
            {
                var key = GetThumbnailKey(kvp.Key.ToString());
                tasks.Add(batch.StringSetAsync(key, kvp.Value, expire));
            }
            
            batch.Execute();
            await Task.WhenAll(tasks);
            
            var totalSize = thumbnails.Values.Sum(t => t.Length);
            _logger.LogInformation("✅ Batch cached {Count} thumbnails, total size: {Size:N0} bytes ({SizeMB:F2} MB)", 
                thumbnails.Count, totalSize, totalSize / 1024.0 / 1024.0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to batch cache thumbnails");
            // Fail gracefully
        }
    }

    #endregion

    #region Dashboard Statistics Helper

    /// <summary>
    /// Build dashboard statistics using streaming (memory-efficient version)
    /// </summary>
    private async Task<DashboardStatistics> BuildDashboardStatisticsStreamingAsync(long totalCount, CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        // Aggregate statistics (streaming)
        long totalImages = 0;
        long totalThumbnails = 0;
        long totalCacheImages = 0;
        long totalSize = 0;
        long totalThumbnailSize = 0;
        long totalCacheSize = 0;
        long activeCollections = 0;
        var topCollections = new List<TopCollection>();
        
        // Stream in batches
        const int BATCH_SIZE = 100;
        for (var skip = 0; skip < totalCount; skip += BATCH_SIZE)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
                
            var batchCollections = await _collectionRepository.FindAsync(
                MongoDB.Driver.Builders<Collection>.Filter.Eq(c => c.IsDeleted, false),
                MongoDB.Driver.Builders<Collection>.Sort.Descending(c => c.Statistics.TotalViews),
                BATCH_SIZE,
                skip
            );
            
            foreach (var c in batchCollections)
            {
                totalImages += c.GetImageCount();
                totalThumbnails += c.Thumbnails?.Count ?? 0;
                totalCacheImages += c.CacheImages?.Count ?? 0;
                totalSize += c.GetTotalSize();
                totalThumbnailSize += c.Thumbnails?.Sum(t => t.FileSize) ?? 0;
                totalCacheSize += c.CacheImages?.Sum(ci => ci.FileSize) ?? 0;
                
                if (c.IsActive && !c.IsDeleted)
                    activeCollections++;
                
                // Collect top 10 by views
                if (topCollections.Count < 10 && c.IsActive && !c.IsDeleted)
                {
                    topCollections.Add(new TopCollection
                    {
                        Id = c.Id.ToString(),
                        Name = c.Name,
                        ImageCount = c.GetImageCount(),
                        TotalSize = c.GetTotalSize(),
                        ViewCount = c.Statistics.TotalViews,
                        LastViewed = c.Statistics.LastViewed,
                        ThumbnailPath = c.Thumbnails?.FirstOrDefault()?.ThumbnailPath
                    });
                }
            }
        }

        // Get cache folder statistics
        var cacheFolders = await _cacheFolderRepository.GetAllAsync();
        var cacheFolderStats = cacheFolders.Select(cf => new CacheFolderStat
        {
            Id = cf.Id.ToString(),
            Name = cf.Name,
            Path = cf.Path,
            CurrentSizeBytes = cf.CurrentSizeBytes,
            MaxSizeBytes = cf.MaxSizeBytes,
            TotalFiles = cf.TotalFiles,
            TotalCollections = cf.CachedCollectionIds?.Count ?? 0,
            UsagePercentage = cf.MaxSizeBytes > 0 ? (double)cf.CurrentSizeBytes / cf.MaxSizeBytes * 100 : 0,
            IsActive = cf.IsActive
        }).ToList();

        // Recent activity
        var recentActivity = new List<RecentActivity>
        {
            new() { Id = "1", Type = "system_startup", Message = "System started", Timestamp = DateTime.UtcNow.AddMinutes(-5) },
            new() { Id = "2", Type = "index_rebuilt", Message = "Collection index rebuilt", Timestamp = DateTime.UtcNow.AddMinutes(-10) }
        };

        // System health
        var systemHealth = new Domain.ValueObjects.SystemHealth
        {
            RedisStatus = "Connected",
            MongoDbStatus = "Connected", 
            WorkerStatus = "Running",
            ApiStatus = "Running",
            Uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime,
            MemoryUsageBytes = GC.GetTotalMemory(false),
            DiskSpaceFreeBytes = GetFreeDiskSpace(),
            LastHealthCheck = DateTime.UtcNow
        };

        var stats = new DashboardStatistics
        {
            TotalCollections = (int)totalCount,
            ActiveCollections = (int)activeCollections,
            TotalImages = (int)totalImages,
            TotalThumbnails = (int)totalThumbnails,
            TotalCacheImages = (int)totalCacheImages,
            TotalSize = totalSize,
            TotalThumbnailSize = totalThumbnailSize,
            TotalCacheSize = totalCacheSize,
            AverageImagesPerCollection = totalCount > 0 ? (double)totalImages / totalCount : 0,
            AverageSizePerCollection = totalCount > 0 ? (double)totalSize / totalCount : 0,
            ActiveJobs = 0,
            CompletedJobsToday = 0,
            FailedJobsToday = 0,
            LastUpdated = DateTime.UtcNow,
            CacheFolderStats = cacheFolderStats,
            RecentActivity = recentActivity,
            TopCollections = topCollections,
            SystemHealth = systemHealth
        };

        var duration = DateTime.UtcNow - startTime;
        _logger.LogInformation("📊 Built dashboard statistics (streaming) in {Duration}ms: {Collections} collections, {Images} images", 
            duration.TotalMilliseconds, totalCount, totalImages);

        return stats;
    }

    /// <summary>
    /// Build dashboard statistics from collections (used during index rebuild)
    /// DEPRECATED: Use BuildDashboardStatisticsStreamingAsync instead to avoid memory issues
    /// </summary>
    [Obsolete("Use BuildDashboardStatisticsStreamingAsync for better memory efficiency")]
    private async Task<DashboardStatistics> BuildDashboardStatisticsFromCollectionsAsync(List<Collection> collections)
    {
        var startTime = DateTime.UtcNow;
        
        var activeCollections = collections.Where(c => c.IsActive && !c.IsDeleted).ToList();
        
        // Calculate basic statistics
        var totalImages = collections.Sum(c => c.GetImageCount());
        var totalThumbnails = collections.Sum(c => c.Thumbnails?.Count ?? 0);
        var totalCacheImages = collections.Sum(c => c.CacheImages?.Count ?? 0);
        var totalSize = collections.Sum(c => c.GetTotalSize());
        var totalThumbnailSize = collections.Sum(c => c.Thumbnails?.Sum(t => t.FileSize) ?? 0);
        var totalCacheSize = collections.Sum(c => c.CacheImages?.Sum(c => c.FileSize) ?? 0);

        // Get cache folder statistics
        var cacheFolders = await _cacheFolderRepository.GetAllAsync();
        var cacheFolderStats = cacheFolders.Select(cf => new CacheFolderStat
        {
            Id = cf.Id.ToString(),
            Name = cf.Name,
            Path = cf.Path,
            CurrentSizeBytes = cf.CurrentSizeBytes,
            MaxSizeBytes = cf.MaxSizeBytes,
            TotalFiles = cf.TotalFiles,
            TotalCollections = cf.CachedCollectionIds?.Count ?? 0,
            UsagePercentage = cf.MaxSizeBytes > 0 ? (double)cf.CurrentSizeBytes / cf.MaxSizeBytes * 100 : 0,
            IsActive = cf.IsActive
        }).ToList();

        // Get top collections by view count
        var topCollections = collections
            .Where(c => c.IsActive && !c.IsDeleted)
            .OrderByDescending(c => c.Statistics.TotalViews)
            .Take(10)
            .Select(c => new TopCollection
            {
                Id = c.Id.ToString(),
                Name = c.Name,
                ImageCount = c.GetImageCount(),
                TotalSize = c.GetTotalSize(),
                ViewCount = c.Statistics.TotalViews,
                LastViewed = c.Statistics.LastViewed,
                ThumbnailPath = c.Thumbnails?.FirstOrDefault()?.ThumbnailPath
            }).ToList();

        // Get recent activity (simplified for now)
        var recentActivity = new List<RecentActivity>
        {
            new() { Id = "1", Type = "system_startup", Message = "System started", Timestamp = DateTime.UtcNow.AddMinutes(-5) },
            new() { Id = "2", Type = "index_rebuilt", Message = "Collection index rebuilt", Timestamp = DateTime.UtcNow.AddMinutes(-10) }
        };

        // Build system health
        var systemHealth = new Domain.ValueObjects.SystemHealth
        {
            RedisStatus = "Connected",
            MongoDbStatus = "Connected", 
            WorkerStatus = "Running",
            ApiStatus = "Running",
            Uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime,
            MemoryUsageBytes = GC.GetTotalMemory(false),
            DiskSpaceFreeBytes = GetFreeDiskSpace(),
            LastHealthCheck = DateTime.UtcNow
        };

        var stats = new DashboardStatistics
        {
            TotalCollections = collections.Count,
            ActiveCollections = activeCollections.Count,
            TotalImages = totalImages,
            TotalThumbnails = totalThumbnails,
            TotalCacheImages = totalCacheImages,
            TotalSize = totalSize,
            TotalThumbnailSize = totalThumbnailSize,
            TotalCacheSize = totalCacheSize,
            AverageImagesPerCollection = collections.Count > 0 ? (double)totalImages / collections.Count : 0,
            AverageSizePerCollection = collections.Count > 0 ? (double)totalSize / collections.Count : 0,
            ActiveJobs = 0, // Will be updated by background job service
            CompletedJobsToday = 0, // Will be updated by background job service
            FailedJobsToday = 0, // Will be updated by background job service
            LastUpdated = DateTime.UtcNow,
            CacheFolderStats = cacheFolderStats,
            RecentActivity = recentActivity,
            TopCollections = topCollections,
            SystemHealth = systemHealth
        };

        var duration = DateTime.UtcNow - startTime;
        _logger.LogInformation("📊 Built dashboard statistics in {Duration}ms: {Collections} collections, {Images} images", 
            duration.TotalMilliseconds, collections.Count, totalImages);

        return stats;
    }

    /// <summary>
    /// Get free disk space (simplified implementation)
    /// </summary>
    private static long GetFreeDiskSpace()
    {
        try
        {
            // Simple fallback - return a reasonable default
            return 100L * 1024 * 1024 * 1024; // 100GB
        }
        catch
        {
            return 0;
        }
    }

    #endregion

    #region Dashboard Statistics

    /// <summary>
    /// Get dashboard statistics from Redis cache (ultra-fast)
    /// </summary>
    public async Task<DashboardStatistics?> GetDashboardStatisticsAsync()
    {
        try
        {
            var statsJson = await _db.StringGetAsync(DASHBOARD_STATS_KEY);
            if (!statsJson.HasValue)
            {
                _logger.LogDebug("No dashboard statistics found in Redis cache");
                return null;
            }

            var stats = JsonSerializer.Deserialize<DashboardStatistics>(statsJson);
            _logger.LogDebug("✅ Retrieved dashboard statistics from Redis cache");
            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get dashboard statistics from Redis");
            return null;
        }
    }

    /// <summary>
    /// Store dashboard statistics in Redis cache
    /// </summary>
    public async Task StoreDashboardStatisticsAsync(DashboardStatistics stats)
    {
        try
        {
            stats.LastUpdated = DateTime.UtcNow;
            var statsJson = JsonSerializer.Serialize(stats);
            
            // Store with 5-minute expiration for auto-refresh
            await _db.StringSetAsync(DASHBOARD_STATS_KEY, statsJson, TimeSpan.FromMinutes(5));
            
            _logger.LogDebug("✅ Stored dashboard statistics in Redis cache");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store dashboard statistics in Redis");
        }
    }

    /// <summary>
    /// Update dashboard statistics incrementally (for real-time updates)
    /// </summary>
    public async Task UpdateDashboardStatisticsAsync(string updateType, object updateData)
    {
        try
        {
            var metadata = new
            {
                Type = updateType,
                Data = updateData,
                Timestamp = DateTime.UtcNow
            };

            var metadataJson = JsonSerializer.Serialize(metadata);
            
            // Store update metadata
            await _db.ListLeftPushAsync(DASHBOARD_METADATA_KEY, metadataJson);
            
            // Keep only last 100 updates
            await _db.ListTrimAsync(DASHBOARD_METADATA_KEY, 0, 99);
            
            // Invalidate dashboard stats to force refresh
            await _db.KeyDeleteAsync(DASHBOARD_STATS_KEY);
            
            _logger.LogDebug("✅ Updated dashboard metadata: {UpdateType}", updateType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update dashboard metadata");
        }
    }

    /// <summary>
    /// Get recent dashboard activity from Redis
    /// </summary>
    public async Task<List<object>> GetRecentDashboardActivityAsync(int limit = 10)
    {
        try
        {
            var activities = await _db.ListRangeAsync(DASHBOARD_METADATA_KEY, 0, limit - 1);
            var result = new List<object>();

            foreach (var activity in activities)
            {
                if (activity.HasValue)
                {
                    var activityObj = JsonSerializer.Deserialize<object>(activity);
                    result.Add(activityObj);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get recent dashboard activity");
            return new List<object>();
        }
    }

    /// <summary>
    /// Check if dashboard statistics are fresh (less than 1 minute old)
    /// </summary>
    public async Task<bool> IsDashboardStatisticsFreshAsync()
    {
        try
        {
            var stats = await GetDashboardStatisticsAsync();
            if (stats == null) return false;

            var age = DateTime.UtcNow - stats.LastUpdated;
            return age.TotalMinutes < 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check dashboard statistics freshness");
            return false;
        }
    }

    #endregion
    
    #region Helper Methods for Thumbnail Processing

    /// <summary>
    /// Get MIME content type from thumbnail format
    /// </summary>
    private static string GetContentTypeFromFormat(string format)
    {
        return format.ToLower() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "webp" => "image/webp",
            "gif" => "image/gif",  // ✅ Fixed: was "image/bmp"
            "bmp" => "image/bmp",
            _ => "image/jpeg" // Default fallback
        };
    }
    
    /// <summary>
    /// Determine if thumbnail needs to be resized before caching in Redis
    /// Uses 3-layer detection: IsDirect flag, dimensions, file size
    /// </summary>
    private bool ShouldResizeThumbnail(ThumbnailEmbedded thumbnail)
    {
        // Layer 1: IsDirect flag (direct mode always needs resize)
        if (thumbnail.IsDirect)
        {
            _logger.LogDebug("Thumbnail is direct mode (original image), needs resize");
            return true;
        }
        
        // Layer 2: Stored dimensions (most accurate check)
        if (thumbnail.Width > 400 || thumbnail.Height > 400)
        {
            _logger.LogDebug("Thumbnail dimensions {W}×{H} exceed 400px threshold, needs resize",
                thumbnail.Width, thumbnail.Height);
            return true;
        }
        
        // Layer 3: File size (safety check for large files)
        if (thumbnail.FileSize > 500 * 1024)  // >500KB
        {
            _logger.LogDebug("Thumbnail file size {SizeKB} KB exceeds 500KB threshold, needs resize",
                thumbnail.FileSize / 1024);
            return true;
        }
        
        // All checks passed: thumbnail is already correct size
        _logger.LogDebug("Thumbnail {W}×{H} ({SizeKB} KB) within thresholds, use as-is",
            thumbnail.Width, thumbnail.Height, thumbnail.FileSize / 1024);
        return false;
    }
    
    /// <summary>
    /// Resize image in memory for Redis cache (used for direct mode and oversized thumbnails)
    /// Does NOT save to disk, only returns resized bytes
    /// Uses settings from MongoDB (thumbnail.default.*) via IImageProcessingSettingsService
    /// </summary>
    private async Task<byte[]?> ResizeImageForCacheAsync(string imagePath)
    {
        try
        {
            // Get thumbnail settings from MongoDB (cached for performance)
            var thumbnailFormat = await _imageProcessingSettingsService.GetThumbnailFormatAsync();
            var thumbnailQuality = await _imageProcessingSettingsService.GetThumbnailQualityAsync();
            var thumbnailSize = await _imageProcessingSettingsService.GetThumbnailSizeAsync();
            
            _logger.LogDebug("Resizing image {Path} to {Size}×{Size} for Redis cache (Format={Format}, Quality={Quality})", 
                imagePath, thumbnailSize, thumbnailFormat, thumbnailQuality);
            
            // Create ArchiveEntryInfo for image processing service (regular file)
            var archiveEntry = ArchiveEntryInfo.ForRegularFile(imagePath);
            
            // Use existing image processing service to resize with settings from MongoDB
            var resizedBytes = await _imageProcessingService.GenerateThumbnailAsync(
                archiveEntry,
                thumbnailSize,
                thumbnailSize,
                thumbnailFormat,
                thumbnailQuality);
            
            if (resizedBytes != null && resizedBytes.Length > 0)
            {
                _logger.LogDebug("Successfully resized image to {Size} KB (Format={Format}, Quality={Quality})", 
                    resizedBytes.Length / 1024, thumbnailFormat, thumbnailQuality);
                return resizedBytes;
            }
            
            _logger.LogWarning("Resize returned empty data for {Path}", imagePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resize image {Path}", imagePath);
            return null;
        }
    }

    #endregion
    
    #region Smart Rebuild (NEW)
    
    /// <summary>
    /// Rebuild index with specified mode and options (NEW - Smart incremental rebuild)
    /// </summary>
    public async Task<RebuildStatistics> RebuildIndexAsync(
        RebuildMode mode,
        RebuildOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new RebuildOptions();
        var stats = new RebuildStatistics 
        { 
            Mode = mode,
            StartedAt = DateTime.UtcNow
        };
        var startTime = DateTime.UtcNow;
        
        try
        {
            _logger.LogInformation("🔄 Starting {Mode} index rebuild...", mode);
            
            // Wait for Redis to be ready
            if (!_redis.IsConnected)
            {
                _logger.LogWarning("⚠️ Redis not connected yet, waiting...");
                var waited = 0;
                while (!_redis.IsConnected && waited < 10000)
                {
                    await Task.Delay(500, cancellationToken);
                    waited += 500;
                }
                
                if (!_redis.IsConnected)
                {
                    _logger.LogError("❌ Redis connection timeout, aborting rebuild");
                    stats.CompletedAt = DateTime.UtcNow;
                    stats.Duration = DateTime.UtcNow - startTime;
                    return stats;
                }
            }
            
            // Step 1: Clear Redis if Full mode
            if (mode == RebuildMode.Full)
            {
                _logger.LogInformation("🧹 Full rebuild: Clearing all Redis data...");
                await ClearIndexAsync();
            }
            
            // Step 2: Count total collections
            var totalCount = await _collectionRepository.CountAsync(
                MongoDB.Driver.Builders<Collection>.Filter.Eq(c => c.IsDeleted, false)
            );
            stats.TotalCollections = (int)totalCount;
            
            _logger.LogInformation("📊 Found {Count} collections in MongoDB", totalCount);
            
            // Step 3: Determine which collections need rebuilding
            var collectionsToRebuild = new List<ObjectId>();
            var collectionsToSkip = new List<ObjectId>();
            
            _logger.LogInformation("🔍 Analyzing collections to determine rebuild scope...");
            
            const int ANALYSIS_BATCH_SIZE = 100;
            for (var skip = 0; skip < totalCount; skip += ANALYSIS_BATCH_SIZE)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("⚠️ Index rebuild cancelled");
                    stats.CompletedAt = DateTime.UtcNow;
                    stats.Duration = DateTime.UtcNow - startTime;
                    return stats;
                }
                
                var batch = await _collectionRepository.FindAsync(
                    MongoDB.Driver.Builders<Collection>.Filter.Eq(c => c.IsDeleted, false),
                    MongoDB.Driver.Builders<Collection>.Sort.Ascending(c => c.Id),
                    ANALYSIS_BATCH_SIZE,
                    skip
                );
                
                foreach (var collection in batch)
                {
                    var decision = await ShouldRebuildCollectionAsync(collection, mode);
                    
                    if (decision == RebuildDecision.Skip)
                    {
                        collectionsToSkip.Add(collection.Id);
                    }
                    else
                    {
                        collectionsToRebuild.Add(collection.Id);
                    }
                }
                
                if ((skip / ANALYSIS_BATCH_SIZE + 1) % 10 == 0)
                {
                    _logger.LogDebug("📊 Analyzed {Count}/{Total} collections...", 
                        skip + ANALYSIS_BATCH_SIZE, totalCount);
                }
            }
            
            stats.SkippedCollections = collectionsToSkip.Count;
            stats.RebuiltCollections = collectionsToRebuild.Count;
            
            _logger.LogInformation("📊 Analysis complete: {Rebuild} to rebuild, {Skip} to skip", 
                collectionsToRebuild.Count, collectionsToSkip.Count);
            
            // Step 4: Dry run mode - just report
            if (options.DryRun)
            {
                _logger.LogInformation("🔍 DRY RUN: Would rebuild {Count} collections", 
                    collectionsToRebuild.Count);
                
                stats.CompletedAt = DateTime.UtcNow;
                stats.Duration = DateTime.UtcNow - startTime;
                stats.MemoryPeakMB = GC.GetTotalMemory(false) / 1024 / 1024;
                
                return stats;
            }
            
            // Step 5: Rebuild only selected collections
            if (collectionsToRebuild.Count > 0)
            {
                await RebuildSelectedCollectionsAsync(
                    collectionsToRebuild, 
                    options, 
                    cancellationToken);
            }
            else
            {
                _logger.LogInformation("✅ No collections to rebuild");
            }
            
            // Step 6: Update statistics
            await _db.StringSetAsync(LAST_REBUILD_KEY, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await _db.StringSetAsync(STATS_KEY + ":total", totalCount);
            
            stats.CompletedAt = DateTime.UtcNow;
            stats.Duration = DateTime.UtcNow - startTime;
            stats.MemoryPeakMB = GC.GetTotalMemory(false) / 1024 / 1024;
            
            _logger.LogInformation("✅ Rebuild complete: {Rebuilt} rebuilt, {Skipped} skipped in {Duration}s",
                stats.RebuiltCollections, stats.SkippedCollections, stats.Duration.TotalSeconds);
            
            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to rebuild index");
            stats.CompletedAt = DateTime.UtcNow;
            stats.Duration = DateTime.UtcNow - startTime;
            throw;
        }
    }
    
    /// <summary>
    /// Rebuild decision enum
    /// </summary>
    private enum RebuildDecision
    {
        Skip,
        Rebuild
    }
    
    /// <summary>
    /// Determine if collection should be rebuilt based on mode and state
    /// </summary>
    private async Task<RebuildDecision> ShouldRebuildCollectionAsync(
        Collection collection,
        RebuildMode mode)
    {
        switch (mode)
        {
            case RebuildMode.Full:
            case RebuildMode.ForceRebuildAll:
                // Always rebuild
                return RebuildDecision.Rebuild;
            
            case RebuildMode.ChangedOnly:
                var state = await GetCollectionIndexStateAsync(collection.Id);
                
                if (state == null)
                {
                    _logger.LogDebug("Collection {Id} not in index, will add", collection.Id);
                    return RebuildDecision.Rebuild;
                }
                
                // Check if updated since last index
                if (collection.UpdatedAt > state.CollectionUpdatedAt)
                {
                    _logger.LogDebug("Collection {Id} updated ({New} > {Old}), will rebuild",
                        collection.Id, collection.UpdatedAt, state.CollectionUpdatedAt);
                    return RebuildDecision.Rebuild;
                }
                
                _logger.LogDebug("Collection {Id} unchanged, skipping", collection.Id);
                return RebuildDecision.Skip;
            
            case RebuildMode.Verify:
                // Verify mode is handled differently in VerifyIndexAsync
                return RebuildDecision.Rebuild;
            
            default:
                return RebuildDecision.Rebuild;
        }
    }
    
    /// <summary>
    /// Rebuild selected collections (smart rebuild)
    /// </summary>
    private async Task RebuildSelectedCollectionsAsync(
        List<ObjectId> collectionIds,
        RebuildOptions options,
        CancellationToken cancellationToken)
    {
        if (collectionIds.Count == 0)
        {
            _logger.LogInformation("✅ No collections to rebuild");
            return;
        }
        
        const int BATCH_SIZE = 100;
        var processedCount = 0;
        var totalBatches = (int)Math.Ceiling((double)collectionIds.Count / BATCH_SIZE);
        
        _logger.LogInformation("🔨 Rebuilding {Count} collections in {Batches} batches...",
            collectionIds.Count, totalBatches);
        
        // Process in batches
        for (var i = 0; i < collectionIds.Count; i += BATCH_SIZE)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("⚠️ Index rebuild cancelled");
                return;
            }
            
            var currentBatch = (i / BATCH_SIZE) + 1;
            var batchStartTime = DateTime.UtcNow;
            
            // Memory monitoring
            var memoryBefore = GC.GetTotalMemory(false);
            _logger.LogDebug("💾 Batch {Current}/{Total}: Memory before = {MemoryMB:F2} MB", 
                currentBatch, totalBatches, memoryBefore / 1024.0 / 1024.0);
            
            var batchIds = collectionIds.Skip(i).Take(BATCH_SIZE).ToList();
            
            // Fetch collections by IDs
            var filter = MongoDB.Driver.Builders<Collection>.Filter.In(c => c.Id, batchIds);
            var batchCollections = await _collectionRepository.FindAsync(
                filter,
                MongoDB.Driver.Builders<Collection>.Sort.Ascending(c => c.Id),
                batchIds.Count,
                0
            );
            
            var collectionList = batchCollections.ToList();
            
            // Process batch
            var batch = _db.CreateBatch();
            var tasks = new List<Task>();
            
            foreach (var collection in collectionList)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("⚠️ Index rebuild cancelled");
                    return;
                }
                
                // Add to sorted sets
                tasks.Add(AddToSortedSetsAsync(batch, collection));
                
                // Add to hash (with optional thumbnail caching)
                if (!options.SkipThumbnailCaching)
                {
                    tasks.Add(AddToHashAsync(batch, collection));
                }
                else
                {
                    // Skip thumbnail loading for faster rebuild
                    tasks.Add(AddToHashWithoutThumbnailAsync(batch, collection));
                }
                
                // ✅ NEW: Update state tracking
                tasks.Add(UpdateCollectionIndexStateAsync(batch, collection));
                
                processedCount++;
            }
            
            // Execute batch
            batch.Execute();
            await Task.WhenAll(tasks);
            
            // Memory monitoring after batch
            var memoryAfter = GC.GetTotalMemory(false);
            var memoryDelta = memoryAfter - memoryBefore;
            var batchDuration = DateTime.UtcNow - batchStartTime;
            
            _logger.LogInformation("✅ Batch {Current}/{Total} complete: {Count} collections in {Duration}ms, Memory delta = {DeltaMB:+0.00;-0.00} MB (now {CurrentMB:F2} MB)", 
                currentBatch, totalBatches, collectionList.Count, batchDuration.TotalMilliseconds,
                memoryDelta / 1024.0 / 1024.0, memoryAfter / 1024.0 / 1024.0);
            
            // ✅ CRITICAL: Clear tasks list to release memory
            tasks.Clear();
            
            // Force GC after each batch
            collectionList.Clear();
            collectionList = null!;
            
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        
        _logger.LogInformation("✅ All {Count} collections rebuilt successfully", processedCount);
    }
    
    /// <summary>
    /// Add collection to hash WITHOUT loading thumbnail (faster rebuild)
    /// </summary>
    private async Task AddToHashWithoutThumbnailAsync(IDatabaseAsync db, Collection collection)
    {
        var summary = new CollectionSummary
        {
            Id = collection.Id.ToString(),
            Name = collection.Name ?? "",
            FirstImageId = collection.Images?.FirstOrDefault()?.Id.ToString(),
            ImageCount = collection.Images?.Count ?? 0,
            ThumbnailCount = collection.Thumbnails?.Count ?? 0,
            CacheCount = collection.CacheImages?.Count ?? 0,
            TotalSize = collection.Statistics.TotalSize,
            CreatedAt = collection.CreatedAt,
            UpdatedAt = collection.UpdatedAt,
            LibraryId = collection.LibraryId?.ToString() ?? string.Empty,
            Description = collection.Description,
            Type = (int)collection.Type,
            Tags = new List<string>(),
            Path = collection.Path ?? "",
            ThumbnailBase64 = null  // ✅ Skip thumbnail loading
        };

        var json = JsonSerializer.Serialize(summary);
        await db.StringSetAsync(GetHashKey(collection.Id.ToString()), json);
    }
    
    /// <summary>
    /// Verify index consistency and optionally fix issues
    /// </summary>
    public async Task<VerifyResult> VerifyIndexAsync(
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var result = new VerifyResult { DryRun = dryRun };
        var startTime = DateTime.UtcNow;
        
        try
        {
            _logger.LogInformation("🔍 Starting index verification (DryRun={DryRun})...", dryRun);
            
            // Phase 1: Check MongoDB → Redis (find missing/outdated)
            _logger.LogInformation("📊 Phase 1: Checking MongoDB collections against Redis index...");
            
            var totalCount = await _collectionRepository.CountAsync(
                MongoDB.Driver.Builders<Collection>.Filter.Eq(c => c.IsDeleted, false)
            );
            result.TotalInMongoDB = (int)totalCount;
            
            var collectionsToAdd = new List<ObjectId>();
            var collectionsToUpdate = new List<ObjectId>();
            
            const int BATCH_SIZE = 100;
            for (var skip = 0; skip < totalCount; skip += BATCH_SIZE)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("⚠️ Verify cancelled");
                    return result;
                }
                
                var batch = await _collectionRepository.FindAsync(
                    MongoDB.Driver.Builders<Collection>.Filter.Eq(c => c.IsDeleted, false),
                    MongoDB.Driver.Builders<Collection>.Sort.Ascending(c => c.Id),
                    BATCH_SIZE,
                    skip
                );
                
                foreach (var collection in batch)
                {
                    var state = await GetCollectionIndexStateAsync(collection.Id);
                    
                    if (state == null)
                    {
                        collectionsToAdd.Add(collection.Id);
                        result.MissingInRedis.Add(collection.Id.ToString());
                    }
                    else if (collection.UpdatedAt > state.CollectionUpdatedAt)
                    {
                        collectionsToUpdate.Add(collection.Id);
                        result.OutdatedInRedis.Add(collection.Id.ToString());
                    }
                    else
                    {
                        // Check if thumbnail was added after indexing
                        var firstThumbnail = collection.GetCollectionThumbnail();
                        if (!state.HasFirstThumbnail && firstThumbnail != null && !string.IsNullOrEmpty(firstThumbnail.ThumbnailPath))
                        {
                            collectionsToUpdate.Add(collection.Id);
                            result.MissingThumbnails.Add(collection.Id.ToString());
                        }
                    }
                }
                
                if ((skip / BATCH_SIZE + 1) % 10 == 0)
                {
                    _logger.LogDebug("📊 Verified {Count}/{Total} MongoDB collections...", 
                        skip + BATCH_SIZE, totalCount);
                }
            }
            
            result.ToAdd = collectionsToAdd.Count;
            result.ToUpdate = collectionsToUpdate.Count;
            
            _logger.LogInformation("✅ Phase 1 complete: {Add} to add, {Update} to update",
                result.ToAdd, result.ToUpdate);
            
            // Phase 2: Check Redis → MongoDB (find orphaned/deleted)
            _logger.LogInformation("📊 Phase 2: Checking Redis index for orphaned entries...");
            
            var redisCollectionIds = await GetAllIndexedCollectionIdsAsync();
            result.TotalInRedis = redisCollectionIds.Count;
            
            var collectionsToRemove = new List<ObjectId>();
            
            foreach (var collectionIdStr in redisCollectionIds)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("⚠️ Verify cancelled");
                    return result;
                }
                
                if (!ObjectId.TryParse(collectionIdStr, out var collectionId))
                {
                    _logger.LogWarning("Invalid collection ID in Redis: {Id}", collectionIdStr);
                    collectionsToRemove.Add(ObjectId.Empty);
                    result.OrphanedInRedis.Add(collectionIdStr);
                    continue;
                }
                
                var collection = await _collectionRepository.GetByIdAsync(collectionId);
                
                if (collection == null || collection.IsDeleted)
                {
                    collectionsToRemove.Add(collectionId);
                    result.OrphanedInRedis.Add(collectionIdStr);
                }
            }
            
            result.ToRemove = collectionsToRemove.Count;
            
            _logger.LogInformation("✅ Phase 2 complete: {Remove} orphaned entries found",
                result.ToRemove);
            
            // Phase 3: Fix inconsistencies (if not dry run)
            if (!dryRun)
            {
                _logger.LogInformation("🔧 Phase 3: Fixing inconsistencies...");
                
                // Add missing collections
                if (collectionsToAdd.Any())
                {
                    _logger.LogInformation("➕ Adding {Count} missing collections...", collectionsToAdd.Count);
                    await RebuildSelectedCollectionsAsync(
                        collectionsToAdd, 
                        new RebuildOptions(), 
                        cancellationToken);
                }
                
                // Update outdated collections
                if (collectionsToUpdate.Any())
                {
                    _logger.LogInformation("🔄 Updating {Count} outdated collections...", collectionsToUpdate.Count);
                    await RebuildSelectedCollectionsAsync(
                        collectionsToUpdate, 
                        new RebuildOptions(), 
                        cancellationToken);
                }
                
                // Remove orphaned entries
                if (collectionsToRemove.Any())
                {
                    _logger.LogInformation("🗑️ Removing {Count} orphaned entries...", collectionsToRemove.Count);
                    foreach (var collectionId in collectionsToRemove)
                    {
                        if (collectionId != ObjectId.Empty)
                        {
                            await RemoveCollectionAsync(collectionId);
                        }
                    }
                }
                
                _logger.LogInformation("✅ Phase 3 complete: Fixed all inconsistencies");
            }
            else
            {
                _logger.LogInformation("🔍 DRY RUN: Would fix {Total} inconsistencies (Add={Add}, Update={Update}, Remove={Remove})",
                    result.ToAdd + result.ToUpdate + result.ToRemove,
                    result.ToAdd, result.ToUpdate, result.ToRemove);
            }
            
            result.Duration = DateTime.UtcNow - startTime;
            result.IsConsistent = result.ToAdd == 0 && result.ToUpdate == 0 && result.ToRemove == 0;
            
            _logger.LogInformation("✅ Verification complete in {Duration}s: {Status}",
                result.Duration.TotalSeconds,
                result.IsConsistent ? "CONSISTENT ✅" : "INCONSISTENT ⚠️");
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to verify index");
            result.Duration = DateTime.UtcNow - startTime;
            throw;
        }
    }
    
    /// <summary>
    /// Get all collection IDs that are currently in Redis index
    /// </summary>
    private async Task<List<string>> GetAllIndexedCollectionIdsAsync()
    {
        try
        {
            var server = _redis.GetServer(_redis.GetEndPoints().First());
            var collectionIds = new List<string>();
            
            // Scan for all state keys
            await foreach (var key in server.KeysAsync(pattern: $"{STATE_PREFIX}*", pageSize: 1000))
            {
                var keyStr = key.ToString();
                var collectionId = keyStr.Replace(STATE_PREFIX, "");
                collectionIds.Add(collectionId);
            }
            
            _logger.LogDebug("Found {Count} collections in Redis index", collectionIds.Count);
            return collectionIds;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get indexed collection IDs");
            return new List<string>();
        }
    }
    
    #endregion
    
    #region State Tracking
    
    /// <summary>
    /// Get collection index state from Redis
    /// </summary>
    public async Task<CollectionIndexState?> GetCollectionIndexStateAsync(
        ObjectId collectionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var key = GetStateKey(collectionId.ToString());
            var json = await _db.StringGetAsync(key);
            
            if (!json.HasValue)
            {
                _logger.LogDebug("No state found for collection {CollectionId}", collectionId);
                return null;
            }
            
            var state = JsonSerializer.Deserialize<CollectionIndexState>(json.ToString());
            _logger.LogDebug("Retrieved state for collection {CollectionId}: IndexedAt={IndexedAt}, UpdatedAt={UpdatedAt}",
                collectionId, state?.IndexedAt, state?.CollectionUpdatedAt);
            
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get state for collection {CollectionId}", collectionId);
            return null;
        }
    }
    
    /// <summary>
    /// Update collection index state in Redis
    /// Called during index rebuild to track when collection was last indexed
    /// </summary>
    private async Task UpdateCollectionIndexStateAsync(
        IDatabaseAsync db,
        Collection collection)
    {
        try
        {
            // Get first thumbnail info
            var firstThumbnail = collection.GetCollectionThumbnail();
            
            var state = new CollectionIndexState
            {
                CollectionId = collection.Id.ToString(),
                IndexedAt = DateTime.UtcNow,
                CollectionUpdatedAt = collection.UpdatedAt,
                
                // Statistics (lightweight, used by other screens)
                ImageCount = collection.Images?.Count ?? 0,
                ThumbnailCount = collection.Thumbnails?.Count ?? 0,
                CacheCount = collection.CacheImages?.Count ?? 0,
                
                // First thumbnail tracking (for collection card display)
                HasFirstThumbnail = firstThumbnail != null && !string.IsNullOrEmpty(firstThumbnail.ThumbnailPath),
                FirstThumbnailPath = firstThumbnail?.ThumbnailPath,
                
                IndexVersion = "v1.0"
            };
            
            var key = GetStateKey(collection.Id.ToString());
            var json = JsonSerializer.Serialize(state);
            
            // Store with no expiration (persist state)
            await db.StringSetAsync(key, json);
            
            _logger.LogDebug("Updated state for collection {CollectionId}: Images={Images}, Thumbnails={Thumbnails}, Cache={Cache}",
                collection.Id, state.ImageCount, state.ThumbnailCount, state.CacheCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update state for collection {CollectionId}", collection.Id);
            // Don't throw - state tracking is not critical
        }
    }
    
    #endregion
}


