using MongoDB.Driver;
using MongoDB.Bson;
using ImageViewer.Domain.Entities;
using ImageViewer.Domain.Interfaces;

namespace ImageViewer.Infrastructure.Data;

/// <summary>
/// MongoDB implementation of cache folder repository
/// </summary>
public class MongoCacheFolderRepository : MongoRepository<CacheFolder>, ICacheFolderRepository
{
    public MongoCacheFolderRepository(IMongoDatabase database) : base(database, "cache_folders")
    {
    }

    /// <summary>
    /// Get cache folder by path
    /// </summary>
    public async Task<CacheFolder?> GetByPathAsync(string path)
    {
        var filter = Builders<CacheFolder>.Filter.Eq(x => x.Path, path);
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    /// <summary>
    /// Get active cache folders ordered by priority
    /// </summary>
    public async Task<IEnumerable<CacheFolder>> GetActiveOrderedByPriorityAsync()
    {
        var filter = Builders<CacheFolder>.Filter.Eq(x => x.IsActive, true);
        var sort = Builders<CacheFolder>.Sort.Ascending(x => x.Priority);
        return await _collection.Find(filter).Sort(sort).ToListAsync();
    }

    /// <summary>
    /// Get cache folders by priority range
    /// </summary>
    public async Task<IEnumerable<CacheFolder>> GetByPriorityRangeAsync(int minPriority, int maxPriority)
    {
        var filter = Builders<CacheFolder>.Filter.And(
            Builders<CacheFolder>.Filter.Gte(x => x.Priority, minPriority),
            Builders<CacheFolder>.Filter.Lte(x => x.Priority, maxPriority)
        );
        var sort = Builders<CacheFolder>.Sort.Ascending(x => x.Priority);
        return await _collection.Find(filter).Sort(sort).ToListAsync();
    }

    /// <summary>
    /// Atomically increment cache folder size using MongoDB $inc operator
    /// Thread-safe for concurrent operations - prevents race conditions
    /// Pattern copied from BackgroundJob atomic updates
    /// </summary>
    public async Task IncrementSizeAsync(ObjectId folderId, long sizeBytes)
    {
        var filter = Builders<CacheFolder>.Filter.Eq(x => x.Id, folderId);
        
        // SINGLE ATOMIC UPDATE: Only increment - don't try to do multiple updates
        var update = Builders<CacheFolder>.Update
            .Inc(x => x.CurrentSizeBytes, sizeBytes)
            .Set(x => x.UpdatedAt, DateTime.UtcNow);

        await _collection.UpdateOneAsync(filter, update);
    }

    /// <summary>
    /// Atomically decrement cache folder size using MongoDB $inc operator
    /// Thread-safe for concurrent operations - prevents race conditions
    /// Pattern copied from BackgroundJob atomic updates
    /// </summary>
    public async Task DecrementSizeAsync(ObjectId folderId, long sizeBytes)
    {
        var filter = Builders<CacheFolder>.Filter.And(
            Builders<CacheFolder>.Filter.Eq(x => x.Id, folderId),
            Builders<CacheFolder>.Filter.Gte(x => x.CurrentSizeBytes, sizeBytes) // Only decrement if we have enough
        );
        
        // SINGLE ATOMIC UPDATE: Only decrement - MongoDB handles the operation atomically
        var update = Builders<CacheFolder>.Update
            .Inc(x => x.CurrentSizeBytes, -sizeBytes)
            .Set(x => x.UpdatedAt, DateTime.UtcNow);

        var result = await _collection.UpdateOneAsync(filter, update);
        
        // If update didn't match (not enough bytes), just set to 0
        if (result.ModifiedCount == 0)
        {
            var resetFilter = Builders<CacheFolder>.Filter.Eq(x => x.Id, folderId);
            var resetUpdate = Builders<CacheFolder>.Update
                .Max(x => x.CurrentSizeBytes, 0L)
                .Set(x => x.UpdatedAt, DateTime.UtcNow);
            
            await _collection.UpdateOneAsync(resetFilter, resetUpdate);
        }
    }

    /// <summary>
    /// Atomically increment file count
    /// </summary>
    public async Task IncrementFileCountAsync(ObjectId folderId, int count = 1)
    {
        var filter = Builders<CacheFolder>.Filter.Eq(x => x.Id, folderId);
        var update = Builders<CacheFolder>.Update
            .Inc(x => x.TotalFiles, count)
            .Set(x => x.LastCacheGeneratedAt, DateTime.UtcNow)
            .Set(x => x.UpdatedAt, DateTime.UtcNow);

        await _collection.UpdateOneAsync(filter, update);
    }

    /// <summary>
    /// Atomically increment cache statistics (size and file count) in SINGLE transaction
    /// 在单个事务中原子增加缓存统计信息（大小和文件数） - Tăng thống kê bộ nhớ cache nguyên tử trong một giao dịch
    /// Thread-safe for concurrent bulk operations
    /// </summary>
    public async Task IncrementCacheStatisticsAsync(ObjectId folderId, long sizeBytes, int fileCount = 1)
    {
        var filter = Builders<CacheFolder>.Filter.Eq(x => x.Id, folderId);
        var update = Builders<CacheFolder>.Update
            .Inc(x => x.CurrentSizeBytes, sizeBytes)
            .Inc(x => x.TotalFiles, fileCount)
            .Set(x => x.LastCacheGeneratedAt, DateTime.UtcNow)
            .Set(x => x.UpdatedAt, DateTime.UtcNow);

        await _collection.UpdateOneAsync(filter, update);
    }

    /// <summary>
    /// Atomically decrement file count
    /// </summary>
    public async Task DecrementFileCountAsync(ObjectId folderId, int count = 1)
    {
        var filter = Builders<CacheFolder>.Filter.And(
            Builders<CacheFolder>.Filter.Eq(x => x.Id, folderId),
            Builders<CacheFolder>.Filter.Gte(x => x.TotalFiles, count)
        );
        
        var update = Builders<CacheFolder>.Update
            .Inc(x => x.TotalFiles, -count)
            .Set(x => x.UpdatedAt, DateTime.UtcNow);

        var result = await _collection.UpdateOneAsync(filter, update);
        
        // If update didn't match, just set to 0
        if (result.ModifiedCount == 0)
        {
            var resetFilter = Builders<CacheFolder>.Filter.Eq(x => x.Id, folderId);
            var resetUpdate = Builders<CacheFolder>.Update
                .Max(x => x.TotalFiles, 0)
                .Set(x => x.UpdatedAt, DateTime.UtcNow);
            
            await _collection.UpdateOneAsync(resetFilter, resetUpdate);
        }
    }

    /// <summary>
    /// Atomically add collection to cached collections and update count
    /// 原子地添加集合到缓存集合列表并更新计数 - Thêm bộ sưu tập vào danh sách bộ nhớ cache và cập nhật số đếm nguyên tử
    /// Uses MongoDB aggregation pipeline for ATOMIC count calculation - NO race condition!
    /// </summary>
    public async Task AddCachedCollectionAsync(ObjectId folderId, string collectionId)
    {
        var filter = Builders<CacheFolder>.Filter.Eq(x => x.Id, folderId);
        
        // Use aggregation pipeline update (MongoDB 4.2+) for atomic operations
        // $addToSet equivalent + $size for atomic count - all in ONE operation
        var pipelineStage = @"{
            $set: {
                cachedCollectionIds: { 
                    $cond: {
                        if: { $in: ['" + collectionId + @"', { $ifNull: ['$cachedCollectionIds', []] }] },
                        then: { $ifNull: ['$cachedCollectionIds', []] },
                        else: { $concatArrays: [{ $ifNull: ['$cachedCollectionIds', []] }, ['" + collectionId + @"']] }
                    }
                },
                totalCollections: {
                    $size: {
                        $cond: {
                            if: { $in: ['" + collectionId + @"', { $ifNull: ['$cachedCollectionIds', []] }] },
                            then: { $ifNull: ['$cachedCollectionIds', []] },
                            else: { $concatArrays: [{ $ifNull: ['$cachedCollectionIds', []] }, ['" + collectionId + @"']] }
                        }
                    }
                },
                updatedAt: '$$NOW'
            }
        }";

        var pipeline = new EmptyPipelineDefinition<CacheFolder>()
            .AppendStage<CacheFolder, CacheFolder, CacheFolder>(pipelineStage);

        await _collection.UpdateOneAsync(filter, pipeline);
    }

    /// <summary>
    /// Remove a collection from the cached collections list
    /// </summary>
    public async Task RemoveCachedCollectionAsync(ObjectId folderId, string collectionId)
    {
        var filter = Builders<CacheFolder>.Filter.Eq(x => x.Id, folderId);
        var update = Builders<CacheFolder>.Update
            .Pull(x => x.CachedCollectionIds, collectionId)
            .Set(x => x.UpdatedAt, DateTime.UtcNow);

        await _collection.UpdateOneAsync(filter, update);
        
        // Recalculate total collections
        var folder = await GetByIdAsync(folderId);
        if (folder != null)
        {
            var countUpdate = Builders<CacheFolder>.Update
                .Set(x => x.TotalCollections, folder.CachedCollectionIds.Count);
            await _collection.UpdateOneAsync(filter, countUpdate);
        }
    }

    /// <summary>
    /// Update last cache generated timestamp
    /// </summary>
    public async Task UpdateLastCacheGeneratedAsync(ObjectId folderId)
    {
        var filter = Builders<CacheFolder>.Filter.Eq(x => x.Id, folderId);
        var update = Builders<CacheFolder>.Update
            .Set(x => x.LastCacheGeneratedAt, DateTime.UtcNow)
            .Set(x => x.UpdatedAt, DateTime.UtcNow);

        await _collection.UpdateOneAsync(filter, update);
    }

    /// <summary>
    /// Update last cleanup timestamp
    /// </summary>
    public async Task UpdateLastCleanupAsync(ObjectId folderId)
    {
        var filter = Builders<CacheFolder>.Filter.Eq(x => x.Id, folderId);
        var update = Builders<CacheFolder>.Update
            .Set(x => x.LastCleanupAt, DateTime.UtcNow)
            .Set(x => x.UpdatedAt, DateTime.UtcNow);

        await _collection.UpdateOneAsync(filter, update);
    }
}
