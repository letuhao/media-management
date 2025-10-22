// Cleanup Duplicate Thumbnail and Cache Records - MongoDB Shell Script
// Run this script using MongoDB shell: mongo image_viewer cleanup-duplicate-records-mongo-shell.js
// 
// IMPORTANT: This script modifies your database. Always backup first!
// Backup command: mongodump --db image_viewer --out backup_before_cleanup

print("🧹 Cleaning up duplicate thumbnail and cache records...");
print("Database: image_viewer");
print("Collection: collections");
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

// Get collection count first
var totalCollections = db.collections.countDocuments({});
var collectionsWithDuplicates = 0;
var totalDuplicatesRemoved = 0;
var totalThumbnailDuplicates = 0;
var totalCacheDuplicates = 0;

print("📊 Found " + totalCollections + " collections to analyze");
print("🔄 Processing collections in batches to avoid memory issues...");

// Process collections in batches to avoid memory overflow
var batchSize = 10;
var processedCollections = 0;
var skip = 0;

while (skip < totalCollections) {
    print("📦 Processing batch " + Math.floor(skip / batchSize) + 1 + " (collections " + (skip + 1) + " to " + Math.min(skip + batchSize, totalCollections) + ")");
    
    var collections = db.collections.find({}).skip(skip).limit(batchSize).toArray();
    
    for (var i = 0; i < collections.length; i++) {
        var coll = collections[i];
    var collectionId = coll._id;
    var collectionName = coll.Name;
    var images = coll.Images;
    var thumbnails = coll.Thumbnails;
    var cacheImages = coll.CacheImages;
    
    if (images && thumbnails && cacheImages) {
        var imageCount = images.length;
        var thumbnailCount = thumbnails.length;
        var cacheCount = cacheImages.length;
        
        // Find duplicate thumbnails (same ImageId + Width + Height)
        var thumbnailDuplicates = [];
        var thumbnailGroups = {};
        
        for (var j = 0; j < thumbnails.length; j++) {
            var thumb = thumbnails[j];
            var key = thumb.ImageId + "_" + thumb.Width + "_" + thumb.Height;
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
        
        // Find duplicate cache images (same ImageId)
        var cacheDuplicates = [];
        var cacheGroups = {};
        
        for (var j = 0; j < cacheImages.length; j++) {
            var cache = cacheImages[j];
            var key = cache.ImageId;
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
            print("\n🔍 Collection: " + collectionName + " (" + collectionId + ")");
            print("   Images: " + imageCount);
            print("   Thumbnails: " + thumbnailCount + " (Duplicates: " + thumbnailDuplicates.length + ")");
            print("   Cache: " + cacheCount + " (Duplicates: " + cacheDuplicates.length + ")");
            print("   Progress: " + processedCollections + "/" + totalCollections + " collections processed");
            
            // SAFETY CHECK: Verify we're not removing too many records
            var expectedThumbnailCount = thumbnailCount - thumbnailDuplicates.length;
            var expectedCacheCount = cacheCount - cacheDuplicates.length;
            
            // SAFETY CHECK: Ensure we don't remove more than 50% of records
            var thumbnailRemovalRatio = thumbnailCount > 0 ? thumbnailDuplicates.length / thumbnailCount : 0;
            var cacheRemovalRatio = cacheCount > 0 ? cacheDuplicates.length / cacheCount : 0;
            
            if (thumbnailRemovalRatio > 0.5 || cacheRemovalRatio > 0.5) {
                print("   ⚠️  SAFETY CHECK FAILED: Would remove more than 50% of records!");
                print("      Thumbnail removal ratio: " + Math.round(thumbnailRemovalRatio * 100 * 10) / 10 + "%");
                print("      Cache removal ratio: " + Math.round(cacheRemovalRatio * 100 * 10) / 10 + "%");
                print("      Skipping this collection to prevent accidental mass deletion");
                continue;
            }
            
            // SAFETY CHECK: Ensure we don't end up with negative counts
            if (expectedThumbnailCount < 0 || expectedCacheCount < 0) {
                print("   ⚠️  SAFETY CHECK FAILED: Would result in negative counts!");
                print("      Expected thumbnails: " + expectedThumbnailCount);
                print("      Expected cache: " + expectedCacheCount);
                print("      Skipping this collection to prevent data corruption");
                continue;
            }
            
            // Remove duplicate thumbnails
            if (thumbnailDuplicates.length > 0) {
                var newThumbnails = [];
                for (var j = 0; j < thumbnails.length; j++) {
                    var isDuplicate = false;
                    // FIXED: Use proper duplicate detection by checking if this thumbnail
                    // is in the duplicates array by comparing ImageId + Width + Height
                    for (var k = 0; k < thumbnailDuplicates.length; k++) {
                        if (thumbnails[j].ImageId === thumbnailDuplicates[k].ImageId &&
                            thumbnails[j].Width === thumbnailDuplicates[k].Width &&
                            thumbnails[j].Height === thumbnailDuplicates[k].Height) {
                            isDuplicate = true;
                            break;
                        }
                    }
                    if (!isDuplicate) {
                        newThumbnails.push(thumbnails[j]);
                    }
                }
                
                // SAFETY CHECK: Verify count makes sense
                if (newThumbnails.length !== expectedThumbnailCount) {
                    print("   ⚠️  SAFETY CHECK FAILED: Thumbnail count mismatch!");
                    print("      Expected: " + expectedThumbnailCount + ", Actual: " + newThumbnails.length);
                    print("      Skipping thumbnail cleanup for this collection");
                } else {
                    if (isDryRun) {
                        print("   🔍 DRY RUN: Would remove " + thumbnailDuplicates.length + " duplicate thumbnails");
                    } else {
                        try {
                            var result = db.collections.updateOne(
                                { "_id": collectionId },
                                { "$set": { "Thumbnails": newThumbnails } }
                            );
                            if (result.modifiedCount === 1) {
                                print("   ✅ Removed " + thumbnailDuplicates.length + " duplicate thumbnails");
                            } else {
                                print("   ⚠️  Failed to update thumbnails for collection " + collectionId);
                            }
                        } catch (error) {
                            print("   ❌ ERROR updating thumbnails: " + error.message);
                        }
                    }
                }
            }
            
            // Remove duplicate cache images
            if (cacheDuplicates.length > 0) {
                var newCacheImages = [];
                for (var j = 0; j < cacheImages.length; j++) {
                    var isDuplicate = false;
                    // FIXED: Use proper duplicate detection by checking ImageId
                    for (var k = 0; k < cacheDuplicates.length; k++) {
                        if (cacheImages[j].ImageId === cacheDuplicates[k].ImageId) {
                            isDuplicate = true;
                            break;
                        }
                    }
                    if (!isDuplicate) {
                        newCacheImages.push(cacheImages[j]);
                    }
                }
                
                // SAFETY CHECK: Verify count makes sense
                if (newCacheImages.length !== expectedCacheCount) {
                    print("   ⚠️  SAFETY CHECK FAILED: Cache count mismatch!");
                    print("      Expected: " + expectedCacheCount + ", Actual: " + newCacheImages.length);
                    print("      Skipping cache cleanup for this collection");
                } else {
                    if (isDryRun) {
                        print("   🔍 DRY RUN: Would remove " + cacheDuplicates.length + " duplicate cache images");
                    } else {
                        try {
                            var result = db.collections.updateOne(
                                { "_id": collectionId },
                                { "$set": { "CacheImages": newCacheImages } }
                            );
                            if (result.modifiedCount === 1) {
                                print("   ✅ Removed " + cacheDuplicates.length + " duplicate cache images");
                            } else {
                                print("   ⚠️  Failed to update cache images for collection " + collectionId);
                            }
                        } catch (error) {
                            print("   ❌ ERROR updating cache images: " + error.message);
                        }
                    }
                }
            }
            
            totalThumbnailDuplicates += thumbnailDuplicates.length;
            totalCacheDuplicates += cacheDuplicates.length;
            totalDuplicatesRemoved += thumbnailDuplicates.length + cacheDuplicates.length;
        }
        
        // Clear collection variables to free memory
        images = null;
        thumbnails = null;
        cacheImages = null;
        thumbnailDuplicates = null;
        cacheDuplicates = null;
        thumbnailGroups = null;
        cacheGroups = null;
        
        processedCollections++;
    }
    
    skip += batchSize;
    
    // Clear variables to free memory
    collections = null;
    
    // Force garbage collection if available
    if (typeof gc !== 'undefined') {
        gc();
    }
}

print("\n📈 Summary:");
print("   Total Collections: " + totalCollections);
print("   Collections with Duplicates: " + collectionsWithDuplicates);
print("   Total Thumbnail Duplicates: " + totalThumbnailDuplicates);
print("   Total Cache Duplicates: " + totalCacheDuplicates);
print("   Total Duplicates Removed: " + totalDuplicatesRemoved);

if (isDryRun) {
    print("\n🔍 This was a dry run. No changes were made.");
    print("   To run for real, remove --dry-run parameter");
} else {
    print("\n✅ Cleanup completed!");
}

print("\n💡 To prevent future duplicates, ensure the fixed consumers are deployed.");
print("💡 Always backup your database before running cleanup scripts!");
