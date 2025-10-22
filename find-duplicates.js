// Find Collections with Actual Duplicates
// mongosh image_viewer find-duplicates.js

print("🔍 Searching for collections with actual duplicates...");
print("");

var collectionsWithThumbnailDuplicates = 0;
var collectionsWithCacheDuplicates = 0;
var totalThumbnailDuplicates = 0;
var totalCacheDuplicates = 0;

// Check all collections for duplicates
var collectionsCursor = db.collections.find({
    "thumbnails": { "$exists": true, "$ne": [] }
});

collectionsCursor.forEach(function(coll) {
    var thumbnails = coll.thumbnails || [];
    var cacheImages = coll.cacheImages || [];
    
    // Check thumbnail duplicates
    var thumbnailGroups = {};
    for (var i = 0; i < thumbnails.length; i++) {
        var thumb = thumbnails[i];
        var key = thumb.imageId + "_" + thumb.width + "_" + thumb.height;
        if (!thumbnailGroups[key]) {
            thumbnailGroups[key] = [];
        }
        thumbnailGroups[key].push(thumb);
    }
    
    var thumbnailDuplicates = 0;
    for (var key in thumbnailGroups) {
        if (thumbnailGroups[key].length > 1) {
            thumbnailDuplicates += thumbnailGroups[key].length - 1;
        }
    }
    
    // Check cache duplicates
    var cacheGroups = {};
    for (var i = 0; i < cacheImages.length; i++) {
        var cache = cacheImages[i];
        var key = cache.imageId;
        if (!cacheGroups[key]) {
            cacheGroups[key] = [];
        }
        cacheGroups[key].push(cache);
    }
    
    var cacheDuplicates = 0;
    for (var key in cacheGroups) {
        if (cacheGroups[key].length > 1) {
            cacheDuplicates += cacheGroups[key].length - 1;
        }
    }
    
    if (thumbnailDuplicates > 0 || cacheDuplicates > 0) {
        print("🔍 Collection: " + coll.name);
        print("   ID: " + coll._id);
        print("   Images: " + (coll.images ? coll.images.length : 0));
        print("   Thumbnails: " + thumbnails.length + " (Duplicates: " + thumbnailDuplicates + ")");
        print("   Cache: " + cacheImages.length + " (Duplicates: " + cacheDuplicates + ")");
        
        if (thumbnailDuplicates > 0) {
            collectionsWithThumbnailDuplicates++;
            totalThumbnailDuplicates += thumbnailDuplicates;
        }
        
        if (cacheDuplicates > 0) {
            collectionsWithCacheDuplicates++;
            totalCacheDuplicates += cacheDuplicates;
        }
        
        print("");
    }
});

print("📊 Summary:");
print("   Collections with thumbnail duplicates: " + collectionsWithThumbnailDuplicates);
print("   Collections with cache duplicates: " + collectionsWithCacheDuplicates);
print("   Total thumbnail duplicates: " + totalThumbnailDuplicates);
print("   Total cache duplicates: " + totalCacheDuplicates);

if (totalThumbnailDuplicates === 0 && totalCacheDuplicates === 0) {
    print("\n✅ No duplicates found! Your database is clean.");
    print("💡 The duplicates you mentioned earlier may have been:");
    print("   - Already cleaned up");
    print("   - From a different database");
    print("   - From a different collection");
} else {
    print("\n⚠️  Duplicates found! Run the cleanup script to remove them.");
}

print("\n✅ Search completed!");
