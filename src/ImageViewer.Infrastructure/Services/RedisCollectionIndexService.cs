using System.Text.Json;
using ImageViewer.Application.Mappings;
using ImageViewer.Application.Services;
using ImageViewer.Domain.Entities;
using ImageViewer.Domain.Enums;
using ImageViewer.Domain.Interfaces;
using ImageViewer.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
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
                
                // Process collections sequentially (no batch due to async I/O operations)
                // Using batch with async file I/O causes deadlock
                foreach (var collection in collectionList)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("⚠️ Index rebuild cancelled");
                        return;
                    }

                    try
                    {
                        // Process each collection with direct database operations (not batched)
                        await AddToSortedSetsAsync(_db, collection);
                        await AddToHashAsync(_db, collection);
                        await UpdateCollectionIndexStateAsync(_db, collection);
                    
                    processedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to index collection {CollectionId}, skipping", collection.Id);
                        // Continue with next collection
                    }
                }

                // Memory monitoring after batch
                var memoryAfter = GC.GetTotalMemory(false);
                var memoryDelta = memoryAfter - memoryBefore;
                var batchDuration = DateTime.UtcNow - batchStartTime;
                
                _logger.LogInformation("✅ Batch {Current}/{Total} completed: {Count} collections in {Duration}ms, Memory delta = {DeltaMB:+0.00;-0.00} MB (now {CurrentMB:F2} MB)", 
                    currentBatch, totalBatches, collectionList.Count, batchDuration.TotalMilliseconds,
                    memoryDelta / 1024.0 / 1024.0, memoryAfter / 1024.0 / 1024.0);
                
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
            "name" => GetNameScore(collection.Name) * multiplier,
            "imagecount" => collection.Statistics.TotalItems * multiplier,
            "totalsize" => collection.Statistics.TotalSize * multiplier,
            _ => collection.UpdatedAt.Ticks * multiplier
        };
    }
    
    /// <summary>
    /// Convert collection name to a score that preserves alphabetical order.
    /// Uses first 10 characters (case-insensitive) to create sortable numeric score.
    /// </summary>
    private double GetNameScore(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return 0;
            
        // Normalize: lowercase and trim
        var normalized = name.ToLowerInvariant().Trim();
        if (string.IsNullOrEmpty(normalized))
            return 0;
        
        // Convert first 10 characters to a numeric score that preserves order
        // Each character contributes to a position in base-256 number
        double score = 0;
        int maxChars = Math.Min(10, normalized.Length);
        
        for (int i = 0; i < maxChars; i++)
        {
            // Get Unicode value (0-65535 for most characters)
            int charValue = (int)normalized[i];
            
            // Add to score with decreasing weight for each position
            // Position 0 has highest weight, position 9 has lowest
            // Using base 256 (enough for extended ASCII)
            score += charValue * Math.Pow(256, 9 - i);
        }
        
        return score;
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
            
            // Deserialize all summaries while PRESERVING ORDER from sorted set
            // Use Dictionary for O(1) lookup, then rebuild list in original order
            var summaryDict = new Dictionary<string, CollectionSummary>();
            
            for (int i = 0; i < jsonValues.Length; i++)
            {
                if (jsonValues[i].HasValue)
                {
                    try
                    {
                        var summary = JsonSerializer.Deserialize<CollectionSummary>(jsonValues[i].ToString());
                        if (summary != null)
                        {
                            summaryDict[collectionIds[i].ToString()] = summary;
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize collection {Id}", collectionIds[i]);
                    }
                }
            }
            
            // Rebuild list in the EXACT order from sorted set (critical for sort correctness!)
            var summaries = new List<CollectionSummary>(collectionIds.Length);
            foreach (var collectionId in collectionIds)
            {
                if (summaryDict.TryGetValue(collectionId.ToString(), out var summary))
                {
                    summaries.Add(summary);
                }
                // Skip missing collections but maintain order of existing ones
            }
            
            _logger.LogDebug("Batch retrieved {Found}/{Total} collection summaries (order preserved)", summaries.Count, collectionIds.Length);
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

    public async Task<CollectionPageResult> SearchCollectionPageAsync(
        string searchQuery,
        int page,
        int pageSize,
        string sortBy = "updatedAt",
        string sortDirection = "desc",
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Searching collections: query='{Query}', page={Page}, pageSize={PageSize}, sortBy={SortBy}, sortDirection={SortDirection}",
                searchQuery, page, pageSize, sortBy, sortDirection);

            // Use MongoDB for search - much faster than loading all Redis data
            // MongoDB has text index on name/description/tags
            var searchLower = searchQuery.ToLowerInvariant();
            
            // Build MongoDB filter: case-insensitive search on name and path
            var filterBuilder = MongoDB.Driver.Builders<Collection>.Filter;
            var searchFilter = filterBuilder.And(
                filterBuilder.Eq(c => c.IsDeleted, false),
                filterBuilder.Or(
                    filterBuilder.Regex(c => c.Name, new MongoDB.Bson.BsonRegularExpression(searchQuery, "i")),
                    filterBuilder.Regex(c => c.Path, new MongoDB.Bson.BsonRegularExpression(searchQuery, "i"))
                )
            );

            // Build sort definition
            var sortBuilder = MongoDB.Driver.Builders<Collection>.Sort;
            MongoDB.Driver.SortDefinition<Collection> sortDef = sortBy.ToLower() switch
            {
                "updatedat" => sortDirection == "desc" 
                    ? sortBuilder.Descending(c => c.UpdatedAt)
                    : sortBuilder.Ascending(c => c.UpdatedAt),
                "createdat" => sortDirection == "desc"
                    ? sortBuilder.Descending(c => c.CreatedAt)
                    : sortBuilder.Ascending(c => c.CreatedAt),
                "name" => sortDirection == "desc"
                    ? sortBuilder.Descending(c => c.Name)
                    : sortBuilder.Ascending(c => c.Name),
                "imagecount" => sortDirection == "desc"
                    ? sortBuilder.Descending(c => c.Statistics.TotalItems)
                    : sortBuilder.Ascending(c => c.Statistics.TotalItems),
                "totalsize" => sortDirection == "desc"
                    ? sortBuilder.Descending(c => c.Statistics.TotalSize)
                    : sortBuilder.Ascending(c => c.Statistics.TotalSize),
                _ => sortDirection == "desc"
                    ? sortBuilder.Descending(c => c.UpdatedAt)
                    : sortBuilder.Ascending(c => c.UpdatedAt)
            };

            // Get total count for pagination
            var totalCount = await _collectionRepository.CountAsync(searchFilter);

            // Fetch only the page we need from MongoDB (not all data!)
            var skip = (page - 1) * pageSize;
            var collections = await _collectionRepository.FindAsync(
                searchFilter,
                sortDef,
                pageSize,
                skip
            );

            var collectionList = collections.ToList();
            
            _logger.LogDebug("Search found {Count} collections on page {Page} (total {Total})", 
                collectionList.Count, page, totalCount);

            // Convert to CollectionSummary format
            var summaries = collectionList.Select(c => new CollectionSummary
            {
                Id = c.Id.ToString(),
                Name = c.Name ?? "",
                FirstImageId = c.Images?.FirstOrDefault()?.Id.ToString(),
                ImageCount = c.Images?.Count ?? 0,
                ThumbnailCount = c.Thumbnails?.Count ?? 0,
                CacheCount = c.CacheImages?.Count ?? 0,
                TotalSize = c.Statistics.TotalSize,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
                LibraryId = c.LibraryId?.ToString() ?? string.Empty,
                Description = c.Description,
                Type = (int)c.Type,
                Tags = new List<string>(),
                Path = c.Path ?? "",
                ThumbnailBase64 = null // Will be populated from Redis below
            }).ToList();

            // Load thumbnails from Redis for this page only (much faster than loading from disk)
            // Redis already has pre-cached base64 thumbnails from the index
            await LoadThumbnailsFromRedisAsync(summaries);

            var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

            return new CollectionPageResult
            {
                Collections = summaries,
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
            _logger.LogError(ex, "Failed to search collections with query '{Query}'", searchQuery);
            throw;
        }
    }

    /// <summary>
    /// Load thumbnails from Redis cache for a list of collection summaries.
    /// Redis already has pre-cached base64 thumbnails from the index rebuild.
    /// This is MUCH faster than loading from disk.
    /// </summary>
    private async Task LoadThumbnailsFromRedisAsync(List<CollectionSummary> summaries)
    {
        if (summaries.Count == 0)
            return;

        try
        {
            // Get collection IDs
            var collectionIds = summaries
                .Select(s => new RedisValue(s.Id))
                .ToArray();

            // Batch fetch full summaries from Redis (which include thumbnails)
            var cachedSummaries = await BatchGetCollectionSummariesAsync(collectionIds);

            // Create lookup dictionary
            var thumbnailLookup = cachedSummaries
                .Where(s => !string.IsNullOrEmpty(s.ThumbnailBase64))
                .ToDictionary(s => s.Id, s => s.ThumbnailBase64);

            // Update thumbnails in the summaries
            foreach (var summary in summaries)
            {
                if (thumbnailLookup.TryGetValue(summary.Id, out var thumbnailBase64))
                {
                    summary.ThumbnailBase64 = thumbnailBase64;
                }
            }

            _logger.LogDebug("Loaded {Count} thumbnails from Redis cache for search results", thumbnailLookup.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load thumbnails from Redis, continuing without thumbnails");
            // Don't throw - thumbnails are optional
        }
    }

    /// <summary>
    /// Sort collections in memory using same logic as Redis sorted sets
    /// </summary>
    private List<CollectionSummary> SortCollections(List<CollectionSummary> collections, string sortBy, string sortDirection)
    {
        var multiplier = sortDirection == "desc" ? -1 : 1;

        return sortBy.ToLower() switch
        {
            "updatedat" => multiplier > 0
                ? collections.OrderBy(c => c.UpdatedAt).ToList()
                : collections.OrderByDescending(c => c.UpdatedAt).ToList(),

            "createdat" => multiplier > 0
                ? collections.OrderBy(c => c.CreatedAt).ToList()
                : collections.OrderByDescending(c => c.CreatedAt).ToList(),

            "name" => multiplier > 0
                ? collections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : collections.OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),

            "imagecount" => multiplier > 0
                ? collections.OrderBy(c => c.ImageCount).ToList()
                : collections.OrderByDescending(c => c.ImageCount).ToList(),

            "totalsize" => multiplier > 0
                ? collections.OrderBy(c => c.TotalSize).ToList()
                : collections.OrderByDescending(c => c.TotalSize).ToList(),

            _ => multiplier > 0
                ? collections.OrderBy(c => c.UpdatedAt).ToList()
                : collections.OrderByDescending(c => c.UpdatedAt).ToList()
        };
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
                    _logger.LogInformation("📊 Analyzed {Count}/{Total} collections...", 
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
            _logger.LogInformation("💾 Batch {Current}/{Total}: Memory before = {MemoryMB:F2} MB", 
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
            
            // Process collections sequentially (no batch due to async I/O operations)
            // Using batch with async file I/O causes deadlock
            foreach (var collection in collectionList)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("⚠️ Index rebuild cancelled");
                    return;
                }

                _logger.LogDebug("Building index for collection {id}", collection.Id.ToString());
                
                try
                {
                    // Process each collection with direct database operations (not batched)
                    // This avoids deadlock from async I/O inside batch operations
                    await AddToSortedSetsAsync(_db, collection);
                
                // Add to hash (with optional thumbnail caching)
                if (!options.SkipThumbnailCaching)
                {
                        await AddToHashAsync(_db, collection);
                }
                else
                {
                    // Skip thumbnail loading for faster rebuild
                        await AddToHashWithoutThumbnailAsync(_db, collection);
                }
                
                    // Update state tracking
                    await UpdateCollectionIndexStateAsync(_db, collection);
                
                processedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to index collection {CollectionId}, skipping", collection.Id);
                    // Continue with next collection
                }
            }
            
            // Memory monitoring after batch
            var memoryAfter = GC.GetTotalMemory(false);
            var memoryDelta = memoryAfter - memoryBefore;
            var batchDuration = DateTime.UtcNow - batchStartTime;
            
            _logger.LogInformation("✅ Batch {Current}/{Total} complete: {Count} collections in {Duration}ms, Memory delta = {DeltaMB:+0.00;-0.00} MB (now {CurrentMB:F2} MB)", 
                currentBatch, totalBatches, collectionList.Count, batchDuration.TotalMilliseconds,
                memoryDelta / 1024.0 / 1024.0, memoryAfter / 1024.0 / 1024.0);
            
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

    #region Archive Entry Path Fix

    /// <summary>
    /// Extract dimensions for images and update collection.
    /// This fixes both path issues and missing width/height values.
    /// </summary>
    private async Task AtomicUpdateImageArchiveEntriesAsync(ObjectId collectionId, Collection collection, string? fixMode = null)
    {
        int dimensionsFilled = 0;
        bool shouldFixDimensions = string.IsNullOrEmpty(fixMode) || fixMode == "All" || fixMode == "DimensionsOnly";
        
        // ✅ Extract dimensions using same logic as CollectionScanConsumer
        if (shouldFixDimensions)
        {
            for (int i = 0; i < collection.Images.Count; i++)
            {
                var image = collection.Images[i];
                
                // Only process images with missing dimensions (and not __MACOSX)
                if ((image.Width == 0 || image.Height == 0) && 
                    image.ArchiveEntry != null && 
                    !image.RelativePath.Contains("__MACOSX"))
                {
                    try
                    {
                        // Use same logic as CollectionScanConsumer - handles both regular files and archives
                        var (width, height) = await ExtractImageDimensionsAsync(image.ArchiveEntry);
                        if (width > 0 && height > 0)
                        {
                            // Update dimensions in memory before saving
                            image.UpdateMetadata(width, height, image.FileSize);
                            dimensionsFilled++;
                            _logger.LogDebug("📊 Extracted dimensions for {Entry}: {Width}x{Height}", 
                                image.ArchiveEntry.EntryName, width, height);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "⚠️ Failed to extract dimensions for {Entry}", image.ArchiveEntry?.EntryName);
                    }
                }
            }
        }
        
        // Update collection with modified images (includes both archive entry fixes and dimension extraction)
        await _collectionRepository.UpdateAsync(collection);
        
        if (shouldFixDimensions)
        {
            _logger.LogInformation("✅ Updated collection with {Count} images, filled {DimCount} missing dimensions", 
                collection.Images.Count, dimensionsFilled);
        }
        else
        {
            _logger.LogInformation("✅ Updated collection with {Count} images (paths only, skipped dimensions)", 
                collection.Images.Count);
        }
        
        // Invalidate Redis cache for this collection
        await _db.KeyDeleteAsync($"collection:{collectionId}");
    }

    /// <summary>
    /// Extract image dimensions (handles BOTH regular files and archive entries).
    /// Uses IImageProcessingService which now properly handles archive extraction.
    /// </summary>
    private async Task<(int width, int height)> ExtractImageDimensionsAsync(ArchiveEntryInfo archiveEntry)
    {
        try
        {
            // IImageProcessingService now handles BOTH regular files and archive entries
            var dimensions = await _imageProcessingService.GetImageDimensionsAsync(archiveEntry);
            if (dimensions != null && dimensions.Width > 0 && dimensions.Height > 0)
            {
                return (dimensions.Width, dimensions.Height);
            }
            
            return (0, 0); // Default to 0 if extraction fails
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Failed to extract dimensions for {Path}#{Entry}", 
                archiveEntry.ArchivePath, archiveEntry.EntryName);
            return (0, 0);
        }
    }

    /// <summary>
    /// Fix archive entry paths for collections with missing folder structure.
    /// This repairs the bug where entryName/entryPath don't include folders inside archives.
    /// </summary>
    public async Task<ArchiveEntryFixResult> FixArchiveEntryPathsAsync(
        bool dryRun = true,
        int? limit = null,
        string? collectionId = null,
        string? fixMode = null, // "All", "DimensionsOnly", "PathsOnly"
        bool onlyCorrupted = false, // If true, only process collections with dimension issues
        CancellationToken cancellationToken = default)
    {
        var result = new ArchiveEntryFixResult
        {
            DryRun = dryRun,
            StartedAt = DateTime.UtcNow
        };

        try
        {
            // 🎯 SINGLE COLLECTION MODE (for debugging)
            if (!string.IsNullOrEmpty(collectionId))
            {
                _logger.LogInformation("🔧 Starting archive entry fix for SINGLE collection (DryRun={DryRun}, CollectionId={CollectionId})", 
                    dryRun, collectionId);

                var collection = await _collectionRepository.GetByIdAsync(new MongoDB.Bson.ObjectId(collectionId));
                
                if (collection == null)
                {
                    _logger.LogWarning("⚠️ Collection {CollectionId} not found", collectionId);
                    result.ErrorMessages.Add($"Collection {collectionId} not found");
                    result.CompletedAt = DateTime.UtcNow;
                    result.Duration = result.CompletedAt - result.StartedAt;
                    return result;
                }

                _logger.LogInformation("📊 Processing single collection: {Name} (Type={Type})",
                    collection.Name, collection.Type);

                var fixedCount = await FixSingleCollectionArchiveEntriesAsync(collection, dryRun, fixMode, cancellationToken);
                
                if (fixedCount > 0)
                {
                    result.CollectionsWithIssues = 1;
                    result.ImagesFixed = fixedCount;
                    result.FixedCollectionIds.Add(collection.Id.ToString());
                    
                    _logger.LogInformation("✅ Fixed {Count} images in collection {Name}",
                        fixedCount, collection.Name);
                }
                else
                {
                    _logger.LogInformation("✅ No issues found in collection {Name}", collection.Name);
                }

                result.TotalCollectionsScanned = 1;
                result.CompletedAt = DateTime.UtcNow;
                result.Duration = result.CompletedAt - result.StartedAt;

                return result;
            }

            // 📊 NORMAL MODE: Process multiple collections
            _logger.LogInformation("🔧 Starting archive entry fix for ALL collections (DryRun={DryRun}, Limit={Limit}, FixMode={FixMode}, OnlyCorrupted={OnlyCorrupted})", 
                dryRun, limit, fixMode ?? "All", onlyCorrupted);

            // ✅ Build filter based on options
            var filterBuilder = MongoDB.Driver.Builders<Collection>.Filter;
            var filter = filterBuilder.Eq(c => c.IsDeleted, false);

            // 🎯 If onlyCorrupted=true, only select collections with dimension issues
            MongoDB.Driver.FilterDefinition<Collection> dimensionFilter = null;
            if (onlyCorrupted)
            {
                dimensionFilter = filterBuilder.ElemMatch(c => c.Images, img =>
                    (img.Width == 0 || img.Height == 0) &&
                    !img.RelativePath.Contains("__MACOSX")
                );
                filter = filterBuilder.And(filter, dimensionFilter);
                
                _logger.LogInformation("🎯 Filtering for collections with dimension issues only (excludes __MACOSX)");
            }

            var totalCollections = await _collectionRepository.CountAsync(filter);
            var collectionsToProcess = limit.HasValue ? Math.Min((int)totalCollections, limit.Value) : (int)totalCollections;

            _logger.LogInformation("📊 Found {Total} collections matching filter, will process {Process}",
                totalCollections, collectionsToProcess);
            
            // 🔍 DEBUG: Check if specific test collection matches filter
            //if (onlyCorrupted && dimensionFilter != null)
            //{
            //    try
            //    {
            //        var testCollectionId = new MongoDB.Bson.ObjectId("68f2a388ff19d7b375b40da9");
            //        var testFilter = filterBuilder.And(
            //            filterBuilder.Eq(c => c.Id, testCollectionId),
            //            dimensionFilter
            //        );
            //        var testCount = await _collectionRepository.CountAsync(testFilter);
            //        _logger.LogInformation("🔍 DEBUG: Test collection 68f2a388ff19d7b375b40da9 matches dimension filter: {Matches}", testCount > 0);
            //    }
            //    catch (Exception ex)
            //    {
            //        _logger.LogDebug(ex, "Debug test collection check failed");
            //    }
            //}

            // Process in batches
            const int BATCH_SIZE = 50;
            var processed = 0;

            for (var skip = 0; skip < collectionsToProcess && !cancellationToken.IsCancellationRequested; skip += BATCH_SIZE)
            {
                var batchSize = Math.Min(BATCH_SIZE, collectionsToProcess - skip);
                var collections = await _collectionRepository.FindAsync(
                    filter,
                    MongoDB.Driver.Builders<Collection>.Sort.Ascending(c => c.Id),
                    batchSize,
                    skip
                );

                foreach (var collection in collections)
                {
                    try
                    {
                        var fixedCount = await FixSingleCollectionArchiveEntriesAsync(collection, dryRun, fixMode, cancellationToken);
                        
                        if (fixedCount > 0)
                        {
                            result.CollectionsWithIssues++;
                            result.ImagesFixed += fixedCount;
                            result.FixedCollectionIds.Add(collection.Id.ToString());
                            
                            _logger.LogInformation("✅ Fixed {Count} images in collection {Name} ({Id})",
                                fixedCount, collection.Name, collection.Id);
                        }

                        processed++;
                        result.TotalCollectionsScanned++;

                        if (processed % 10 == 0)
                        {
                            _logger.LogInformation("📊 Progress: {Processed}/{Total} collections scanned, {Issues} with issues, {Fixed} images fixed",
                                processed, collectionsToProcess, result.CollectionsWithIssues, result.ImagesFixed);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fix collection {CollectionId}", collection.Id);
                        result.ErrorMessages.Add($"Collection {collection.Id}: {ex.Message}");
                    }
                }
            }

            result.CompletedAt = DateTime.UtcNow;
            result.Duration = result.CompletedAt - result.StartedAt;

            _logger.LogInformation("✅ Archive entry fix complete (folders + archives): Scanned={Scanned}, WithIssues={Issues}, ImagesFixed={Fixed}, Duration={Duration}",
                result.TotalCollectionsScanned, result.CollectionsWithIssues, result.ImagesFixed, result.Duration);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to fix archive entries");
            result.CompletedAt = DateTime.UtcNow;
            result.Duration = result.CompletedAt - result.StartedAt;
            result.ErrorMessages.Add($"Fatal error: {ex.Message}");
            return result;
        }
    }

    /// <summary>
    /// Fix archive entry paths for a single collection by opening the archive/folder and matching entries
    /// </summary>
    private async Task<int> FixSingleCollectionArchiveEntriesAsync(
        Collection collection,
        bool dryRun,
        string? fixMode,
        CancellationToken cancellationToken)
    {
        if (collection.Images == null || collection.Images.Count == 0)
            return 0;

        // 🎯 DIMENSIONS ONLY MODE: Skip path checking, only fix dimensions
        if (fixMode == "DimensionsOnly")
        {
            // Count images with dimension issues (excluding __MACOSX)
            // ✅ FIX: Process BOTH regular files and archive entries
            var imagesNeedingDimensions = collection.Images.Count(img =>
                (img.Width == 0 || img.Height == 0) &&
                img.ArchiveEntry != null &&
                !img.RelativePath.Contains("__MACOSX")
            );

            if (imagesNeedingDimensions == 0)
            {
                _logger.LogDebug("✅ Collection {Name} has no dimension issues", collection.Name);
                return 0;
            }

            _logger.LogInformation("📊 Collection {Name} has {Count} images needing dimension extraction (regular files + archive entries)", 
                collection.Name, imagesNeedingDimensions);

            if (!dryRun)
            {
                await AtomicUpdateImageArchiveEntriesAsync(collection.Id, collection, fixMode);
                _logger.LogInformation("💾 Updated collection {Name} with dimensions", collection.Name);
            }

            return imagesNeedingDimensions;
        }

        // Check if collection path exists (only needed for path fixing modes)
        if (!File.Exists(collection.Path) && !Directory.Exists(collection.Path))
        {
            _logger.LogWarning("⚠️ Collection path not found: {Path}", collection.Path);
            return 0;
        }

        // Dispatch to folder or archive handler for path fixing
        if (collection.Type == CollectionType.Folder)
        {
            return await FixFolderCollectionArchiveEntriesAsync(collection, dryRun, fixMode, cancellationToken);
        }
        else
        {
            return await FixArchiveCollectionArchiveEntriesAsync(collection, dryRun, fixMode, cancellationToken);
        }
    }

    /// <summary>
    /// Fix ArchiveEntry for folder collections by scanning the file system
    /// </summary>
    private async Task<int> FixFolderCollectionArchiveEntriesAsync(
        Collection collection,
        bool dryRun,
        string? fixMode,
        CancellationToken cancellationToken)
    {
        var fixedCount = 0;
        var needsUpdate = false;

        try
        {
            // For folder collections, build a lookup of existing files
            // Key = relative path (with subfolders), Value = full absolute path
            var filesByRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Fallback: Key = filename only, Value = relative path (for first match)
            var filesByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            if (Directory.Exists(collection.Path))
            {
                var files = Directory.GetFiles(collection.Path, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var filename = Path.GetFileName(file);
                    var relativePath = Path.GetRelativePath(collection.Path, file);
                    
                    // Store by relative path (unique key)
                    filesByRelativePath[relativePath] = file;
                    
                    // Store by filename for fallback (first match only)
                    if (!filesByName.ContainsKey(filename))
                    {
                        filesByName[filename] = relativePath;
                    }
                }
            }

            // Check each image
            foreach (var image in collection.Images)
            {
                var filename = image.Filename;
                var currentRelativePath = image.RelativePath;
                var hasArchiveEntry = image.ArchiveEntry != null;

                // Find the correct relative path
                string correctRelativePath;
                string correctFullPath;
                
                // METHOD 1: Try to find by current RelativePath (most accurate)
                if (!string.IsNullOrEmpty(currentRelativePath) && filesByRelativePath.ContainsKey(currentRelativePath))
                {
                    correctRelativePath = currentRelativePath;
                    correctFullPath = filesByRelativePath[currentRelativePath];
                }
                // METHOD 2: Fallback to filename-only matching (for legacy data)
                else if (filesByName.ContainsKey(filename))
                {
                    correctRelativePath = filesByName[filename];
                    correctFullPath = filesByRelativePath[correctRelativePath];
                    
                    _logger.LogDebug("📂 Using filename fallback for {Filename}: '{Current}' → '{Correct}'",
                        filename, currentRelativePath, correctRelativePath);
                }
                else
                {
                    // File doesn't exist on disk anymore - skip
                    _logger.LogWarning("⚠️ File not found on disk: {Filename} (RelativePath: {RelPath}) in {Collection}",
                        filename, currentRelativePath, collection.Name);
                    continue;
                }

                // Ensure correctFullPath is absolute
                if (!Path.IsPathRooted(correctFullPath))
                {
                    correctFullPath = Path.Combine(collection.Path, correctRelativePath);
                }

                // DETECTION: Check if ArchiveEntry is null OR has wrong data
                var needsFix = false;
                string issue = "";

                if (!hasArchiveEntry)
                {
                    needsFix = true;
                    issue = "ArchiveEntry is null";
                }
                else if (image.ArchiveEntry!.EntryName != correctRelativePath)
                {
                    needsFix = true;
                    issue = $"EntryName mismatch: '{image.ArchiveEntry.EntryName}' != '{correctRelativePath}'";
                }
                else if (image.ArchiveEntry.EntryPath != correctFullPath)
                {
                    needsFix = true;
                    issue = $"EntryPath mismatch: '{image.ArchiveEntry.EntryPath}' != '{correctFullPath}'";
                }
                else if (image.RelativePath != correctRelativePath)
                {
                    needsFix = true;
                    issue = $"RelativePath mismatch: '{image.RelativePath}' != '{correctRelativePath}'";
                }

                if (needsFix)
                {
                    _logger.LogDebug("📂 Folder image needs fix: {Filename} in {Collection} - {Issue}",
                        filename, collection.Name, issue);

                    if (!dryRun)
                    {
                        // Create or update ArchiveEntry for folder collection
                        var newArchiveEntry = new ArchiveEntryInfo
                        {
                            ArchivePath = collection.Path,         // Collection root path
                            EntryName = correctRelativePath,       // Relative path from collection root
                            EntryPath = correctFullPath,           // Full absolute path
                            FileType = ImageFileType.RegularFile,
                            CompressedSize = 0,
                            UncompressedSize = 0
                        };
                        
                        // Set IsDirectory through reflection or directly (it's deprecated but still settable)
#pragma warning disable CS0618 // IsDirectory is obsolete but we need to set it
                        newArchiveEntry.IsDirectory = true;  // Regular file (not in archive)
#pragma warning restore CS0618
                        
                        // Use SetArchiveEntry to update (works for both null and non-null cases)
                        image.SetArchiveEntry(newArchiveEntry, correctRelativePath);
                        needsUpdate = true;
                    }

                    fixedCount++;
                }
            }

            // ✅ FIX: Extract dimensions and update collection
            if (needsUpdate && !dryRun)
            {
                await AtomicUpdateImageArchiveEntriesAsync(collection.Id, collection, fixMode);
                _logger.LogInformation("💾 Updated folder collection {Id} with {Count} fixed entries",
                    collection.Id, fixedCount);
            }

            return fixedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fix folder collection {CollectionId}", collection.Id);
            throw;
        }
    }

    /// <summary>
    /// Fix ArchiveEntry for archive collections by opening the archive
    /// </summary>
    private async Task<int> FixArchiveCollectionArchiveEntriesAsync(
        Collection collection,
        bool dryRun,
        string? fixMode,
        CancellationToken cancellationToken)
    {
        var fixedCount = 0;
        var needsUpdate = false;

        try
        {
            // Open archive and build entry lookup
            using var archive = SharpCompress.Archives.ArchiveFactory.Open(collection.Path);
            
            // Two lookups: by full path (primary) and by filename (fallback)
            var entriesByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var entriesByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Build lookups from archive entries
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                var filename = Path.GetFileName(entry.Key);
                var entryPath = entry.Key;  // Full path inside archive (includes folders)
                
                // Store by full path (unique, includes folder structure)
                entriesByPath[entryPath] = entryPath;
                
                // Store by filename for fallback (first match only)
                if (!entriesByName.ContainsKey(filename))
                {
                    entriesByName[filename] = entryPath;
                }
            }

            _logger.LogDebug("📊 Archive {Archive} contains {Total} entries, {Unique} unique filenames",
                Path.GetFileName(collection.Path), entriesByPath.Count, entriesByName.Count);

            // Check each image for incorrect paths or null ArchiveEntry
            foreach (var image in collection.Images)
            {
                var filename = image.Filename;
                var currentRelativePath = image.RelativePath;
                var hasArchiveEntry = image.ArchiveEntry != null;
                var currentEntryName = hasArchiveEntry ? image.ArchiveEntry!.EntryName : null;

                // Find correct path in archive
                string correctPath;
                
                // METHOD 1: Try exact match by current EntryName (most accurate)
                if (!string.IsNullOrEmpty(currentEntryName) && entriesByPath.ContainsKey(currentEntryName))
                {
                    correctPath = currentEntryName;  // Already correct
                }
                // METHOD 2: Try exact match by RelativePath
                else if (!string.IsNullOrEmpty(currentRelativePath) && entriesByPath.ContainsKey(currentRelativePath))
                {
                    correctPath = currentRelativePath;
                }
                // METHOD 3: Fallback to filename-only matching (for corrupted data)
                else if (entriesByName.ContainsKey(filename))
                {
                    correctPath = entriesByName[filename];
                    
                    _logger.LogDebug("🗜️ Using filename fallback for {Filename}: EntryName='{Current}' → '{Correct}'",
                        filename, currentEntryName ?? "null", correctPath);
                }
                else
                {
                    // File doesn't exist in archive
                    _logger.LogWarning("⚠️ Image {ImageId} filename '{Filename}' not found in archive {Archive}", 
                        image.Id, filename, Path.GetFileName(collection.Path));
                    continue;
                }

                // DETECTION: Check if ArchiveEntry is null OR has corrupted data
                var needsFix = false;
                string issue = "";

                if (!hasArchiveEntry)
                {
                    needsFix = true;
                    issue = "ArchiveEntry is null (legacy data)";
                }
                else
                {
                    // Check all three fields for corruption
                    // All should match the correct path from archive
                    if (correctPath != currentEntryName)
                    {
                        needsFix = true;
                        issue = $"EntryName mismatch: '{currentEntryName}' != '{correctPath}'";
                    }
                    else if (correctPath != currentRelativePath)
                    {
                        needsFix = true;
                        issue = $"RelativePath mismatch: '{currentRelativePath}' != '{correctPath}'";
                    }
                    else if (image.ArchiveEntry.EntryPath != correctPath)
                    {
                        needsFix = true;
                        issue = $"EntryPath mismatch: '{image.ArchiveEntry.EntryPath}' != '{correctPath}'";
                    }
                }

                if (needsFix)
                {
                    _logger.LogDebug("🗜️ Archive image needs fix: {Filename} in {Collection} - {Issue}",
                        filename, collection.Name, issue);

                    if (!dryRun)
                    {
                        if (!hasArchiveEntry)
                        {
                            // ✅ CREATE new ArchiveEntry for legacy data
                            var newArchiveEntry = ArchiveEntryInfo.ForArchiveEntry(
                                collection.Path,
                                correctPath,  // EntryName (path inside archive)
                                correctPath,  // EntryPath (same as EntryName for archives)
                                0,            // CompressedSize (unknown)
                                0             // UncompressedSize (unknown)
                            );
                            image.SetArchiveEntry(newArchiveEntry, correctPath);
                        }
                        else
                        {
                            // ✅ UPDATE existing ArchiveEntry
                            image.UpdateArchiveEntryPath(correctPath);
                        }
                        
                        needsUpdate = true;
                    }

                    fixedCount++;
                }
            }

            // ✅ FIX: Extract dimensions and update collection
            if (needsUpdate && !dryRun)
            {
                await AtomicUpdateImageArchiveEntriesAsync(collection.Id, collection, fixMode);
                _logger.LogInformation("💾 Updated archive collection {Id} with {Count} fixed entries",
                    collection.Id, fixedCount);
            }

            return fixedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fix archive collection {CollectionId}", collection.Id);
            throw;
        }
    }

    #endregion
}


