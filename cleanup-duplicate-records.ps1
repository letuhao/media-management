# Cleanup Duplicate Thumbnail and Cache Records
# This script removes duplicate records from MongoDB collections
# Run this after fixing the duplicate generation bug

param(
    [string]$MongoConnectionString = "mongodb://localhost:27017",
    [string]$DatabaseName = "image_viewer",
    [string]$CollectionName = "collections",
    [switch]$DryRun = $false
)

Write-Host "🧹 Cleaning up duplicate thumbnail and cache records..." -ForegroundColor Green
Write-Host "Database: $DatabaseName" -ForegroundColor Cyan
Write-Host "Collection: $CollectionName" -ForegroundColor Cyan
Write-Host "Dry Run: $DryRun" -ForegroundColor Yellow

if ($DryRun) {
    Write-Host "⚠️  DRY RUN MODE - No changes will be made" -ForegroundColor Yellow
}

# Load MongoDB .NET Driver assemblies
try {
    Write-Host "📦 Loading MongoDB .NET Driver assemblies..." -ForegroundColor Gray
    
    # Try to find MongoDB driver assemblies in common locations
    $mongoDriverPaths = @(
        ".\packages\MongoDB.Driver.*\lib\net*\MongoDB.Driver.dll",
        ".\packages\MongoDB.Bson.*\lib\net*\MongoDB.Bson.dll",
        ".\bin\Debug\net*\MongoDB.Driver.dll",
        ".\bin\Release\net*\MongoDB.Driver.dll",
        ".\bin\MongoDB.Driver.dll",
        ".\MongoDB.Driver.dll"
    )
    
    $mongoDriverDll = $null
    $mongoBsonDll = $null
    
    foreach ($path in $mongoDriverPaths) {
        $found = Get-ChildItem -Path $path -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) {
            $mongoDriverDll = $found.FullName
            Write-Host "   Found MongoDB.Driver.dll: $($found.FullName)" -ForegroundColor Gray
            break
        }
    }
    
    foreach ($path in $mongoDriverPaths) {
        $path = $path -replace "MongoDB.Driver", "MongoDB.Bson"
        $found = Get-ChildItem -Path $path -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) {
            $mongoBsonDll = $found.FullName
            Write-Host "   Found MongoDB.Bson.dll: $($found.FullName)" -ForegroundColor Gray
            break
        }
    }
    
    if ($mongoBsonDll) {
        Add-Type -Path $mongoBsonDll
        Write-Host "   ✅ Loaded MongoDB.Bson.dll" -ForegroundColor Green
    }
    
    if ($mongoDriverDll) {
        Add-Type -Path $mongoDriverDll
        Write-Host "   ✅ Loaded MongoDB.Driver.dll" -ForegroundColor Green
    }
    
    if (-not $mongoDriverDll -or -not $mongoBsonDll) {
        Write-Host "❌ FAILED to find MongoDB driver assemblies!" -ForegroundColor Red
        Write-Host "   Please ensure MongoDB.Driver NuGet package is installed." -ForegroundColor Red
        Write-Host "   Try running: dotnet add package MongoDB.Driver" -ForegroundColor Red
        Write-Host "   Or install via NuGet Package Manager in Visual Studio." -ForegroundColor Red
        exit 1
    }
    
} catch {
    Write-Host "❌ FAILED to load MongoDB assemblies: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "   Please ensure MongoDB.Driver NuGet package is installed." -ForegroundColor Red
    exit 1
}

# MongoDB connection
try {
    $mongoClient = New-Object MongoDB.Driver.MongoClient($MongoConnectionString)
    $database = $mongoClient.GetDatabase($DatabaseName)
    $collection = $database.GetCollection($CollectionName)
    Write-Host "✅ Connected to MongoDB database: $DatabaseName" -ForegroundColor Green
} catch {
    Write-Host "❌ FAILED to connect to MongoDB: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "   Please check your connection string and ensure MongoDB is running." -ForegroundColor Red
    exit 1
}

Write-Host "`n📊 Analyzing collections for duplicates..." -ForegroundColor Green

# Get all collections
try {
    $collections = $collection.Find({}).ToList()
    Write-Host "✅ Found $($collections.Count) collections to analyze" -ForegroundColor Green
} catch {
    Write-Host "❌ FAILED to query collections: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "   Please check your database permissions and collection name." -ForegroundColor Red
    exit 1
}

$totalCollections = $collections.Count
$collectionsWithDuplicates = 0
$totalDuplicatesRemoved = 0
$totalThumbnailDuplicates = 0
$totalCacheDuplicates = 0

foreach ($coll in $collections) {
    $collectionId = $coll._id
    $collectionName = $coll.Name
    $images = $coll.Images
    $thumbnails = $coll.Thumbnails
    $cacheImages = $coll.CacheImages
    
    if ($images -and $thumbnails -and $cacheImages) {
        $imageCount = $images.Count
        $thumbnailCount = $thumbnails.Count
        $cacheCount = $cacheImages.Count
        
        # Check for duplicates
        $thumbnailDuplicates = @()
        $cacheDuplicates = @()
        
        # Find duplicate thumbnails (same ImageId + Width + Height)
        # CRITICAL: Only check ImageId + Width + Height, not ThumbnailPath
        # Different paths for same image+size are legitimate (e.g., different cache folders)
        $thumbnailGroups = $thumbnails | Group-Object -Property @{Expression={$_.ImageId + "_" + $_.Width + "_" + $_.Height}}
        foreach ($group in $thumbnailGroups) {
            if ($group.Count -gt 1) {
                # Keep the first one, mark others as duplicates
                $thumbnailDuplicates += $group.Group[1..($group.Count-1)]
            }
        }
        
        # Find duplicate cache images (same ImageId)
        # CRITICAL: Only check ImageId, not CachePath
        # Different paths for same image are legitimate (e.g., different cache folders)
        $cacheGroups = $cacheImages | Group-Object -Property ImageId
        foreach ($group in $cacheGroups) {
            if ($group.Count -gt 1) {
                # Keep the first one, mark others as duplicates
                $cacheDuplicates += $group.Group[1..($group.Count-1)]
            }
        }
        
        if ($thumbnailDuplicates.Count -gt 0 -or $cacheDuplicates.Count -gt 0) {
            $collectionsWithDuplicates++
            Write-Host "`n🔍 Collection: $collectionName ($collectionId)" -ForegroundColor Yellow
            Write-Host "   Images: $imageCount" -ForegroundColor Gray
            Write-Host "   Thumbnails: $thumbnailCount (Duplicates: $($thumbnailDuplicates.Count))" -ForegroundColor Gray
            Write-Host "   Cache: $cacheCount (Duplicates: $($cacheDuplicates.Count))" -ForegroundColor Gray
            
            if (-not $DryRun) {
                # SAFETY CHECK: Verify we're not removing too many records
                $expectedThumbnailCount = $thumbnailCount - $thumbnailDuplicates.Count
                $expectedCacheCount = $cacheCount - $cacheDuplicates.Count
                
                # SAFETY CHECK: Ensure we don't remove more than 50% of records (prevents accidental mass deletion)
                $thumbnailRemovalRatio = if ($thumbnailCount -gt 0) { $thumbnailDuplicates.Count / $thumbnailCount } else { 0 }
                $cacheRemovalRatio = if ($cacheCount -gt 0) { $cacheDuplicates.Count / $cacheCount } else { 0 }
                
                if ($thumbnailRemovalRatio -gt 0.5 -or $cacheRemovalRatio -gt 0.5) {
                    Write-Host "   ⚠️  SAFETY CHECK FAILED: Would remove more than 50% of records!" -ForegroundColor Red
                    Write-Host "      Thumbnail removal ratio: $([math]::Round($thumbnailRemovalRatio * 100, 1))%" -ForegroundColor Red
                    Write-Host "      Cache removal ratio: $([math]::Round($cacheRemovalRatio * 100, 1))%" -ForegroundColor Red
                    Write-Host "      Skipping this collection to prevent accidental mass deletion" -ForegroundColor Red
                    continue
                }
                
                # SAFETY CHECK: Ensure we don't end up with negative counts
                if ($expectedThumbnailCount -lt 0 -or $expectedCacheCount -lt 0) {
                    Write-Host "   ⚠️  SAFETY CHECK FAILED: Would result in negative counts!" -ForegroundColor Red
                    Write-Host "      Expected thumbnails: $expectedThumbnailCount" -ForegroundColor Red
                    Write-Host "      Expected cache: $expectedCacheCount" -ForegroundColor Red
                    Write-Host "      Skipping this collection to prevent data corruption" -ForegroundColor Red
                    continue
                }
                
                # Remove duplicate thumbnails
                if ($thumbnailDuplicates.Count -gt 0) {
                    $newThumbnails = $thumbnails | Where-Object { $thumbnailDuplicates -notcontains $_ }
                    
                    # SAFETY CHECK: Verify count makes sense
                    if ($newThumbnails.Count -ne $expectedThumbnailCount) {
                        Write-Host "   ⚠️  SAFETY CHECK FAILED: Thumbnail count mismatch!" -ForegroundColor Red
                        Write-Host "      Expected: $expectedThumbnailCount, Actual: $($newThumbnails.Count)" -ForegroundColor Red
                        Write-Host "      Skipping thumbnail cleanup for this collection" -ForegroundColor Red
                    } else {
                        try {
                            $collection.UpdateOne(
                                @{ "_id" = $collectionId },
                                @{ "$set" = @{ "Thumbnails" = $newThumbnails } }
                            )
                            Write-Host "   ✅ Removed $($thumbnailDuplicates.Count) duplicate thumbnails" -ForegroundColor Green
                        } catch {
                            Write-Host "   ❌ FAILED to remove duplicate thumbnails: $($_.Exception.Message)" -ForegroundColor Red
                            Write-Host "      Skipping thumbnail cleanup for this collection" -ForegroundColor Red
                        }
                    }
                }
                
                # Remove duplicate cache images
                if ($cacheDuplicates.Count -gt 0) {
                    $newCacheImages = $cacheImages | Where-Object { $cacheDuplicates -notcontains $_ }
                    
                    # SAFETY CHECK: Verify count makes sense
                    if ($newCacheImages.Count -ne $expectedCacheCount) {
                        Write-Host "   ⚠️  SAFETY CHECK FAILED: Cache count mismatch!" -ForegroundColor Red
                        Write-Host "      Expected: $expectedCacheCount, Actual: $($newCacheImages.Count)" -ForegroundColor Red
                        Write-Host "      Skipping cache cleanup for this collection" -ForegroundColor Red
                    } else {
                        try {
                            $collection.UpdateOne(
                                @{ "_id" = $collectionId },
                                @{ "$set" = @{ "CacheImages" = $newCacheImages } }
                            )
                            Write-Host "   ✅ Removed $($cacheDuplicates.Count) duplicate cache images" -ForegroundColor Green
                        } catch {
                            Write-Host "   ❌ FAILED to remove duplicate cache images: $($_.Exception.Message)" -ForegroundColor Red
                            Write-Host "      Skipping cache cleanup for this collection" -ForegroundColor Red
                        }
                    }
                }
            } else {
                Write-Host "   🔍 Would remove $($thumbnailDuplicates.Count) duplicate thumbnails" -ForegroundColor Cyan
                Write-Host "   🔍 Would remove $($cacheDuplicates.Count) duplicate cache images" -ForegroundColor Cyan
            }
            
            $totalThumbnailDuplicates += $thumbnailDuplicates.Count
            $totalCacheDuplicates += $cacheDuplicates.Count
            $totalDuplicatesRemoved += $thumbnailDuplicates.Count + $cacheDuplicates.Count
        }
    }
}

Write-Host "`n📈 Summary:" -ForegroundColor Green
Write-Host "   Total Collections: $totalCollections" -ForegroundColor Cyan
Write-Host "   Collections with Duplicates: $collectionsWithDuplicates" -ForegroundColor Cyan
Write-Host "   Total Thumbnail Duplicates: $totalThumbnailDuplicates" -ForegroundColor Cyan
Write-Host "   Total Cache Duplicates: $totalCacheDuplicates" -ForegroundColor Cyan
Write-Host "   Total Duplicates Removed: $totalDuplicatesRemoved" -ForegroundColor Cyan

if ($DryRun) {
    Write-Host "`n⚠️  This was a dry run. Run without -DryRun to actually remove duplicates." -ForegroundColor Yellow
} else {
    Write-Host "`n✅ Cleanup completed!" -ForegroundColor Green
}

Write-Host "`n💡 To prevent future duplicates, ensure the fixed consumers are deployed." -ForegroundColor Blue
