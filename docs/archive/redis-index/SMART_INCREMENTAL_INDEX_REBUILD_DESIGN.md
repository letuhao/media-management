# Smart Incremental Index Rebuild - Design Document

## 🚨 Current Problem

**Issue**: The Redis index rebuild **ALWAYS clears everything** (line 108) and rebuilds from scratch, even for collections that haven't changed!

```csharp
// Current logic (Line 87-117)
await ClearIndexAsync(); // ❌ Clears EVERYTHING!

// Then rebuilds ALL collections
for (var skip = 0; skip < totalCount; skip += BATCH_SIZE) {
    // Rebuilds ALL collections including unchanged ones
}
```

**Impact**:
- ✅ Takes 30 minutes to rebuild index
- ❌ Wastes time rebuilding unchanged collections
- ❌ Rebuilds complete collections that already have cache/thumbnails
- ❌ No way to do incremental updates

---

## 🎯 Design Goals

1. **Smart Rebuild Options** - Let users choose rebuild strategy
2. **Change Detection** - Detect which collections need rebuilding
3. **State Tracking** - Track rebuild state per collection in Redis
4. **Incremental Updates** - Only rebuild changed/incomplete collections
5. **Performance** - Reduce 30 minutes to <5 minutes for most updates

---

## 📋 Proposed Rebuild Modes

### Mode 1: Full Rebuild (Current Behavior)
**Use Case**: First time, or after Redis data corruption

**Behavior**:
- Clear ALL Redis data
- Rebuild ALL collections
- Ignore existing state

**Speed**: 30 minutes (10,000 collections)

---

### Mode 2: Incremental (Smart Default)
**Use Case**: Regular updates, most common scenario

**Behavior**:
- Check each collection's state in Redis
- Only rebuild if:
  - Collection not in Redis
  - Collection `UpdatedAt` changed
  - Collection incomplete (missing thumbnails/cache)
- Skip unchanged, complete collections

**Speed**: ~5 minutes (only changed collections)

**Algorithm**:
```
For each collection in MongoDB:
  1. Check if exists in Redis
  2. If NOT exists → REBUILD
  3. If exists:
     a. Compare UpdatedAt timestamps
     b. If MongoDB.UpdatedAt > Redis.IndexedAt → REBUILD
     c. If completeness score < 100% → REBUILD
     d. Otherwise → SKIP
```

---

### Mode 3: Force Rebuild Changed Only
**Use Case**: After fixing bugs in index logic

**Behavior**:
- Don't clear Redis
- Rebuild collections where `UpdatedAt > IndexedAt`
- Keep unchanged collections as-is

**Speed**: ~10 minutes (recently updated collections)

---

### Mode 4: Repair Incomplete
**Use Case**: After cache/thumbnail generation failures

**Behavior**:
- Don't clear Redis
- Only rebuild collections with:
  - Missing thumbnails (ThumbnailCount < ImageCount)
  - Missing cache (CacheCount < ImageCount)
  - Completeness score < 100%

**Speed**: ~5 minutes (incomplete collections only)

---

### Mode 5: Force Rebuild All (Without Clear)
**Use Case**: Refresh all data but keep Redis structure

**Behavior**:
- Don't clear Redis (keeps stats)
- Rebuild ALL collections (overwrite existing)
- Useful for schema changes

**Speed**: ~30 minutes (all collections)

---

## 🏗️ Architecture Design

### 1. Rebuild State Tracking in Redis

**New Redis Key Pattern**:
```
collection_index:state:{collectionId}
```

**State Data Structure**:
```csharp
public class CollectionIndexState
{
    public string CollectionId { get; set; }
    public DateTime IndexedAt { get; set; }          // When indexed
    public DateTime CollectionUpdatedAt { get; set; } // Collection.UpdatedAt at index time
    public int ImageCount { get; set; }
    public int ThumbnailCount { get; set; }
    public int CacheCount { get; set; }
    public double CompletenessScore { get; set; }    // 0-100%
    public string IndexVersion { get; set; }         // "v1.0" for schema version
    public bool IsComplete { get; set; }             // Quick flag
}
```

**Completeness Score Calculation**:
```csharp
double CalculateCompletenessScore(Collection collection)
{
    if (collection.ImageCount == 0) return 100.0;
    
    var thumbnailRatio = (double)collection.ThumbnailCount / collection.ImageCount;
    var cacheRatio = (double)collection.CacheCount / collection.ImageCount;
    
    // Weight: 50% thumbnails, 50% cache
    return (thumbnailRatio * 50) + (cacheRatio * 50);
}
```

---

### 2. Change Detection Logic

**Compare Function**:
```csharp
public enum RebuildDecision
{
    Skip,           // No changes, complete
    Rebuild,        // Changed or incomplete
    ForceRebuild    // User requested force
}

private async Task<RebuildDecision> ShouldRebuildCollectionAsync(
    Collection collection, 
    RebuildMode mode)
{
    // Mode: Full → Always rebuild
    if (mode == RebuildMode.Full)
        return RebuildDecision.ForceRebuild;
    
    // Check if state exists in Redis
    var state = await GetCollectionIndexStateAsync(collection.Id);
    
    // Not in Redis → Rebuild
    if (state == null)
    {
        _logger.LogDebug("Collection {Id} not in index, will rebuild", collection.Id);
        return RebuildDecision.Rebuild;
    }
    
    // Mode: Repair Incomplete → Check completeness
    if (mode == RebuildMode.RepairIncomplete)
    {
        if (!state.IsComplete || state.CompletenessScore < 100.0)
        {
            _logger.LogDebug("Collection {Id} incomplete ({Score}%), will rebuild", 
                collection.Id, state.CompletenessScore);
            return RebuildDecision.Rebuild;
        }
        return RebuildDecision.Skip;
    }
    
    // Mode: Incremental → Check UpdatedAt + Completeness
    if (mode == RebuildMode.Incremental)
    {
        // Check if updated since last index
        if (collection.UpdatedAt > state.CollectionUpdatedAt)
        {
            _logger.LogDebug("Collection {Id} updated ({CollectionTime} > {IndexTime}), will rebuild",
                collection.Id, collection.UpdatedAt, state.CollectionUpdatedAt);
            return RebuildDecision.Rebuild;
        }
        
        // Check if incomplete
        if (!state.IsComplete || state.CompletenessScore < 100.0)
        {
            _logger.LogDebug("Collection {Id} incomplete ({Score}%), will rebuild", 
                collection.Id, state.CompletenessScore);
            return RebuildDecision.Rebuild;
        }
        
        // Unchanged and complete → Skip
        _logger.LogDebug("Collection {Id} unchanged and complete, skipping", collection.Id);
        return RebuildDecision.Skip;
    }
    
    // Mode: Changed Only → Only check UpdatedAt
    if (mode == RebuildMode.ChangedOnly)
    {
        if (collection.UpdatedAt > state.CollectionUpdatedAt)
            return RebuildDecision.Rebuild;
        return RebuildDecision.Skip;
    }
    
    // Default: Rebuild
    return RebuildDecision.Rebuild;
}
```

---

### 3. New Interface Method

**Update `ICollectionIndexService`**:
```csharp
public interface ICollectionIndexService
{
    // Existing methods...
    Task RebuildIndexAsync(CancellationToken cancellationToken = default);
    
    // NEW: Smart rebuild with options
    Task RebuildIndexAsync(
        RebuildMode mode = RebuildMode.Incremental, 
        RebuildOptions? options = null,
        CancellationToken cancellationToken = default);
    
    // NEW: Get rebuild statistics
    Task<RebuildStatistics> GetRebuildStatisticsAsync();
    
    // NEW: Get collection index state
    Task<CollectionIndexState?> GetCollectionIndexStateAsync(ObjectId collectionId);
}

public enum RebuildMode
{
    Full,              // Clear all, rebuild all
    Incremental,       // Smart: only changed/incomplete (DEFAULT)
    ChangedOnly,       // Only collections with UpdatedAt > IndexedAt
    RepairIncomplete,  // Only incomplete collections
    ForceRebuildAll    // Rebuild all without clearing
}

public class RebuildOptions
{
    public bool SkipThumbnailCaching { get; set; } = false;  // Skip base64 thumbnail caching
    public bool SkipDashboardStats { get; set; } = false;    // Skip dashboard stats rebuild
    public int? MinCompletenessScore { get; set; } = 100;    // Minimum completeness to skip
    public bool DryRun { get; set; } = false;                // Only report what would be rebuilt
}

public class RebuildStatistics
{
    public int TotalCollections { get; set; }
    public int SkippedCollections { get; set; }
    public int RebuiltCollections { get; set; }
    public int IncompleteCollections { get; set; }
    public int ChangedCollections { get; set; }
    public TimeSpan Duration { get; set; }
    public long MemoryPeakMB { get; set; }
    public RebuildMode Mode { get; set; }
}
```

---

### 4. Implementation Flow

#### **Phase 1: Determine What Needs Rebuilding**

```csharp
// NEW: Smart rebuild with mode selection
public async Task RebuildIndexAsync(
    RebuildMode mode = RebuildMode.Incremental,
    RebuildOptions? options = null,
    CancellationToken cancellationToken = default)
{
    options ??= new RebuildOptions();
    var stats = new RebuildStatistics { Mode = mode };
    var startTime = DateTime.UtcNow;
    
    _logger.LogInformation("🔄 Starting {Mode} index rebuild...", mode);
    
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
        return;
    }
    
    // Step 5: Rebuild only selected collections
    await RebuildSelectedCollectionsAsync(
        collectionsToRebuild, 
        options, 
        cancellationToken);
    
    // Step 6: Update statistics
    stats.Duration = DateTime.UtcNow - startTime;
    await StoreRebuildStatisticsAsync(stats);
    
    _logger.LogInformation("✅ Rebuild complete: {Rebuilt} rebuilt, {Skipped} skipped in {Duration}ms",
        stats.RebuiltCollections, stats.SkippedCollections, stats.Duration.TotalMilliseconds);
}
```

#### **Phase 2: Rebuild Selected Collections**

```csharp
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
        var batchIds = collectionIds.Skip(i).Take(BATCH_SIZE).ToList();
        var batchCollections = await _collectionRepository.FindByIdsAsync(batchIds);
        
        var batch = _db.CreateBatch();
        var tasks = new List<Task>();
        
        foreach (var collection in batchCollections)
        {
            // Add to sorted sets
            tasks.Add(AddToSortedSetsAsync(batch, collection));
            
            // Add to hash (with optional thumbnail caching)
            if (!options.SkipThumbnailCaching)
            {
                tasks.Add(AddToHashAsync(batch, collection));
            }
            else
            {
                tasks.Add(AddToHashWithoutThumbnailAsync(batch, collection));
            }
            
            // Update state
            tasks.Add(UpdateCollectionIndexStateAsync(batch, collection));
            
            processedCount++;
        }
        
        batch.Execute();
        await Task.WhenAll(tasks);
        
        // Memory cleanup
        tasks.Clear();
        batchCollections = null!;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        
        _logger.LogInformation("✅ Batch {Current}/{Total} complete: {Count} collections",
            (i / BATCH_SIZE) + 1, totalBatches, batchCollections.Count);
    }
}
```

#### **Phase 3: State Tracking**

```csharp
private async Task UpdateCollectionIndexStateAsync(
    IDatabaseAsync db, 
    Collection collection)
{
    var state = new CollectionIndexState
    {
        CollectionId = collection.Id.ToString(),
        IndexedAt = DateTime.UtcNow,
        CollectionUpdatedAt = collection.UpdatedAt,
        ImageCount = collection.Images?.Count ?? 0,
        ThumbnailCount = collection.Thumbnails?.Count ?? 0,
        CacheCount = collection.CacheImages?.Count ?? 0,
        IndexVersion = "v1.0"
    };
    
    // Calculate completeness
    state.CompletenessScore = CalculateCompletenessScore(collection);
    state.IsComplete = state.CompletenessScore >= 100.0;
    
    var key = GetStateKey(collection.Id.ToString());
    var json = JsonSerializer.Serialize(state);
    
    // Store with no expiration (persist state)
    await db.StringSetAsync(key, json);
}

private string GetStateKey(string collectionId)
{
    return $"collection_index:state:{collectionId}";
}

private async Task<CollectionIndexState?> GetCollectionIndexStateAsync(ObjectId collectionId)
{
    var key = GetStateKey(collectionId.ToString());
    var json = await _db.StringGetAsync(key);
    
    if (!json.HasValue)
        return null;
    
    try
    {
        return JsonSerializer.Deserialize<CollectionIndexState>(json.ToString());
    }
    catch
    {
        return null;
    }
}

private double CalculateCompletenessScore(Collection collection)
{
    var imageCount = collection.Images?.Count ?? 0;
    if (imageCount == 0) return 100.0;
    
    var thumbnailCount = collection.Thumbnails?.Count ?? 0;
    var cacheCount = collection.CacheImages?.Count ?? 0;
    
    var thumbnailRatio = Math.Min(1.0, (double)thumbnailCount / imageCount);
    var cacheRatio = Math.Min(1.0, (double)cacheCount / imageCount);
    
    // Weight: 50% thumbnails, 50% cache
    return (thumbnailRatio * 50) + (cacheRatio * 50);
}
```

---

## 📊 Expected Performance Improvements

### Scenario 1: Daily Updates (100 new/changed collections)

| Mode | Collections Processed | Time |
|------|----------------------|------|
| **Full** (current) | 10,000 | 30 min ❌ |
| **Incremental** (smart) | 100 | 2 min ✅ |

**Improvement**: 15x faster! 🚀

---

### Scenario 2: After Cache Generation Failures (500 incomplete)

| Mode | Collections Processed | Time |
|------|----------------------|------|
| **Full** (current) | 10,000 | 30 min ❌ |
| **Repair Incomplete** | 500 | 5 min ✅ |

**Improvement**: 6x faster! 🚀

---

### Scenario 3: First Time (All need rebuild)

| Mode | Collections Processed | Time |
|------|----------------------|------|
| **Full** | 10,000 | 30 min |
| **Incremental** (first time) | 10,000 | 30 min |

**Same speed** (as expected for first time)

---

## 🔧 API Changes

### New Controller Endpoint

```csharp
// POST /api/v1/admin/index/rebuild
[HttpPost("rebuild")]
public async Task<IActionResult> RebuildIndex([FromBody] RebuildRequest request)
{
    var options = new RebuildOptions
    {
        SkipThumbnailCaching = request.SkipThumbnailCaching,
        SkipDashboardStats = request.SkipDashboardStats,
        MinCompletenessScore = request.MinCompletenessScore,
        DryRun = request.DryRun
    };
    
    var stats = await _collectionIndexService.RebuildIndexAsync(
        request.Mode, 
        options, 
        HttpContext.RequestAborted);
    
    return Ok(stats);
}

public class RebuildRequest
{
    public RebuildMode Mode { get; set; } = RebuildMode.Incremental;
    public bool SkipThumbnailCaching { get; set; } = false;
    public bool SkipDashboardStats { get; set; } = false;
    public int? MinCompletenessScore { get; set; } = 100;
    public bool DryRun { get; set; } = false;
}
```

---

## 🎨 UI Design (Settings Page)

```tsx
<Card>
  <CardHeader>
    <h2>Redis Index Rebuild</h2>
  </CardHeader>
  <CardContent>
    <div className="space-y-4">
      {/* Mode Selection */}
      <div>
        <label>Rebuild Mode</label>
        <select value={rebuildMode} onChange={e => setRebuildMode(e.target.value)}>
          <option value="Incremental">Incremental (Smart - Default)</option>
          <option value="ChangedOnly">Changed Only</option>
          <option value="RepairIncomplete">Repair Incomplete</option>
          <option value="Full">Full Rebuild (Clear All)</option>
          <option value="ForceRebuildAll">Force Rebuild All (No Clear)</option>
        </select>
      </div>
      
      {/* Options */}
      <div>
        <label>
          <input type="checkbox" checked={skipThumbnails} onChange={...} />
          Skip Thumbnail Caching (faster but no thumbnails)
        </label>
      </div>
      
      <div>
        <label>
          <input type="checkbox" checked={dryRun} onChange={...} />
          Dry Run (only report what would be rebuilt)
        </label>
      </div>
      
      {/* Rebuild Button */}
      <Button onClick={handleRebuild} disabled={isRebuilding}>
        {isRebuilding ? 'Rebuilding...' : 'Start Rebuild'}
      </Button>
      
      {/* Statistics */}
      {stats && (
        <div className="bg-slate-800 p-4 rounded">
          <h3>Last Rebuild Statistics</h3>
          <p>Mode: {stats.mode}</p>
          <p>Total: {stats.totalCollections}</p>
          <p>Rebuilt: {stats.rebuiltCollections}</p>
          <p>Skipped: {stats.skippedCollections}</p>
          <p>Duration: {stats.duration}</p>
        </div>
      )}
    </div>
  </CardContent>
</Card>
```

---

## 🚀 Implementation Plan

### Phase 1: Core Infrastructure ✅
1. Add `CollectionIndexState` class
2. Add `RebuildMode` enum
3. Add `RebuildOptions` class
4. Add state tracking methods

### Phase 2: Change Detection ✅
1. Implement `ShouldRebuildCollectionAsync`
2. Implement `CalculateCompletenessScore`
3. Add logging for decisions

### Phase 3: Smart Rebuild Logic ✅
1. Update `RebuildIndexAsync` with mode parameter
2. Add analysis phase (determine what to rebuild)
3. Add selective rebuild phase

### Phase 4: API & UI 🔲
1. Add admin endpoint for rebuild
2. Add UI in settings page
3. Add progress tracking

### Phase 5: Testing 🔲
1. Test each mode
2. Test dry run
3. Test state persistence

---

## 📝 Summary

**Current Problem**: 30 minutes to rebuild 10,000 collections every time

**Solution**: Smart incremental rebuild with 5 modes:
1. **Incremental** (default) - Only changed/incomplete (~2-5 minutes)
2. **ChangedOnly** - Only updated collections (~5 minutes)
3. **RepairIncomplete** - Only incomplete collections (~5 minutes)
4. **Full** - Clear all, rebuild all (30 minutes)
5. **ForceRebuildAll** - Rebuild all without clearing (30 minutes)

**Key Features**:
- ✅ State tracking per collection in Redis
- ✅ Change detection via `UpdatedAt` comparison
- ✅ Completeness scoring (0-100%)
- ✅ Dry run mode to preview changes
- ✅ Memory-efficient batch processing
- ✅ Progress tracking and statistics

**Expected Improvement**: **15x faster** for daily updates! 🚀

---

## 🤔 Discussion Questions

1. **Should we default to Incremental mode for auto-rebuild on startup?**
   - Pro: Much faster for daily restarts
   - Con: Might miss some edge cases

2. **Should completeness score be weighted differently?**
   - Current: 50% thumbnails, 50% cache
   - Alternative: 70% cache, 30% thumbnails?

3. **Should we add a "verify" mode that checks Redis vs MongoDB consistency?**
   - Could help detect corruption

4. **Should we expose rebuild API to users or keep it admin-only?**
   - Security consideration

5. **Should we add automatic incremental rebuild on schedule (e.g., every hour)?**
   - Could keep index fresh without manual intervention

What do you think? Should we proceed with implementation?


