// Cleanup Duplicate Thumbnail and Cache Records - CORRECT VERSION
// This script handles the ACTUAL data structure where thumbnails and cache are separate collections
// Run this script using MongoDB shell: mongosh image_viewer cleanup-duplicate-records-mongo-shell-correct.js
// 
// IMPORTANT: This script modifies your database. Always backup first!
// Backup command: mongodump --db image_viewer --out backup_before_cleanup

print("🧹 Cleaning up duplicate thumbnail and cache records...");
print("Database: image_viewer");
print("Collections: thumbnails, cache");
print("⚠️  WARNING: This script will modify your database!");
print("⚠️  Make sure you have a backup before proceeding!");
print("");

// Check if this is a dry run
var isDryRun = false;

// Check if dry run was set via --eval
if (typeof isDryRun !== 'undefined' && isDryRun === true) {
    isDryRun = true;
}
// Check if dry run was set via command line arguments (legacy mongo shell)
else if (typeof process !== 'undefined' && process.argv) {
    for (var i = 0; i < process.argv.length; i++) {
        if (process.argv[i] === '--dry-run') {
            isDryRun = true;
            break;
        }
    }
}

if (isDryRun) {
    print("🔍 DRY RUN MODE - No changes will be made");
} else {
    print("🚨 LIVE MODE - Changes will be made to the database");
    print("   Press Ctrl+C to cancel, or wait 5 seconds to continue...");
    
    // Give user time to cancel
    sleep(5000);
}

// ============================================================================
// CLEANUP THUMBNAILS COLLECTION
// ============================================================================
print("📸 Processing thumbnails collection...");

var totalThumbnails = db.thumbnails.countDocuments({});
print("📊 Found " + totalThumbnails + " thumbnails to analyze");

var thumbnailDuplicatesRemoved = 0;
var thumbnailGroups = {};

// Group thumbnails by imageId + width + height
var thumbnailsCursor = db.thumbnails.find({});
thumbnailsCursor.forEach(function(thumb) {
    var key = thumb.imageId + "_" + thumb.width + "_" + thumb.height;
    if (!thumbnailGroups[key]) {
        thumbnailGroups[key] = [];
    }
    thumbnailGroups[key].push(thumb);
});

// Process each group
for (var key in thumbnailGroups) {
    var group = thumbnailGroups[key];
    if (group.length > 1) {
        print("🔍 Found " + group.length + " duplicate thumbnails for image " + group[0].imageId + " (" + group[0].width + "x" + group[0].height + ")");
        
        // Keep the first one (oldest by creation date), remove the rest
        var keepThumbnail = group[0];
        var duplicatesToRemove = group.slice(1);
        
        // Sort by createdAt to keep the oldest
        group.sort(function(a, b) {
            return new Date(a.createdAt) - new Date(b.createdAt);
        });
        keepThumbnail = group[0];
        duplicatesToRemove = group.slice(1);
        
        print("   Keeping thumbnail: " + keepThumbnail._id + " (created: " + keepThumbnail.createdAt + ")");
        
        for (var i = 0; i < duplicatesToRemove.length; i++) {
            var duplicate = duplicatesToRemove[i];
            print("   Removing duplicate: " + duplicate._id + " (created: " + duplicate.createdAt + ")");
            
            if (isDryRun) {
                print("   🔍 DRY RUN: Would remove thumbnail " + duplicate._id);
            } else {
                try {
                    var result = db.thumbnails.deleteOne({ "_id": duplicate._id });
                    if (result.deletedCount === 1) {
                        print("   ✅ Removed duplicate thumbnail " + duplicate._id);
                        thumbnailDuplicatesRemoved++;
                    } else {
                        print("   ⚠️  Failed to remove thumbnail " + duplicate._id);
                    }
                } catch (error) {
                    print("   ❌ ERROR removing thumbnail " + duplicate._id + ": " + error.message);
                }
            }
        }
    }
}

// ============================================================================
// CLEANUP CACHE COLLECTION
// ============================================================================
print("\n💾 Processing cache collection...");

var totalCache = db.cache.countDocuments({});
print("📊 Found " + totalCache + " cache records to analyze");

var cacheDuplicatesRemoved = 0;
var cacheGroups = {};

// Group cache by imageId (only one cache per image)
var cacheCursor = db.cache.find({});
cacheCursor.forEach(function(cache) {
    var key = cache.imageId;
    if (!cacheGroups[key]) {
        cacheGroups[key] = [];
    }
    cacheGroups[key].push(cache);
});

// Process each group
for (var key in cacheGroups) {
    var group = cacheGroups[key];
    if (group.length > 1) {
        print("🔍 Found " + group.length + " duplicate cache records for image " + group[0].imageId);
        
        // Keep the first one (oldest by creation date), remove the rest
        var keepCache = group[0];
        var duplicatesToRemove = group.slice(1);
        
        // Sort by createdAt to keep the oldest
        group.sort(function(a, b) {
            return new Date(a.createdAt) - new Date(b.createdAt);
        });
        keepCache = group[0];
        duplicatesToRemove = group.slice(1);
        
        print("   Keeping cache: " + keepCache._id + " (created: " + keepCache.createdAt + ")");
        
        for (var i = 0; i < duplicatesToRemove.length; i++) {
            var duplicate = duplicatesToRemove[i];
            print("   Removing duplicate: " + duplicate._id + " (created: " + duplicate.createdAt + ")");
            
            if (isDryRun) {
                print("   🔍 DRY RUN: Would remove cache " + duplicate._id);
            } else {
                try {
                    var result = db.cache.deleteOne({ "_id": duplicate._id });
                    if (result.deletedCount === 1) {
                        print("   ✅ Removed duplicate cache " + duplicate._id);
                        cacheDuplicatesRemoved++;
                    } else {
                        print("   ⚠️  Failed to remove cache " + duplicate._id);
                    }
                } catch (error) {
                    print("   ❌ ERROR removing cache " + duplicate._id + ": " + error.message);
                }
            }
        }
    }
}

// ============================================================================
// SUMMARY
// ============================================================================
print("\n📈 Summary:");
print("   Total Thumbnails: " + totalThumbnails);
print("   Thumbnail Duplicates Removed: " + thumbnailDuplicatesRemoved);
print("   Total Cache Records: " + totalCache);
print("   Cache Duplicates Removed: " + cacheDuplicatesRemoved);
print("   Total Duplicates Removed: " + (thumbnailDuplicatesRemoved + cacheDuplicatesRemoved));

if (isDryRun) {
    print("\n🔍 This was a dry run. No changes were made.");
    print("   To run for real, remove --dry-run parameter");
} else {
    print("\n✅ Cleanup completed!");
}

print("\n💡 To prevent future duplicates, ensure the fixed consumers are deployed.");
print("💡 Always backup your database before running cleanup scripts!");
