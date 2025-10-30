using ImageViewer.Domain.Interfaces;
using ImageViewer.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageViewer.Api.Controllers;

/// <summary>
/// Admin controller for system maintenance operations
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Roles = "Admin")] // ✅ Admin role required for all admin endpoints
public class AdminController : ControllerBase
{
    private readonly IMetadataRecalculationService _metadataRecalculationService;
    private readonly ICollectionIndexService _collectionIndexService;
    private readonly ICacheCleanupService _cacheCleanupService;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        IMetadataRecalculationService metadataRecalculationService,
        ICollectionIndexService collectionIndexService,
        ICacheCleanupService cacheCleanupService,
        ILogger<AdminController> logger)
    {
        _metadataRecalculationService = metadataRecalculationService;
        _collectionIndexService = collectionIndexService;
        _cacheCleanupService = cacheCleanupService;
        _logger = logger;
    }

    /// <summary>
    /// Recalculate cache folder metadata based on actual files and collection data
    /// </summary>
    [HttpPost("recalculate-cache-folder-metadata")]
    [ProducesResponseType(typeof(MetadataRecalculationResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> RecalculateCacheFolderMetadata()
    {
        try
        {
            _logger.LogInformation("🔄 Admin requested cache folder metadata recalculation");
            
            var result = await _metadataRecalculationService.RecalculateCacheFolderMetadataAsync();
            
            if (result.Success)
            {
                _logger.LogInformation("✅ Cache folder metadata recalculation completed successfully");
                return Ok(result);
            }
            else
            {
                _logger.LogWarning("⚠️ Cache folder metadata recalculation completed with errors: {ErrorCount}", result.Errors.Count);
                return Ok(result); // Still return 200 but with error details
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error during cache folder metadata recalculation");
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
        }
    }

    /// <summary>
    /// Recalculate library metadata based on collection statistics
    /// </summary>
    [HttpPost("recalculate-library-metadata")]
    [ProducesResponseType(typeof(MetadataRecalculationResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> RecalculateLibraryMetadata()
    {
        try
        {
            _logger.LogInformation("🔄 Admin requested library metadata recalculation");
            
            var result = await _metadataRecalculationService.RecalculateLibraryMetadataAsync();
            
            if (result.Success)
            {
                _logger.LogInformation("✅ Library metadata recalculation completed successfully");
                return Ok(result);
            }
            else
            {
                _logger.LogWarning("⚠️ Library metadata recalculation completed with errors: {ErrorCount}", result.Errors.Count);
                return Ok(result); // Still return 200 but with error details
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error during library metadata recalculation");
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
        }
    }

    /// <summary>
    /// Recalculate all metadata (cache folders and libraries)
    /// </summary>
    [HttpPost("recalculate-all-metadata")]
    [ProducesResponseType(typeof(MetadataRecalculationResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> RecalculateAllMetadata()
    {
        try
        {
            _logger.LogInformation("🔄 Admin requested complete metadata recalculation");
            
            var result = await _metadataRecalculationService.RecalculateAllMetadataAsync();
            
            if (result.Success)
            {
                _logger.LogInformation("✅ Complete metadata recalculation completed successfully");
                return Ok(result);
            }
            else
            {
                _logger.LogWarning("⚠️ Complete metadata recalculation completed with errors: {ErrorCount}", result.Errors.Count);
                return Ok(result); // Still return 200 but with error details
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error during complete metadata recalculation");
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
        }
    }

    /// <summary>
    /// Rebuild Redis collection index with smart modes
    /// </summary>
    [HttpPost("index/rebuild")]
    [ProducesResponseType(typeof(RebuildStatistics), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> RebuildIndex([FromBody] RebuildIndexRequest request)
    {
        try
        {
            _logger.LogInformation("Admin triggered index rebuild: Mode={Mode}, SkipThumbnails={SkipThumbnails}, DryRun={DryRun}",
                request.Mode, request.SkipThumbnailCaching, request.DryRun);
            
            var options = new RebuildOptions
            {
                SkipThumbnailCaching = request.SkipThumbnailCaching,
                DryRun = request.DryRun
            };
            
            // Use CancellationToken.None instead of HttpContext.RequestAborted
            // because this is a long-running operation that should complete 
            // even after the HTTP response is sent
            var stats = await _collectionIndexService.RebuildIndexAsync(
                request.Mode,
                options,
                CancellationToken.None);
            
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to rebuild index");
            return StatusCode(500, new { message = "Failed to rebuild index", error = ex.Message });
        }
    }
    
    /// <summary>
    /// Verify Redis index consistency and optionally fix issues
    /// </summary>
    [HttpPost("index/verify")]
    [ProducesResponseType(typeof(VerifyResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> VerifyIndex([FromBody] VerifyIndexRequest request)
    {
        try
        {
            _logger.LogInformation("Admin triggered index verification: DryRun={DryRun}", request.DryRun);
            
            // Use CancellationToken.None for long-running operations
            var result = await _collectionIndexService.VerifyIndexAsync(
                request.DryRun,
                CancellationToken.None);
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to verify index");
            return StatusCode(500, new { message = "Failed to verify index", error = ex.Message });
        }
    }
    
    /// <summary>
    /// Get collection index state for a specific collection
    /// </summary>
    [HttpGet("index/state/{collectionId}")]
    [ProducesResponseType(typeof(CollectionIndexState), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> GetCollectionIndexState(string collectionId)
    {
        try
        {
            if (!MongoDB.Bson.ObjectId.TryParse(collectionId, out var objectId))
            {
                return BadRequest(new { message = "Invalid collection ID" });
            }
            
            var state = await _collectionIndexService.GetCollectionIndexStateAsync(
                objectId,
                HttpContext.RequestAborted);
            
            if (state == null)
            {
                return NotFound(new { message = "Collection state not found in index" });
            }
            
            return Ok(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to get collection index state");
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
        }
    }

    /// <summary>
    /// Get system health and metadata status
    /// </summary>
    [HttpGet("system-health")]
    [ProducesResponseType(typeof(object), 200)]
    public async Task<IActionResult> GetSystemHealth()
    {
        try
        {
            // This could be expanded to include more system health information
            var health = new
            {
                timestamp = DateTime.UtcNow,
                status = "healthy",
                services = new
                {
                    api = "running",
                    database = "connected",
                    cache = "operational"
                },
                metadata = new
                {
                    lastRecalculation = "N/A", // Could track this in a system settings table
                    needsRecalculation = true // Could implement logic to detect this
                }
            };

            return Ok(health);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error getting system health");
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
        }
    }
    
    /// <summary>
    /// Fix archive entry paths for collections with incorrect folder structure
    /// This repairs the bug where entry paths don't include folder structure inside archives
    /// </summary>
    [HttpPost("fix-archive-entries")]
    [ProducesResponseType(typeof(ArchiveEntryFixResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> FixArchiveEntries([FromBody] FixArchiveEntriesRequest request)
    {
        try
        {
            _logger.LogInformation("Admin triggered archive entry fix: DryRun={DryRun}, Limit={Limit}, CollectionId={CollectionId}, FixMode={FixMode}, OnlyCorrupted={OnlyCorrupted}",
                request.DryRun, request.Limit, request.CollectionId ?? "null", request.FixMode ?? "All", request.OnlyCorrupted);

            var result = await _collectionIndexService.FixArchiveEntryPathsAsync(
                request.DryRun,
                request.Limit,
                request.CollectionId,
                request.FixMode,
                request.OnlyCorrupted,
                CancellationToken.None);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to fix archive entries");
            return StatusCode(500, new { message = "Failed to fix archive entries", error = ex.Message });
        }
    }

    /// <summary>
    /// Deduplicate thumbnails and cache entries for all collections
    /// 去重所有集合的缩略图和缓存 - Khử trùng lặp thumbnail và cache cho tất cả collection
    /// </summary>
    [HttpPost("dedupe-cache-thumbnails")]
    [ProducesResponseType(typeof(ImageViewer.Application.DTOs.Maintenance.DeduplicationSummaryDto), 200)]
    public async Task<IActionResult> DedupeAll()
    {
        try
        {
            _logger.LogInformation("🧹 Admin requested deduplication of thumbnails and cache entries (global)");
            var summary = await _cacheCleanupService.DeduplicateAllCollectionsAsync();
            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error during global deduplication");
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
        }
    }

    /// <summary>
    /// Deduplicate thumbnails and cache entries for a specific collection
    /// 去重特定集合的缩略图和缓存 - Khử trùng lặp thumbnail và cache cho collection cụ thể
    /// </summary>
    [HttpPost("collections/{collectionId}/dedupe-cache-thumbnails")]
    [ProducesResponseType(typeof(object), 200)]
    public async Task<IActionResult> DedupeCollection(string collectionId)
    {
        try
        {
            var objectId = MongoDB.Bson.ObjectId.Parse(collectionId);
            _logger.LogInformation("🧹 Admin requested deduplication for collection {CollectionId}", collectionId);
            var result = await _cacheCleanupService.DeduplicateCollectionAsync(objectId);
            return Ok(new {
                success = true,
                removedThumbnails = result.removedThumbnails,
                removedCacheImages = result.removedCacheImages
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error during collection deduplication for {CollectionId}", collectionId);
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
        }
    }
}

/// <summary>
/// Request for rebuilding index
/// </summary>
public class RebuildIndexRequest
{
    public RebuildMode Mode { get; set; } = RebuildMode.ChangedOnly;
    public bool SkipThumbnailCaching { get; set; } = false;
    public bool DryRun { get; set; } = false;
}

/// <summary>
/// Request for verifying index
/// </summary>
public class VerifyIndexRequest
{
    public bool DryRun { get; set; } = true;
}

/// <summary>
/// Request for fixing archive entries
/// </summary>
public class FixArchiveEntriesRequest
{
    public bool DryRun { get; set; } = true;
    public int? Limit { get; set; } = null; // Limit number of collections to process
    public string? CollectionId { get; set; } = null; // Fix specific collection by ID (for debugging)
    public string? FixMode { get; set; } = null; // "All", "DimensionsOnly", "PathsOnly"
    public bool OnlyCorrupted { get; set; } = false; // If true, only process collections with dimension issues
}
