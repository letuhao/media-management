# Archive Entry Path Fix - Complete Plan

## Problem Summary

Archive files with folder structure inside have corrupted data in MongoDB.

### Example

**Actual Archive Structure:**
```
L:\EMedia\AI_202509\[Patreon] - Kikia_Ai_Art - Bay Nikke.zip
  └── [Patreon] - Kikia_Ai_Art - Bay Nikke/
        ├── 00024_1495625771.png
        └── 00025_1234567890.png
```

**SharpCompress `entry.Key`:**
```
"[Patreon] - Kikia_Ai_Art - Bay Nikke/00024_1495625771.png"
```

---

## Current (Wrong) Database Structure ❌

```json
{
  "filename": "00024_1495625771.png",
  "relativePath": "00024_1495625771.png",  ❌ Missing folder!
  "archiveEntry": {
    "archivePath": "L:\\EMedia\\AI_202509\\[Patreon]...zip",
    "entryName": "00024_1495625771.png",  ❌ Missing folder! Extraction FAILS!
    "entryPath": "00024_1495625771.png",  ❌ Missing folder!
  }
}
```

**Why It Breaks:**
```csharp
// Extraction code looks for exact match:
var entry = archive.Entries.FirstOrDefault(e => e.Key == archiveEntry.EntryName);
// e.Key = "folder/00024_1495625771.png"
// archiveEntry.EntryName = "00024_1495625771.png"
// NO MATCH! → Returns null → Cannot extract → Image broken! 💔
```

---

## Correct Database Structure ✅

```json
{
  "filename": "00024_1495625771.png",  ✅ Display name
  "relativePath": "[Patreon] - Kikia_Ai_Art - Bay Nikke/00024_1495625771.png",  ✅ Full path (matches entry.Key)
  "legacyRelativePath": "00024_1495625771.png",  ✅ Keep for legacy data
  "archiveEntry": {
    "archivePath": "L:\\EMedia\\AI_202509\\[Patreon]...zip",
    "entryName": "[Patreon] - Kikia_Ai_Art - Bay Nikke/00024_1495625771.png",  ✅ MUST match entry.Key!
    "entryPath": "[Patreon] - Kikia_Ai_Art - Bay Nikke/00024_1495625771.png",  ✅ Same (consistent)
    "isDirectory": false,
    "fileType": 2
  }
}
```

**Key Principle:** 
- `entryName` = `entryPath` = `relativePath` = **SharpCompress `entry.Key`** (full path for extraction)
- `filename` = **Display name only** (just the filename)

---

## Root Causes

### 1. Wrong Data Created During Scan
**Location:** `CollectionScanConsumer.cs:353-360`
```csharp
mediaFiles.Add(new MediaFileInfo {
    RelativePath = entry.Key,  // ✅ Correct (full path)
    FileName = Path.GetFileName(entry.Key),  // ✅ Correct (just filename)
});

// But then at line 169-174:
var archiveEntry = ArchiveEntryInfo.FromCollection(
    collection.Path,
    collection.Type,
    mediaFile.FileName,  // ❌ WRONG! Should use mediaFile.RelativePath!
    mediaFile.FileSize,
    mediaFile.RelativePath);  // ✅ Now fixed
```

### 2. Services Recreate ArchiveEntry Incorrectly
**Locations:** BulkOperationConsumer, BulkService, AnimatedCacheRepairService, etc.
```csharp
// ❌ WRONG: Recreates from Filename
ArchiveEntry = ArchiveEntryInfo.FromCollection(
    collection.Path,
    collection.Type,
    image.Filename,  // ❌ Just filename, loses folder!
    image.FileSize)

// ✅ CORRECT: Reuse existing or create from RelativePath
ArchiveEntry = image.ArchiveEntry ?? ArchiveEntryInfo.FromCollection(
    collection.Path,
    collection.Type,
    image.Filename,
    image.FileSize,
    image.RelativePath)  // ✅ Has full path
```

---

## Detection Methods for Corrupted Data ✅

All detection methods are implemented in `FixSingleCollectionArchiveEntriesAsync`:

### Method 1: EntryName Doesn't Match Archive Content
```csharp
// Open archive and check if filename exists
var entryLookup = BuildFilenameLookup(archive);
if (!entryLookup.ContainsKey(filename)) {
    // CORRUPTED: File not found in archive at all
}
```

### Method 2: EntryName != Actual Entry Path
```csharp
// Get correct path from archive
var correctPath = entryLookup[filename];  // From archive.Entry.Key
if (correctPath != image.ArchiveEntry.EntryName) {
    // CORRUPTED: EntryName doesn't match actual path in archive
}
```

### Method 3: RelativePath != EntryName (Inconsistency)
```csharp
if (image.RelativePath != image.ArchiveEntry.EntryName) {
    // CORRUPTED: Data inconsistency between fields
}
```

### Method 4: EntryPath != EntryName (Inconsistency)
```csharp
if (image.ArchiveEntry.EntryPath != image.ArchiveEntry.EntryName) {
    // CORRUPTED: Internal ArchiveEntry inconsistency
}
```

### Method 5: Extraction Failure with Fallback Success
```csharp
// In ArchiveFileHelper.cs - detects corruption at runtime:
var entry = archive.Entries.FirstOrDefault(e => e.Key == entryName);
if (entry == null) {
    // Try filename fallback
    entry = archive.Entries.FirstOrDefault(e => 
        Path.GetFileName(e.Key) == Path.GetFileName(entryName));
    if (entry != null) {
        // DETECTED: Corrupted data (fallback worked)
        Log.Warning("Corruption detected, used fallback");
    }
}
```

### Method 6: Images vs Thumbnails/Cache Mismatch
```csharp
// Collections with corruption often have:
collection.Images.Count > collection.Thumbnails.Count + collection.CacheImages.Count
// Because corrupted entries fail to generate thumbnails/caches
```

---

## Complete Fix Plan

### Step 1: Fix Code (Prevent Future Issues) ✅
- [x] Fix `FromCollection` to accept `relativePath` parameter
- [x] Fix `CollectionScanConsumer` to pass `relativePath`
- [ ] Fix all services that recreate ArchiveEntry (8+ locations)
- [ ] Fix extraction code to be more robust

### Step 2: Create Repair Tool ✅
- [x] Add `FixArchiveEntryPathsAsync` method
- [x] Add `UpdateArchiveEntryPath` to ImageEmbedded
- [x] Add admin API endpoint `/admin/fix-archive-entries`
- [ ] Add detection for corrupted data
- [ ] Add UI in Settings page

### Step 3: Run Repair
1. Test with dry run: `{ "dryRun": true, "limit": 100 }`
2. Fix all data: `{ "dryRun": false }`
3. Verify: Check random collections
4. Resume library scans: Will regenerate missing thumbnails/caches

---

## Implementation Status

### Completed ✅
1. Updated `ArchiveEntryInfo.FromCollection` to accept `relativePath`
2. Fixed `CollectionScanConsumer` scan path
3. Created `ImageEmbedded.UpdateArchiveEntryPath` method
4. Created `FixArchiveEntryPathsAsync` repair tool
5. Added admin API endpoint
6. Added frontend API method

### Remaining ❌
1. Fix all 8+ services that recreate ArchiveEntry incorrectly
2. Make extraction code more robust with fallback
3. Add detection methods to admin tool
4. Create UI in Settings page
5. Test and verify

---

## Next Steps

**User Decision Required:**
1. Should we keep `entryName` = `entryPath` (current design)?
2. Or separate them: `entryName` = filename, `entryPath` = full path?
3. Should we also update `Filename` to show folder structure, or keep it as just filename?

**Recommendation:** Keep current design, just populate it correctly:
- `filename` = filename only (for display)
- `relativePath` = full path (for uniqueness)
- `entryName` = full path (for extraction)
- `entryPath` = full path (for consistency)

