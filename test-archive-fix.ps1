# Test Archive Entry Fix
# This script will test if the fix is actually working

$token = Get-Content "admin-token.txt"
$baseUrl = "http://localhost:3000/api/v1"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Testing Archive Entry Fix" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Dry Run (Scan Only)
Write-Host "Step 1: Running DRY RUN (scan only, no changes)..." -ForegroundColor Yellow
$dryRunBody = @{
    dryRun = $true
    limit = 1  # Test with just 1 collection
} | ConvertTo-Json

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
    Write-Host "✅ No issues found! Data is already correct." -ForegroundColor Green
    Write-Host ""
    Write-Host "Try increasing the limit to scan more collections:" -ForegroundColor Yellow
    Write-Host "  Set limit to 100 or 1000" -ForegroundColor Yellow
    exit
}

# Found issues - ask to fix
Write-Host "⚠️  Found $($dryRunResult.imagesFixed) images needing fix in $($dryRunResult.collectionsWithIssues) collection(s)" -ForegroundColor Yellow
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
    limit = 1
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
    Write-Host "   This is a bug in the API - it should have set dryRun=false" -ForegroundColor Red
} else {
    Write-Host "✅ Success! MongoDB has been updated." -ForegroundColor Green
    Write-Host ""
    Write-Host "Fixed Collection IDs:" -ForegroundColor Cyan
    $fixResult.fixedCollectionIds | ForEach-Object {
        Write-Host "  - $_" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Check your API logs for detailed information:" -ForegroundColor Yellow
Write-Host "  Look for: 🗜️ Archive image needs fix" -ForegroundColor Gray
Write-Host "  Look for: 💾 Updated archive collection" -ForegroundColor Gray
Write-Host "========================================" -ForegroundColor Cyan

