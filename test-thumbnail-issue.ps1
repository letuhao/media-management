# Test Thumbnail Issue
# Checks if thumbnail files exist and if they're properly linked to images

$token = Get-Content "admin-token.txt" -Raw | Select-Object -First 1
$collectionId = "68f2a388ff19d7b375b40da9" # From the user's example

Write-Host "🔍 Testing Collection: $collectionId" -ForegroundColor Cyan

# 1. Get collection detail
Write-Host "`n📦 Fetching collection..." -ForegroundColor Yellow
$collection = Invoke-RestMethod -Uri "http://localhost:3000/api/v1/collections/$collectionId" `
    -Headers @{ "Authorization" = "Bearer $token" } `
    -Method Get

Write-Host "Collection: $($collection.name)" -ForegroundColor Green
Write-Host "Images: $($collection.images.Count)" -ForegroundColor Green
Write-Host "Thumbnails: $($collection.thumbnails.Count)" -ForegroundColor Green

# 2. Check first image and its thumbnail
if ($collection.images.Count -gt 0) {
    $firstImage = $collection.images[0]
    Write-Host "`n🖼️  First Image:" -ForegroundColor Yellow
    Write-Host "  ID: $($firstImage.id)" -ForegroundColor White
    Write-Host "  Filename: $($firstImage.filename)" -ForegroundColor White
    Write-Host "  RelativePath: $($firstImage.relativePath)" -ForegroundColor White
    
    # Find matching thumbnail
    $matchingThumbnail = $collection.thumbnails | Where-Object { $_.imageId -eq $firstImage.id }
    
    if ($matchingThumbnail) {
        Write-Host "`n✅ Found matching thumbnail:" -ForegroundColor Green
        Write-Host "  Thumbnail ID: $($matchingThumbnail.id)" -ForegroundColor White
        Write-Host "  Size: $($matchingThumbnail.width)x$($matchingThumbnail.height)" -ForegroundColor White
        Write-Host "  Path: $($matchingThumbnail.thumbnailPath)" -ForegroundColor White
        Write-Host "  IsGenerated: $($matchingThumbnail.isGenerated)" -ForegroundColor White
        Write-Host "  IsValid: $($matchingThumbnail.isValid)" -ForegroundColor White
        
        # Check if file exists
        if (Test-Path $matchingThumbnail.thumbnailPath) {
            $fileInfo = Get-Item $matchingThumbnail.thumbnailPath
            Write-Host "  File exists: YES ✅" -ForegroundColor Green
            Write-Host "  File size: $($fileInfo.Length) bytes" -ForegroundColor White
        } else {
            Write-Host "  File exists: NO ❌" -ForegroundColor Red
        }
        
        # Try to fetch thumbnail via API
        Write-Host "`n🌐 Testing API endpoint..." -ForegroundColor Yellow
        try {
            $response = Invoke-WebRequest -Uri "http://localhost:3000/api/v1/images/$collectionId/$($firstImage.id)/thumbnail" `
                -Headers @{ "Authorization" = "Bearer $token" } `
                -Method Get
            Write-Host "  API Response: $($response.StatusCode) ✅" -ForegroundColor Green
            Write-Host "  Content-Type: $($response.Headers['Content-Type'])" -ForegroundColor White
            Write-Host "  Content-Length: $($response.Content.Length) bytes" -ForegroundColor White
        } catch {
            Write-Host "  API Error: $($_.Exception.Message) ❌" -ForegroundColor Red
            Write-Host "  Status Code: $($_.Exception.Response.StatusCode.value__)" -ForegroundColor Red
        }
    } else {
        Write-Host "`n❌ NO matching thumbnail found!" -ForegroundColor Red
        Write-Host "Looking for imageId: $($firstImage.id)" -ForegroundColor White
        Write-Host "Available thumbnails:" -ForegroundColor White
        $collection.thumbnails | ForEach-Object {
            Write-Host "  - ImageId: $($_.imageId), Size: $($_.width)x$($_.height)" -ForegroundColor Gray
        }
    }
}

# 3. Check system settings for thumbnail size
Write-Host "`n⚙️  Checking System Settings..." -ForegroundColor Yellow
try {
    $settings = Invoke-RestMethod -Uri "http://localhost:3000/api/v1/system-settings/image-processing" `
        -Headers @{ "Authorization" = "Bearer $token" } `
        -Method Get
    
    Write-Host "  Thumbnail Size: $($settings.thumbnailSize)x$($settings.thumbnailSize)" -ForegroundColor White
    Write-Host "  Thumbnail Format: $($settings.thumbnailFormat)" -ForegroundColor White
    Write-Host "  Thumbnail Quality: $($settings.thumbnailQuality)" -ForegroundColor White
} catch {
    Write-Host "  Failed to fetch settings: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host "`n✅ Test Complete!" -ForegroundColor Cyan

