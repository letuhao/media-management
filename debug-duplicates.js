// Debug Script - Check Actual Data Structure and Duplicates
// Run this to see what's actually in your collections
// mongosh image_viewer debug-duplicates.js

print("🔍 Debugging duplicate detection...");
print("Database: image_viewer");
print("");

// Get a sample collection with data
var sampleCollection = db.collections.findOne({
    "thumbnails": { "$exists": true, "$ne": [] },
    "cacheImages": { "$exists": true, "$ne": [] }
});

if (!sampleCollection) {
    print("❌ No collections found with thumbnails and cacheImages");
    exit;
}

print("📸 Sample Collection: " + sampleCollection.name);
print("   Collection ID: " + sampleCollection._id);
print("   Images: " + (sampleCollection.images ? sampleCollection.images.length : 0));
print("   Thumbnails: " + (sampleCollection.thumbnails ? sampleCollection.thumbnails.length : 0));
print("   Cache Images: " + (sampleCollection.cacheImages ? sampleCollection.cacheImages.length : 0));
print("");

// Debug thumbnails
if (sampleCollection.thumbnails && sampleCollection.thumbnails.length > 0) {
    print("🖼️ Thumbnail Analysis:");
    var thumbnailGroups = {};
    
    for (var i = 0; i < sampleCollection.thumbnails.length; i++) {
        var thumb = sampleCollection.thumbnails[i];
        var key = thumb.imageId + "_" + thumb.width + "_" + thumb.height;
        
        print("   Thumbnail " + (i + 1) + ":");
        print("     _id: " + thumb._id);
        print("     imageId: " + thumb.imageId);
        print("     width: " + thumb.width);
        print("     height: " + thumb.height);
        print("     key: " + key);
        
        if (!thumbnailGroups[key]) {
            thumbnailGroups[key] = [];
        }
        thumbnailGroups[key].push(thumb);
    }
    
    print("\n   Thumbnail Groups:");
    for (var key in thumbnailGroups) {
        print("     Group '" + key + "': " + thumbnailGroups[key].length + " items");
        if (thumbnailGroups[key].length > 1) {
            print("       ⚠️  DUPLICATE FOUND!");
            for (var j = 0; j < thumbnailGroups[key].length; j++) {
                print("         " + (j + 1) + ". _id: " + thumbnailGroups[key][j]._id);
            }
        }
    }
}

// Debug cache images
if (sampleCollection.cacheImages && sampleCollection.cacheImages.length > 0) {
    print("\n💾 Cache Image Analysis:");
    var cacheGroups = {};
    
    for (var i = 0; i < sampleCollection.cacheImages.length; i++) {
        var cache = sampleCollection.cacheImages[i];
        var key = cache.imageId;
        
        print("   Cache " + (i + 1) + ":");
        print("     _id: " + cache._id);
        print("     imageId: " + cache.imageId);
        print("     width: " + cache.width);
        print("     height: " + cache.height);
        print("     key: " + key);
        
        if (!cacheGroups[key]) {
            cacheGroups[key] = [];
        }
        cacheGroups[key].push(cache);
    }
    
    print("\n   Cache Groups:");
    for (var key in cacheGroups) {
        print("     Group '" + key + "': " + cacheGroups[key].length + " items");
        if (cacheGroups[key].length > 1) {
            print("       ⚠️  DUPLICATE FOUND!");
            for (var j = 0; j < cacheGroups[key].length; j++) {
                print("         " + (j + 1) + ". _id: " + cacheGroups[key][j]._id);
            }
        }
    }
}

// Check if there are any collections with duplicates
print("\n🔍 Checking for collections with duplicates...");
var collectionsWithThumbnailDuplicates = 0;
var collectionsWithCacheDuplicates = 0;

var collectionsCursor = db.collections.find({
    "thumbnails": { "$exists": true, "$ne": [] }
}).limit(10);

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
    
    var hasThumbnailDuplicates = false;
    for (var key in thumbnailGroups) {
        if (thumbnailGroups[key].length > 1) {
            hasThumbnailDuplicates = true;
            break;
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
    
    var hasCacheDuplicates = false;
    for (var key in cacheGroups) {
        if (cacheGroups[key].length > 1) {
            hasCacheDuplicates = true;
            break;
        }
    }
    
    if (hasThumbnailDuplicates) {
        collectionsWithThumbnailDuplicates++;
        print("   Collection '" + coll.name + "' has thumbnail duplicates");
    }
    
    if (hasCacheDuplicates) {
        collectionsWithCacheDuplicates++;
        print("   Collection '" + coll.name + "' has cache duplicates");
    }
});

print("\n📊 Summary:");
print("   Collections checked: 10");
print("   Collections with thumbnail duplicates: " + collectionsWithThumbnailDuplicates);
print("   Collections with cache duplicates: " + collectionsWithCacheDuplicates);

print("\n✅ Debug completed!");
