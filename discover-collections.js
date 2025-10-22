// Database Structure Discovery Script
// Run this to see what collections actually exist in your database
// mongosh image_viewer discover-collections.js

print("🔍 Discovering database structure...");
print("Database: image_viewer");
print("");

// List all collections
print("📋 All collections in database:");
var collections = db.getCollectionNames();
for (var i = 0; i < collections.length; i++) {
    print("   " + (i + 1) + ". " + collections[i]);
}

print("");

// Check collections collection structure
if (collections.includes("collections")) {
    print("📸 Collections collection structure:");
    var sampleCollection = db.collections.findOne({});
    if (sampleCollection) {
        print("   Sample collection fields:");
        for (var field in sampleCollection) {
            if (field !== "_id") {
                var value = sampleCollection[field];
                var type = Array.isArray(value) ? "Array[" + value.length + "]" : typeof value;
                print("     " + field + ": " + type);
            }
        }
    } else {
        print("   No collections found");
    }
}

// Check if thumbnails collection exists
if (collections.includes("thumbnails")) {
    print("\n🖼️ Thumbnails collection structure:");
    var sampleThumbnail = db.thumbnails.findOne({});
    if (sampleThumbnail) {
        print("   Sample thumbnail fields:");
        for (var field in sampleThumbnail) {
            if (field !== "_id") {
                var value = sampleThumbnail[field];
                var type = typeof value;
                print("     " + field + ": " + type);
            }
        }
    } else {
        print("   No thumbnails found");
    }
}

// Check if cache collection exists
if (collections.includes("cache")) {
    print("\n💾 Cache collection structure:");
    var sampleCache = db.cache.findOne({});
    if (sampleCache) {
        print("   Sample cache fields:");
        for (var field in sampleCache) {
            if (field !== "_id") {
                var value = sampleCache[field];
                var type = typeof value;
                print("     " + field + ": " + type);
            }
        }
    } else {
        print("   No cache records found");
    }
}

// Check if cacheImages collection exists (alternative name)
if (collections.includes("cacheImages")) {
    print("\n💾 CacheImages collection structure:");
    var sampleCacheImage = db.cacheImages.findOne({});
    if (sampleCacheImage) {
        print("   Sample cacheImage fields:");
        for (var field in sampleCacheImage) {
            if (field !== "_id") {
                var value = sampleCacheImage[field];
                var type = typeof value;
                print("     " + field + ": " + type);
            }
        }
    } else {
        print("   No cacheImages found");
    }
}

print("\n✅ Database structure discovery completed!");
print("💡 Use this information to determine the correct cleanup approach.");
