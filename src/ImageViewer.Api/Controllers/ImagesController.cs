using ImageViewer.Api.DTOs;
using ImageViewer.Application.DTOs.Common;
using ImageViewer.Application.Extensions;
using ImageViewer.Application.Services;
using ImageViewer.Domain.Events;
using ImageViewer.Domain.Interfaces;
using ImageViewer.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace ImageViewer.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class ImagesController : ControllerBase
{
    private readonly IImageService _imageService;
    private readonly ILogger<ImagesController> _logger;
    private readonly IMessageQueueService _messageQueueService;
    private readonly ICollectionService _collectionService;
    private readonly ICacheFolderSelectionService _cacheFolderSelectionService;

    public ImagesController(
        IImageService imageService, 
        ILogger<ImagesController> logger,
        IMessageQueueService messageQueueService,
        ICollectionService collectionService,
        ICacheFolderSelectionService cacheFolderSelectionService)
    {
        _imageService = imageService;
        _logger = logger;
        _messageQueueService = messageQueueService;
        _collectionService = collectionService;
        _cacheFolderSelectionService = cacheFolderSelectionService;
    }

    /// <summary>
    /// Get random image
    /// </summary>
    [HttpGet("random")]
    public async Task<ActionResult<ImageEmbedded>> GetRandomImage()
    {
        try
        {
            _logger.LogInformation("Getting random image");
            var image = await _imageService.GetRandomEmbeddedImageAsync();
            if (image == null)
            {
                return NotFound();
            }
            return Ok(image);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting random image");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get random image within a collection
    /// </summary>
    [HttpGet("collection/{collectionId}/random")]
    public async Task<ActionResult<ImageEmbedded>> GetRandomImageByCollection(ObjectId collectionId)
    {
        try
        {
            _logger.LogInformation("Getting random image for collection {CollectionId}", collectionId);
            var image = await _imageService.GetRandomEmbeddedImageByCollectionAsync(collectionId);
            if (image == null)
            {
                return NotFound();
            }
            return Ok(image);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting random image for collection {CollectionId}", collectionId);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get images by collection ID with pagination
    /// </summary>
    [HttpGet("collection/{collectionId}")]
    public async Task<ActionResult<PaginationResponseDto<ImageEmbedded>>> GetImagesByCollection(
        ObjectId collectionId,
        [FromQuery] PaginationRequestDto pagination,
        [FromQuery] int? limit = null,
        [FromQuery] bool? filterValidOnly = false)
    {
        try
        {
            // Use limit parameter if provided, otherwise use pagination.PageSize
            if (limit.HasValue)
            {
                pagination.PageSize = limit.Value;
            }

            // Choose the appropriate service method based on filterValidOnly parameter
            var images = filterValidOnly == true 
                ? await _imageService.GetDisplayableImagesByCollectionAsync(collectionId)
                : await _imageService.GetEmbeddedImagesByCollectionAsync(collectionId);
                
            var totalCount = images.Count();
            var paginatedImages = images
                .AsQueryable()
                .ApplySorting(pagination.SortBy, pagination.SortDirection)
                .ApplyPagination(pagination);
            
            var response = paginatedImages.ToPaginationResponse(totalCount, pagination);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting images for collection {CollectionId}", collectionId);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get unified media items (images & videos) for a collection with URLs for playback/preview.
    /// </summary>
    [HttpGet("collection/{collectionId}/media")]
    public async Task<ActionResult<PaginationResponseDto<MediaItemDto>>> GetMediaByCollection(
        ObjectId collectionId,
        [FromQuery] PaginationRequestDto pagination,
        [FromQuery] int? limit = null,
        [FromQuery] bool? filterValidOnly = false)
    {
        try
        {
            if (limit.HasValue)
            {
                pagination.PageSize = limit.Value;
            }

            var images = filterValidOnly == true
                ? await _imageService.GetDisplayableImagesByCollectionAsync(collectionId)
                : await _imageService.GetEmbeddedImagesByCollectionAsync(collectionId);

            var totalCount = images.Count();

            var mediaItems = images
                .AsQueryable()
                .ApplySorting(pagination.SortBy, pagination.SortDirection)
                .ApplyPagination(pagination)
                .Select(image => CreateMediaItemDto(collectionId, image))
                .ToList();

            var response = new PaginationResponseDto<MediaItemDto>
            {
                Data = mediaItems,
                TotalCount = totalCount,
                Page = pagination.Page,
                PageSize = pagination.PageSize,
                TotalPages = (int)Math.Ceiling(totalCount / (double)pagination.PageSize),
                HasNextPage = pagination.Page * pagination.PageSize < totalCount,
                HasPreviousPage = pagination.Page > 1
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting media items for collection {CollectionId}", collectionId);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get image by ID - REMOVED: Use GET /api/v1/images/{collectionId}/{imageId} instead
    /// </summary>
    [HttpGet("{id}")]
    [Obsolete("Use GET /api/v1/images/{collectionId}/{imageId} instead")]
    public ActionResult GetImage(ObjectId id)
    {
        return BadRequest("This endpoint is deprecated. Use GET /api/v1/images/{collectionId}/{imageId} instead.");
    }

    /// <summary>
    /// Get image metadata by collection and image ID
    /// </summary>
    [HttpGet("{collectionId}/{imageId}")]
    public async Task<ActionResult<ImageEmbedded>> GetImageByCollectionAndId(ObjectId collectionId, string imageId)
    {
        try
        {
            var image = await _imageService.GetEmbeddedImageByIdAsync(imageId, collectionId);
            if (image == null)
            {
                return NotFound();
            }
            return Ok(image);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting image {ImageId} from collection {CollectionId}", imageId, collectionId);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get image file content (loads from cache, falls back to original)
    /// </summary>
    [HttpGet("{collectionId}/{imageId}/file")]
    public async Task<IActionResult> GetImageFile(ObjectId collectionId, string imageId, [FromQuery] int? width = null, [FromQuery] int? height = null)
    {
        try
        {
            var image = await _imageService.GetEmbeddedImageByIdAsync(imageId, collectionId);
            if (image == null)
            {
                return NotFound();
            }

            // Try to load from cache first
            var fileBytes = await _imageService.GetCachedImageAsync(imageId, collectionId, width, height);
            
            // Fallback to original image if cache is not available
            if (fileBytes == null)
            {
                _logger.LogWarning("Cache not found for image {ImageId}, falling back to original file and queuing cache generation", imageId);
                fileBytes = await _imageService.GetImageFileAsync(imageId, collectionId);
                
                // Queue cache generation for this image (fire and forget)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await QueueCacheGenerationAsync(collectionId, imageId, image);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to queue cache generation for image {ImageId}", imageId);
                    }
                });
            }
            else
            {
                _logger.LogDebug("Loaded image {ImageId} from cache", imageId);
            }

            if (fileBytes == null)
            {
                return NotFound("Image file not found");
            }

            var contentType = GetContentType(image.Format);
            return File(fileBytes, contentType, image.Filename);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting image file {ImageId} from collection {CollectionId}", imageId, collectionId);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get image thumbnail
    /// </summary>
    [HttpGet("{collectionId}/{imageId}/thumbnail")]
    public async Task<IActionResult> GetImageThumbnail(ObjectId collectionId, string imageId, [FromQuery] int? width = null, [FromQuery] int? height = null)
    {
        try
        {
            var image = await _imageService.GetEmbeddedImageByIdAsync(imageId, collectionId);
            if (image == null)
            {
                return NotFound();
            }

            var thumbnailBytes = await _imageService.GetThumbnailAsync(imageId, collectionId, width, height);
            if (thumbnailBytes == null)
            {
                return NotFound("Thumbnail not found");
            }

            // Use the correct content type based on the thumbnail format
            // Note: Thumbnails are always generated as static images (JPEG/WebP) regardless of original format
            var settingsService = HttpContext.RequestServices.GetRequiredService<IImageProcessingSettingsService>();
            var thumbnailFormat = await settingsService.GetThumbnailFormatAsync();
            var contentType = GetContentType(thumbnailFormat);
            return File(thumbnailBytes, contentType, $"thumb_{image.Filename}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting thumbnail for image {ImageId} from collection {CollectionId}", imageId, collectionId);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Delete image
    /// </summary>
    [HttpDelete("{collectionId}/{imageId}")]
    public async Task<IActionResult> DeleteImage(ObjectId collectionId, string imageId)
    {
        try
        {
            await _imageService.DeleteEmbeddedImageAsync(imageId, collectionId);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting image {ImageId} from collection {CollectionId}", imageId, collectionId);
            return StatusCode(500, "Internal server error");
        }
    }

    private MediaItemDto CreateMediaItemDto(ObjectId collectionId, ImageEmbedded image)
    {
        var isVideo = IsVideoFormat(image.Format);
        var collectionIdString = collectionId.ToString();

        var thumbnailUrl = Url.Action(
            nameof(GetImageThumbnail),
            values: new { collectionId = collectionIdString, imageId = image.Id });

        var mediaUrl = Url.Action(
            nameof(GetImageFile),
            values: new { collectionId = collectionIdString, imageId = image.Id });

        thumbnailUrl ??= $"/api/v1/images/{collectionIdString}/{image.Id}/thumbnail";
        mediaUrl ??= $"/api/v1/images/{collectionIdString}/{image.Id}/file";

        return new MediaItemDto
        {
            Id = image.Id,
            CollectionId = collectionIdString,
            Filename = image.Filename,
            RelativePath = image.RelativePath,
            Format = image.Format,
            FileSize = image.FileSize,
            Width = image.Width,
            Height = image.Height,
            IsVideo = isVideo,
            MediaType = isVideo ? "video" : "image",
            ThumbnailUrl = thumbnailUrl,
            MediaUrl = mediaUrl,
            Source = image
        };
    }

    private static string GetContentType(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            "webp" => "image/webp",
            "apng" => "image/apng",
            "tiff" or "tif" => "image/tiff",
            "mp4" => "video/mp4",
            "avi" => "video/x-msvideo",
            "mov" => "video/quicktime",
            "wmv" => "video/x-ms-wmv",
            "flv" => "video/x-flv",
            "mkv" => "video/x-matroska",
            "webm" => "video/webm",
            _ => "application/octet-stream"
        };
    }

    private static bool IsVideoFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return false;
        }

        return format.ToLowerInvariant() switch
        {
            "mp4" or "avi" or "mov" or "wmv" or "flv" or "mkv" or "webm" or "m4v" or "3gp" or "mpg" or "mpeg" => true,
            _ => false,
        };
    }

    /// <summary>
    /// Queue cache generation for a single image
    /// </summary>
    private async Task QueueCacheGenerationAsync(ObjectId collectionId, string imageId, ImageEmbedded image)
    {
        try
        {
            // Get collection to determine image path
            var collection = await _collectionService.GetCollectionByIdAsync(collectionId);
            if (collection == null)
            {
                _logger.LogWarning("Collection {CollectionId} not found, cannot queue cache generation", collectionId);
                return;
            }

            // Build full image path using the new DTO method
            var imagePath = Path.Combine(collection.Path, image.Filename);

            // Cache generation parameters
            const int cacheWidth = 1920;
            const int cacheHeight = 1080;
            const string format = "jpeg";

            // Get cache folder and path for this collection
            var cachePath = await _cacheFolderSelectionService.SelectCacheFolderForCacheAsync(
                collectionId, 
                imageId, 
                cacheWidth, 
                cacheHeight, 
                format);
            
            if (cachePath == null)
            {
                _logger.LogWarning("No cache folder available, cannot queue cache generation for image {ImageId}", imageId);
                return;
            }

            // Create cache generation message
            var cacheMessage = new CacheGenerationMessage
            {
                ImageId = imageId,
                CollectionId = collectionId.ToString(),
                //ImagePath = imagePath,
                // ✅ FIX: Reuse existing ArchiveEntry from image
                ArchiveEntry = image.ArchiveEntry ?? new ArchiveEntryInfo()
                {
                    ArchivePath = collection.Path,
                    EntryName = image.RelativePath,  // ✅ Use RelativePath
                    EntryPath = image.RelativePath,
                    IsDirectory = Directory.Exists(collection.Path),
                },
                CachePath = cachePath,
                CacheWidth = cacheWidth,
                CacheHeight = cacheHeight,
                Quality = 85,
                Format = format,
                PreserveOriginal = false,
                ForceRegenerate = false,
                CreatedBy = null,
                CreatedBySystem = "ImageViewer.Api.AutoCache",
                CreatedAt = DateTime.UtcNow,
                JobId = null,
                ScanJobId = null
            };

            // Publish the message to the queue
            await _messageQueueService.PublishAsync(cacheMessage);

            _logger.LogInformation("Queued cache generation for image {ImageId} in collection {CollectionId}", imageId, collectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error queuing cache generation for image {ImageId}", imageId);
            throw;
        }
    }

    /// <summary>
    /// Get cross-collection navigation for an image (next/previous with cross-collection support)
    /// </summary>
    [HttpGet("collection/{collectionId}/navigation/{imageId}")]
    public async Task<ActionResult<CrossCollectionNavigationResult>> GetCrossCollectionNavigation(
        ObjectId collectionId,
        string imageId,
        [FromQuery] string direction = "next",
        [FromQuery] string sortBy = "updatedAt",
        [FromQuery] string sortDirection = "desc")
    {
        try
        {
            if (string.IsNullOrEmpty(direction) || (direction != "next" && direction != "prev"))
            {
                return BadRequest(new { message = "Direction must be 'next' or 'prev'" });
            }

            _logger.LogDebug("Getting cross-collection navigation for image {ImageId} in collection {CollectionId}, direction: {Direction}", 
                imageId, collectionId, direction);

            var result = await _imageService.GetCrossCollectionNavigationAsync(
                imageId, 
                collectionId, 
                direction, 
                sortBy, 
                sortDirection);

            if (!result.HasTarget)
            {
                _logger.LogDebug("No navigation target found for image {ImageId} in collection {CollectionId}, direction: {Direction}", 
                    imageId, collectionId, direction);
                return NotFound(new { message = result.ErrorMessage ?? "No navigation target found" });
            }

            _logger.LogDebug("Cross-collection navigation result: TargetImage={TargetImageId}, TargetCollection={TargetCollectionId}, IsCrossCollection={IsCrossCollection}", 
                result.TargetImageId, result.TargetCollectionId, result.IsCrossCollection);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cross-collection navigation for image {ImageId} in collection {CollectionId}", imageId, collectionId);
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
        }
    }
}
