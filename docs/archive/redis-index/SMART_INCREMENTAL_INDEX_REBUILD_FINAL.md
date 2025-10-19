# Smart Incremental Index Rebuild - Final Design (Approved)

## 📋 User Decisions

### ✅ Default Mode: **Changed Only**
- Auto-rebuild on startup uses `ChangedOnly` mode
- Only rebuilds collections where `UpdatedAt > IndexedAt`
- Faster and safer than Incremental
- Won't accidentally rebuild incomplete collections

### ✅ Completeness Score: **Simplified**
- **ONLY track first thumbnail** (for collection card display)
- Remove cache from completeness calculation
- Simplified: `HasThumbnail` boolean flag instead of score
- Reason: Index only caches first thumbnail as base64, nothing about cache images

### ✅ Add Verify Mode
- Checks Redis vs MongoDB consistency
- Removes deleted collections from Redis
- Reports inconsistencies
- Useful for debugging and cleanup

### ✅ Manual Rebuild Only
- No auto-schedule
- User triggers rebuild when needed
- Keep it simple and predictable

### ✅ Admin-Only (System Settings)
- Already in System Settings screen
- Need to add rebuild options UI
- Admin-only feature

---

## 🏗️ Updated Architecture

### 1. Simplified State Tracking

**Updated State Structure**:
```csharp
public class CollectionIndexState
{
    public string CollectionId { get; set; }
    public DateTime IndexedAt { get; set; }           // When last indexed
    public DateTime CollectionUpdatedAt { get; set; }  // Collection.UpdatedAt at index time
    
    // Statistics (used by other screens, lightweight to include)
    public int ImageCount { get; set; }
    public int ThumbnailCount { get; set; }           // ✅ Keep for statistics display
    public int CacheCount { get; set; }               // ✅ Keep for statistics display
    
    // First thumbnail tracking (for collection card display)
    public bool HasFirstThumbnail { get; set; }       // Boolean: has first thumbnail cached
    public string? FirstThumbnailPath { get; set; }   // Path to verify file exists
    
    public string IndexVersion { get; set; }          // "v1.0" for schema versioning
    
    // ❌ REMOVED: CompletenessScore (not needed, too complex)
    // ✅ KEPT: Counts (used for statistics, no processing cost)
}
```

**Why Keep Counts?**
- ✅ Used for statistics display on other screens
- ✅ Lightweight - just integers, no processing required
- ✅ Already available in collection object
- ❌ Removed: CompletenessScore (complex calculation, not needed)

---

### 2. Updated Rebuild Modes

#### **Mode 1: ChangedOnly (DEFAULT for startup)** ⭐
```csharp
// Only rebuilds collections where UpdatedAt changed
if (collection.UpdatedAt > state.CollectionUpdatedAt)
    return REBUILD;
return SKIP;
```
**Speed**: ~5-10 minutes (recently updated collections)
**Use Case**: Auto-rebuild on startup, daily updates

#### **Mode 2: Verify (NEW - Consistency Check)** 🔍
```csharp
// Phase 1: Check MongoDB → Redis
For each collection in MongoDB:
    if NOT in Redis → ADD
    if UpdatedAt changed → UPDATE
    if thumbnail missing → UPDATE

// Phase 2: Check Redis → MongoDB (cleanup)
For each collection in Redis:
    if NOT in MongoDB → REMOVE (deleted collection)
    if IsDeleted=true → REMOVE

// Phase 3: Report inconsistencies
Report: {
    Added: 10,
    Updated: 50,
    Removed: 5,
    Inconsistent: 2
}
```
**Speed**: ~10 minutes (full scan + cleanup)
**Use Case**: After manual DB changes, suspected corruption

#### **Mode 3: Full (Clear All)** 🔄
```csharp
// Clear ALL Redis data, rebuild ALL collections
await ClearIndexAsync();
// Rebuild everything from scratch
```
**Speed**: 30 minutes
**Use Case**: First time, major corruption

#### **Mode 4: ForceRebuildAll (No Clear)** 🔨
```csharp
// Rebuild ALL collections without clearing Redis
// Useful for schema changes
```
**Speed**: 30 minutes
**Use Case**: Index schema version update

#### **❌ REMOVED: Incremental, RepairIncomplete**
- Reason: Too complex, ChangedOnly + Verify covers all needs

---

### 3. Change Detection (Simplified)

```csharp
private async Task<RebuildDecision> ShouldRebuildCollectionAsync(
    Collection collection, 
    RebuildMode mode)
{
    switch (mode)
    {
        case RebuildMode.Full:
        case RebuildMode.ForceRebuildAll:
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
            // Verify mode handles differently, always check
            return await VerifyCollectionAsync(collection);
        
        default:
            return RebuildDecision.Rebuild;
    }
}
```

---

### 4. Verify Mode Implementation

```csharp
public async Task<VerifyResult> VerifyIndexAsync(CancellationToken cancellationToken = default)
{
    var result = new VerifyResult();
    var startTime = DateTime.UtcNow;
    
    _logger.LogInformation("🔍 Starting index verification...");
    
    // Phase 1: Check MongoDB → Redis (find missing/outdated)
    _logger.LogInformation("📊 Phase 1: Checking MongoDB collections...");
    
    var totalCount = await _collectionRepository.CountAsync(
        MongoDB.Driver.Builders<Collection>.Filter.Eq(c => c.IsDeleted, false)
    );
    
    const int BATCH_SIZE = 100;
    var collectionsToAdd = new List<ObjectId>();
    var collectionsToUpdate = new List<ObjectId>();
    
    for (var skip = 0; skip < totalCount; skip += BATCH_SIZE)
    {
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
            else if (!state.HasFirstThumbnail && collection.Thumbnails?.Any() == true)
            {
                // Thumbnail was added after indexing
                collectionsToUpdate.Add(collection.Id);
                result.MissingThumbnails.Add(collection.Id.ToString());
            }
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
    
    var collectionsToRemove = new List<string>();
    
    foreach (var collectionId in redisCollectionIds)
    {
        var collection = await _collectionRepository.GetByIdAsync(
            MongoDB.Bson.ObjectId.Parse(collectionId));
        
        if (collection == null || collection.IsDeleted)
        {
            collectionsToRemove.Add(collectionId);
            result.OrphanedInRedis.Add(collectionId);
        }
    }
    
    result.ToRemove = collectionsToRemove.Count;
    
    _logger.LogInformation("✅ Phase 2 complete: {Remove} to remove (orphaned/deleted)",
        result.ToRemove);
    
    // Phase 3: Fix inconsistencies (if not dry run)
    if (!result.DryRun)
    {
        _logger.LogInformation("🔧 Phase 3: Fixing inconsistencies...");
        
        // Add missing collections
        if (collectionsToAdd.Any())
        {
            _logger.LogInformation("➕ Adding {Count} missing collections...", collectionsToAdd.Count);
            await RebuildSelectedCollectionsAsync(collectionsToAdd, new RebuildOptions(), cancellationToken);
        }
        
        // Update outdated collections
        if (collectionsToUpdate.Any())
        {
            _logger.LogInformation("🔄 Updating {Count} outdated collections...", collectionsToUpdate.Count);
            await RebuildSelectedCollectionsAsync(collectionsToUpdate, new RebuildOptions(), cancellationToken);
        }
        
        // Remove orphaned entries
        if (collectionsToRemove.Any())
        {
            _logger.LogInformation("🗑️ Removing {Count} orphaned entries...", collectionsToRemove.Count);
            foreach (var collectionId in collectionsToRemove)
            {
                await RemoveCollectionAsync(MongoDB.Bson.ObjectId.Parse(collectionId));
            }
        }
        
        _logger.LogInformation("✅ Phase 3 complete: Fixed all inconsistencies");
    }
    else
    {
        _logger.LogInformation("🔍 DRY RUN: Would fix {Total} inconsistencies",
            result.ToAdd + result.ToUpdate + result.ToRemove);
    }
    
    result.Duration = DateTime.UtcNow - startTime;
    result.IsConsistent = result.ToAdd == 0 && result.ToUpdate == 0 && result.ToRemove == 0;
    
    _logger.LogInformation("✅ Verification complete in {Duration}ms: {Status}",
        result.Duration.TotalMilliseconds,
        result.IsConsistent ? "CONSISTENT ✅" : "INCONSISTENT ⚠️");
    
    return result;
}

// Helper: Get all collection IDs in Redis index
private async Task<List<string>> GetAllIndexedCollectionIdsAsync()
{
    var server = _redis.GetServer(_redis.GetEndPoints().First());
    var collectionIds = new List<string>();
    
    // Scan for all state keys
    await foreach (var key in server.KeysAsync(pattern: "collection_index:state:*"))
    {
        var keyStr = key.ToString();
        var collectionId = keyStr.Replace("collection_index:state:", "");
        collectionIds.Add(collectionId);
    }
    
    return collectionIds;
}

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
```

---

### 5. Updated Interface

```csharp
public interface ICollectionIndexService
{
    // Existing methods...
    
    // NEW: Smart rebuild with mode
    Task<RebuildStatistics> RebuildIndexAsync(
        RebuildMode mode = RebuildMode.ChangedOnly,  // ✅ Default: ChangedOnly
        RebuildOptions? options = null,
        CancellationToken cancellationToken = default);
    
    // NEW: Verify consistency
    Task<VerifyResult> VerifyIndexAsync(
        bool dryRun = true,
        CancellationToken cancellationToken = default);
    
    // Existing backward-compatible method (redirects to ChangedOnly)
    Task RebuildIndexAsync(CancellationToken cancellationToken = default)
        => RebuildIndexAsync(RebuildMode.ChangedOnly, null, cancellationToken);
}

public enum RebuildMode
{
    ChangedOnly,      // ✅ DEFAULT: Only collections with UpdatedAt > IndexedAt
    Verify,           // ✅ NEW: Check consistency, fix issues
    Full,             // Clear all, rebuild all
    ForceRebuildAll   // Rebuild all without clearing
}

public class RebuildOptions
{
    public bool SkipThumbnailCaching { get; set; } = false;  // Skip base64 thumbnail
    public bool DryRun { get; set; } = false;                // Only report
}

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
```

---

## 🎨 UI Design (System Settings Screen)

### **Location**: `/settings` → System tab

```tsx
<Card>
  <CardHeader>
    <h2>Redis Index Management</h2>
    <p className="text-sm text-slate-400">
      Manage the Redis cache index for collection search and navigation
    </p>
  </CardHeader>
  
  <CardContent className="space-y-6">
    {/* Current Status */}
    <div className="bg-slate-800 p-4 rounded">
      <h3 className="font-semibold mb-2">Current Index Status</h3>
      <div className="grid grid-cols-2 gap-4 text-sm">
        <div>
          <span className="text-slate-400">Total Collections:</span>
          <span className="ml-2 font-mono">{stats?.totalCollections || '-'}</span>
        </div>
        <div>
          <span className="text-slate-400">Last Rebuild:</span>
          <span className="ml-2 font-mono">{stats?.lastRebuild || '-'}</span>
        </div>
        <div>
          <span className="text-slate-400">Index Version:</span>
          <span className="ml-2 font-mono">v1.0</span>
        </div>
        <div>
          <span className="text-slate-400">Status:</span>
          <span className={`ml-2 font-semibold ${isConsistent ? 'text-green-400' : 'text-yellow-400'}`}>
            {isConsistent ? '✅ Consistent' : '⚠️ Needs Verification'}
          </span>
        </div>
      </div>
    </div>
    
    {/* Rebuild Section */}
    <div>
      <h3 className="font-semibold mb-3">Rebuild Index</h3>
      
      <div className="space-y-3">
        {/* Mode Selection */}
        <div>
          <label className="block text-sm font-medium mb-2">Rebuild Mode</label>
          <select 
            value={rebuildMode} 
            onChange={e => setRebuildMode(e.target.value)}
            className="w-full bg-slate-800 border border-slate-700 rounded px-3 py-2"
          >
            <option value="ChangedOnly">Changed Only (Recommended) ⭐</option>
            <option value="Verify">Verify & Fix Consistency 🔍</option>
            <option value="Full">Full Rebuild (Clear All) 🔄</option>
            <option value="ForceRebuildAll">Force Rebuild All (No Clear) 🔨</option>
          </select>
          
          {/* Mode Description */}
          <p className="text-xs text-slate-400 mt-1">
            {getModeDescription(rebuildMode)}
          </p>
        </div>
        
        {/* Options */}
        <div className="space-y-2">
          <label className="flex items-center text-sm">
            <input 
              type="checkbox" 
              checked={skipThumbnails} 
              onChange={e => setSkipThumbnails(e.target.checked)}
              className="mr-2"
            />
            Skip thumbnail caching (faster, but collection cards won't show thumbnails)
          </label>
          
          <label className="flex items-center text-sm">
            <input 
              type="checkbox" 
              checked={dryRun} 
              onChange={e => setDryRun(e.target.checked)}
              className="mr-2"
            />
            Dry run (preview changes without applying)
          </label>
        </div>
        
        {/* Action Buttons */}
        <div className="flex gap-2">
          <Button 
            onClick={handleRebuild} 
            disabled={isRebuilding}
            variant="primary"
          >
            {isRebuilding ? (
              <>
                <Loader className="h-4 w-4 animate-spin mr-2" />
                Rebuilding...
              </>
            ) : (
              <>
                <RefreshCw className="h-4 w-4 mr-2" />
                Start Rebuild
              </>
            )}
          </Button>
          
          {rebuildMode === 'Verify' && (
            <Button 
              onClick={handleVerifyOnly} 
              disabled={isRebuilding}
              variant="secondary"
            >
              <Search className="h-4 w-4 mr-2" />
              Verify Only
            </Button>
          )}
        </div>
      </div>
    </div>
    
    {/* Last Rebuild Statistics */}
    {lastStats && (
      <div className="bg-slate-800 p-4 rounded">
        <h3 className="font-semibold mb-2">Last Rebuild Results</h3>
        <div className="space-y-2 text-sm">
          <div className="flex justify-between">
            <span className="text-slate-400">Mode:</span>
            <span className="font-mono">{lastStats.mode}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-slate-400">Duration:</span>
            <span className="font-mono">{formatDuration(lastStats.duration)}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-slate-400">Total Collections:</span>
            <span className="font-mono">{lastStats.totalCollections}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-slate-400">Rebuilt:</span>
            <span className="font-mono text-blue-400">{lastStats.rebuiltCollections}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-slate-400">Skipped:</span>
            <span className="font-mono text-green-400">{lastStats.skippedCollections}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-slate-400">Memory Peak:</span>
            <span className="font-mono">{lastStats.memoryPeakMB} MB</span>
          </div>
        </div>
      </div>
    )}
    
    {/* Verify Results (if applicable) */}
    {verifyResult && (
      <div className={`p-4 rounded border ${
        verifyResult.isConsistent 
          ? 'bg-green-900/20 border-green-700' 
          : 'bg-yellow-900/20 border-yellow-700'
      }`}>
        <h3 className="font-semibold mb-2">
          {verifyResult.isConsistent ? '✅ Index Consistent' : '⚠️ Inconsistencies Found'}
        </h3>
        <div className="space-y-2 text-sm">
          {verifyResult.toAdd > 0 && (
            <div>➕ Missing in Redis: <strong>{verifyResult.toAdd}</strong></div>
          )}
          {verifyResult.toUpdate > 0 && (
            <div>🔄 Outdated in Redis: <strong>{verifyResult.toUpdate}</strong></div>
          )}
          {verifyResult.toRemove > 0 && (
            <div>🗑️ Orphaned in Redis: <strong>{verifyResult.toRemove}</strong></div>
          )}
          {verifyResult.isConsistent && (
            <div className="text-green-400">All collections properly indexed</div>
          )}
        </div>
      </div>
    )}
  </CardContent>
</Card>

{/* Helper function for mode descriptions */}
function getModeDescription(mode: string) {
  switch (mode) {
    case 'ChangedOnly':
      return 'Only rebuilds collections that have been updated since last index. Fast and safe. (Recommended for regular use)';
    case 'Verify':
      return 'Checks consistency between MongoDB and Redis. Adds missing, updates outdated, removes deleted collections. Recommended after manual DB changes.';
    case 'Full':
      return 'Clears ALL Redis data and rebuilds from scratch. Use only if index is corrupted or for first-time setup. (~30 minutes)';
    case 'ForceRebuildAll':
      return 'Rebuilds all collections without clearing Redis structure. Use after index schema changes. (~30 minutes)';
    default:
      return '';
  }
}
```

---

## 📊 Performance Expectations

### Scenario 1: Daily Startup (ChangedOnly)
```
Collections in MongoDB: 10,000
Changed since last index: 50
Time: ~1-2 minutes ✅

Log:
🔄 Starting ChangedOnly index rebuild...
📊 Found 10,000 collections in MongoDB
🔍 Analyzing collections...
📊 Analysis: 50 to rebuild, 9,950 to skip
🔨 Rebuilding 50 collections in 1 batch...
✅ Rebuild complete: 50 rebuilt, 9,950 skipped in 90 seconds
```

### Scenario 2: After Manual DB Changes (Verify)
```
Collections in MongoDB: 10,000
Collections in Redis: 10,005
Missing in Redis: 10
Outdated in Redis: 50
Orphaned in Redis: 5 (deleted from DB)
Time: ~10 minutes ✅

Log:
🔍 Starting index verification...
📊 Phase 1: Checking MongoDB... 10 missing, 50 outdated
📊 Phase 2: Checking Redis... 5 orphaned
🔧 Phase 3: Fixing... ➕ 10 added, 🔄 50 updated, 🗑️ 5 removed
✅ Verification complete: CONSISTENT ✅
```

### Scenario 3: First Time (Full)
```
Collections in MongoDB: 10,000
Time: ~30 minutes

Log:
🔄 Starting Full index rebuild...
🧹 Clearing all Redis data...
🔨 Building Redis index for 10,000 collections in 100 batches...
✅ Rebuild complete: 10,000 rebuilt, 0 skipped in 30 minutes
```

---

## 🚀 Implementation Plan

### ✅ Phase 1: State Tracking (2-3 hours)
1. Add `CollectionIndexState` class
2. Add state get/set methods
3. Update `AddToHashAsync` to save state
4. Test state persistence

### ✅ Phase 2: Rebuild Modes (3-4 hours)
1. Add `RebuildMode` enum
2. Update `RebuildIndexAsync` signature
3. Implement `ChangedOnly` logic
4. Test with sample data

### ✅ Phase 3: Verify Mode (4-5 hours)
1. Implement `VerifyIndexAsync`
2. Add orphaned entry cleanup
3. Add `VerifyResult` class
4. Test verification logic

### ✅ Phase 4: API Integration (2-3 hours)
1. Add admin controller endpoint
2. Add request/response DTOs
3. Test API calls

### ✅ Phase 5: UI (3-4 hours)
1. Add rebuild section to System Settings
2. Add mode selection dropdown
3. Add statistics display
4. Add verify results display
5. Test UI interactions

**Total**: ~15-20 hours

---

## 📝 Summary

**User Decisions Implemented**:
1. ✅ **Default**: ChangedOnly (safer, faster)
2. ✅ **Simplified**: Only track first thumbnail (no cache)
3. ✅ **Added**: Verify mode for consistency checks
4. ✅ **Manual**: No auto-schedule
5. ✅ **Admin**: System Settings UI

**Modes**:
- **ChangedOnly** (default) - 1-2 min ⚡
- **Verify** - 10 min 🔍
- **Full** - 30 min 🔄
- **ForceRebuildAll** - 30 min 🔨

**Ready to implement?** 🚀


