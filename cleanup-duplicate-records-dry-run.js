// Cleanup Duplicate Thumbnail and Cache Records - DRY RUN VERSION
// This script handles the ACTUAL data structure with embedded arrays in collections
// Run this script using MongoDB shell: mongosh image_viewer cleanup-duplicate-records-dry-run.js
// 
// This is a DRY RUN - no changes will be made to the database

print("🧹 Cleaning up duplicate thumbnail and cache records...");
print("Database: image_viewer");
print("Collection: collections (with embedded thumbnails[] and cacheImages[] arrays)");
print("🔍 DRY RUN MODE - No changes will be made");
print("");

// Get collection count first
var totalCollections = db.collections.countDocuments({
    "thumbnails": { "$exists": true, "$ne": [] }
});
var collectionsWithDuplicates = 0;
var totalDuplicatesRemoved = 0;
var totalThumbnailDuplicates = 0;
var totalCacheDuplicates = 0;

print("📊 Found " + totalCollections + " collections with thumbnails to analyze");
print("🔄 Processing collections to find duplicates...");

// Process collections with thumbnails (same approach as working script)
var processedCollections = 0;
var collectionsCursor = db.collections.find({
    "thumbnails": { "$exists": true, "$ne": [] }
});

collectionsCursor.forEach(function(coll) {
    processedCollections++;
    print("📦 Processing collection " + processedCollections + "/" + totalCollections);
    
    var collectionId = coll._id;
    var collectionName = coll.name;
    var images = coll.images || [];
    var thumbnails = coll.thumbnails || [];
    var cacheImages = coll.cacheImages || [];
    
    var imageCount = images.length;
    var thumbnailCount = thumbnails.length;
    var cacheCount = cacheImages.length;
    
    // Find duplicate thumbnails (same imageId + width + height)
    var thumbnailDuplicates = [];
    var thumbnailGroups = {};
    
    for (var j = 0; j < thumbnails.length; j++) {
        var thumb = thumbnails[j];
        var key = thumb.imageId + "_" + thumb.width + "_" + thumb.height;
        if (!thumbnailGroups[key]) {
            thumbnailGroups[key] = [];
        }
        thumbnailGroups[key].push(thumb);
    }
    
    for (var key in thumbnailGroups) {
        if (thumbnailGroups[key].length > 1) {
            // Keep the first one, mark others as duplicates
            for (var k = 1; k < thumbnailGroups[key].length; k++) {
                thumbnailDuplicates.push(thumbnailGroups[key][k]);
            }
        }
    }
    
    // Find duplicate cache images (same imageId)
    var cacheDuplicates = [];
    var cacheGroups = {};
    
    for (var j = 0; j < cacheImages.length; j++) {
        var cache = cacheImages[j];
        var key = cache.imageId;
        if (!cacheGroups[key]) {
            cacheGroups[key] = [];
        }
        cacheGroups[key].push(cache);
    }
    
    for (var key in cacheGroups) {
        if (cacheGroups[key].length > 1) {
            // Keep the first one, mark others as duplicates
            for (var k = 1; k < cacheGroups[key].length; k++) {
                cacheDuplicates.push(cacheGroups[key][k]);
            }
        }
    }
    
    if (thumbnailDuplicates.length > 0 || cacheDuplicates.length > 0) {
        collectionsWithDuplicates++;
        print("🔍 Collection: " + collectionName + " (" + collectionId + ")");
        print("   Images: " + imageCount);
        print("   Thumbnails: " + thumbnailCount + " (Duplicates: " + thumbnailDuplicates.length + ")");
        print("   Cache: " + cacheCount + " (Duplicates: " + cacheDuplicates.length + ")");
        
        // SAFETY CHECK: Verify we're not removing too many records
        var expectedThumbnailCount = thumbnailCount - thumbnailDuplicates.length;
        var expectedCacheCount = cacheCount - cacheDuplicates.length;
        
        // SAFETY CHECK: Ensure we don't remove more than 80% of records (increased from 50%)
        var thumbnailRemovalRatio = thumbnailCount > 0 ? thumbnailDuplicates.length / thumbnailCount : 0;
        var cacheRemovalRatio = cacheCount > 0 ? cacheDuplicates.length / cacheCount : 0;
        
        if (thumbnailRemovalRatio > 0.8 || cacheRemovalRatio > 0.8) {
            print("   ⚠️  SAFETY CHECK FAILED: Would remove more than 50% of records!");
            print("      Thumbnail removal ratio: " + Math.round(thumbnailRemovalRatio * 100 * 10) / 10 + "%");
            print("      Cache removal ratio: " + Math.round(cacheRemovalRatio * 100 * 10) / 10 + "%");
            print("      Would skip this collection to prevent accidental mass deletion");
            return; // Use return instead of continue in forEach
        } else {
            print("   🔍 DRY RUN: Would remove " + thumbnailDuplicates.length + " duplicate thumbnails");
            print("   🔍 DRY RUN: Would remove " + cacheDuplicates.length + " duplicate cache images");
        }
        
        totalThumbnailDuplicates += thumbnailDuplicates.length;
        totalCacheDuplicates += cacheDuplicates.length;
        totalDuplicatesRemoved += thumbnailDuplicates.length + cacheDuplicates.length;
    }
    
    // Force garbage collection if available
    if (typeof gc !== 'undefined') {
        gc();
    }
});

print("\n📈 Summary:");
print("   Total Collections: " + totalCollections);
print("   Collections with Duplicates: " + collectionsWithDuplicates);
print("   Total Thumbnail Duplicates: " + totalThumbnailDuplicates);
print("   Total Cache Duplicates: " + totalCacheDuplicates);
print("   Total Duplicates Removed: " + totalDuplicatesRemoved);

print("\n🔍 This was a dry run. No changes were made.");
print("💡 To run for real, use: mongosh image_viewer cleanup-duplicate-records-working.js");
print("💡 Always backup your database before running cleanup scripts!");
