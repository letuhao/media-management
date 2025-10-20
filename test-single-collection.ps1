# Test Single Collection Fix
# Fix archive entry for ONE specific collection (for debugging)

param(
    [string]$CollectionId = "68f2a387ff19d7b375b40cdd"  # Your collection ID
)

$token = Get-Content "admin-token.txt"
$baseUrl = "http://localhost:3000/api/v1"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Testing Archive Entry Fix - SINGLE COLLECTION" -ForegroundColor Cyan
Write-Host "Collection ID: $CollectionId" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Dry Run (Scan Only)
Write-Host "Step 1: Running DRY RUN (scan only, no changes)..." -ForegroundColor Yellow
$dryRunBody = @{
    dryRun = $true
    collectionId = $CollectionId
} | ConvertTo-Json

try {
    $dryRunResult = Invoke-RestMethod -Method Post `
        -Uri "$baseUrl/admin/fix-archive-entries" `
        -Headers @{
            "Authorization" = "Bearer $token"
            "Content-Type" = "application/json"
        } `
        -Body $dryRunBody

    Write-Host ""
    Write-Host "DRY RUN RESULTS:" -ForegroundColor Green
    Write-Host "  Collections Scanned: $($dryRunResult.totalCollectionsScanned)"
    Write-Host "  Collections with Issues: $($dryRunResult.collectionsWithIssues)" -ForegroundColor $(if ($dryRunResult.collectionsWithIssues -gt 0) { "Yellow" } else { "Green" })
    Write-Host "  Images Needing Fix: $($dryRunResult.imagesFixed)" -ForegroundColor $(if ($dryRunResult.imagesFixed -gt 0) { "Yellow" } else { "Green" })
    Write-Host "  Duration: $($dryRunResult.duration)"
    Write-Host "  Dry Run: $($dryRunResult.dryRun)"
    Write-Host ""

    if ($dryRunResult.collectionsWithIssues -eq 0) {
        Write-Host "✅ No issues found! This collection is already correct." -ForegroundColor Green
        Write-Host ""
        Write-Host "Check the API logs for details:" -ForegroundColor Yellow
        Write-Host "  Look for: 📊 Processing single collection" -ForegroundColor Gray
        Write-Host "  Look for: ✅ No issues found" -ForegroundColor Gray
        exit
    }

    # Found issues - show details
    Write-Host "⚠️  Found $($dryRunResult.imagesFixed) images needing fix!" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Check API logs for detailed information:" -ForegroundColor Cyan
    Write-Host "  Look for: 🗜️ Using filename fallback" -ForegroundColor Gray
    Write-Host "  Look for: 🗜️ Archive image needs fix" -ForegroundColor Gray
    Write-Host ""
    
    $confirm = Read-Host "Do you want to FIX these issues now? (Y/N)"

    if ($confirm -ne "Y" -and $confirm -ne "y") {
        Write-Host "Cancelled." -ForegroundColor Gray
        exit
    }

    # Step 2: Actual Fix
    Write-Host ""
    Write-Host "Step 2: Running ACTUAL FIX (will update MongoDB)..." -ForegroundColor Yellow
    $fixBody = @{
        dryRun = $false  # ✅ ACTUALLY FIX
        collectionId = $CollectionId
    } | ConvertTo-Json

    $fixResult = Invoke-RestMethod -Method Post `
        -Uri "$baseUrl/admin/fix-archive-entries" `
        -Headers @{
            "Authorization" = "Bearer $token"
            "Content-Type" = "application/json"
        } `
        -Body $fixBody

    Write-Host ""
    Write-Host "FIX RESULTS:" -ForegroundColor Green
    Write-Host "  Collections Scanned: $($fixResult.totalCollectionsScanned)"
    Write-Host "  Collections Fixed: $($fixResult.collectionsWithIssues)" -ForegroundColor Green
    Write-Host "  Images Fixed: $($fixResult.imagesFixed)" -ForegroundColor Green
    Write-Host "  Duration: $($fixResult.duration)"
    Write-Host "  Dry Run: $($fixResult.dryRun)"
    Write-Host ""

    if ($fixResult.dryRun -eq $true) {
        Write-Host "❌ ERROR: Fix was run in DRY RUN mode! No changes were made!" -ForegroundColor Red
    } else {
        Write-Host "✅ Success! MongoDB has been updated." -ForegroundColor Green
        Write-Host ""
        Write-Host "Fixed Collection ID: $($fixResult.fixedCollectionIds[0])" -ForegroundColor Cyan
        Write-Host ""
        
        Write-Host "📊 Verify in MongoDB:" -ForegroundColor Yellow
        Write-Host "  mongosh 'mongodb://localhost:27017/ImageViewerDb'" -ForegroundColor Gray
        Write-Host "  db.collections.findOne({ '_id': ObjectId('$CollectionId') }, { 'images.relativePath': 1, 'images.archiveEntry.entryName': 1 })" -ForegroundColor Gray
        Write-Host ""
        
        Write-Host "Check API logs for:" -ForegroundColor Yellow
        Write-Host "  💾 Updated archive collection" -ForegroundColor Gray
        Write-Host "  This confirms MongoDB was updated" -ForegroundColor Gray
    }

} catch {
    Write-Host ""
    Write-Host "❌ ERROR:" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ""
    Write-Host "Check:" -ForegroundColor Yellow
    Write-Host "  1. Is API running? (http://localhost:3000)" -ForegroundColor Gray
    Write-Host "  2. Is admin-token.txt correct?" -ForegroundColor Gray
    Write-Host "  3. Is collection ID correct?" -ForegroundColor Gray
    exit 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Done!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan

