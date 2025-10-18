using Microsoft.Extensions.Logging;
using ImageViewer.Domain.Interfaces;
using ImageViewer.Domain.ValueObjects;
using ImageViewer.Application.Options;
using Microsoft.Extensions.Options;
using MongoDB.Bson;

namespace ImageViewer.Application.Services;

/// <summary>
/// Image service implementation - Updated for embedded design
/// </summary>
public class ImageService : IImageService
{
    private readonly ICollectionRepository _collectionRepository;
    private readonly IImageProcessingService _imageProcessingService;
    private readonly ICacheService _cacheService;
    private readonly ILogger<ImageService> _logger;
    private readonly ImageSizeOptions _sizeOptions;
    private readonly ICollectionService _collectionService;

    public ImageService(
        ICollectionRepository collectionRepository,
        IImageProcessingService imageProcessingService,
        ICacheService cacheService,
        ILogger<ImageService> logger,
        IOptions<ImageSizeOptions> sizeOptions,
        ICollectionService collectionService)
    {
        _collectionRepository = collectionRepository ?? throw new ArgumentNullException(nameof(collectionRepository));
        _collectionService = collectionService ?? throw new ArgumentNullException(nameof(collectionService));
        _imageProcessingService = imageProcessingService ?? throw new ArgumentNullException(nameof(imageProcessingService));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sizeOptions = sizeOptions?.Value ?? new ImageSizeOptions();
    }

    #region Embedded Image Operations

    public async Task<ImageEmbedded?> GetEmbeddedImageByIdAsync(string imageId, ObjectId collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting embedded image by ID {ImageId} from collection {CollectionId}", imageId, collectionId);
            
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                _logger.LogWarning("Collection {CollectionId} not found", collectionId);
                return null;
            }

            return collection.GetImage(imageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting embedded image by ID {ImageId} from collection {CollectionId}", imageId, collectionId);
            throw;
        }
    }

    public async Task<ImageEmbedded?> GetEmbeddedImageByFilenameAsync(string filename, ObjectId collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting embedded image by filename {Filename} from collection {CollectionId}", filename, collectionId);
            
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                _logger.LogWarning("Collection {CollectionId} not found", collectionId);
                return null;
            }

            return collection.GetActiveImages().FirstOrDefault(i => i.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting embedded image by filename {Filename} from collection {CollectionId}", filename, collectionId);
            throw;
        }
    }

    public async Task<IEnumerable<ImageEmbedded>> GetEmbeddedImagesByCollectionAsync(ObjectId collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting embedded images from collection {CollectionId}", collectionId);
            
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                _logger.LogWarning("Collection {CollectionId} not found", collectionId);
                return Enumerable.Empty<ImageEmbedded>();
            }

            return collection.GetActiveImages();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting embedded images from collection {CollectionId}", collectionId);
            throw;
        }
    }

    public async Task<IEnumerable<ImageEmbedded>> GetDisplayableImagesByCollectionAsync(ObjectId collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting displayable images from collection {CollectionId}", collectionId);
            
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                _logger.LogWarning("Collection {CollectionId} not found", collectionId);
                return Enumerable.Empty<ImageEmbedded>();
            }

            return collection.GetDisplayableImages();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting displayable images from collection {CollectionId}", collectionId);
            throw;
        }
    }

    public async Task<IEnumerable<ImageEmbedded>> GetEmbeddedImagesByFormatAsync(string format, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting embedded images by format {Format}", format);
            
            var collections = await _collectionRepository.GetAllAsync();
            var result = new List<ImageEmbedded>();

            foreach (var collection in collections)
            {
                var images = collection.GetActiveImages().Where(i => i.Format.Equals(format, StringComparison.OrdinalIgnoreCase));
                result.AddRange(images);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting embedded images by format {Format}", format);
            throw;
        }
    }

    public async Task<IEnumerable<ImageEmbedded>> GetEmbeddedImagesBySizeRangeAsync(int minWidth, int minHeight, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting embedded images by size range {MinWidth}x{MinHeight}", minWidth, minHeight);
            
            var collections = await _collectionRepository.GetAllAsync();
            var result = new List<ImageEmbedded>();

            foreach (var collection in collections)
            {
                var images = collection.GetActiveImages().Where(i => i.Width >= minWidth && i.Height >= minHeight);
                result.AddRange(images);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting embedded images by size range {MinWidth}x{MinHeight}", minWidth, minHeight);
            throw;
        }
    }

    public async Task<IEnumerable<ImageEmbedded>> GetLargeEmbeddedImagesAsync(long minSizeBytes, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting large embedded images with minimum size {MinSizeBytes}", minSizeBytes);
            
            var collections = await _collectionRepository.GetAllAsync();
            var result = new List<ImageEmbedded>();

            foreach (var collection in collections)
            {
                var images = collection.GetActiveImages().Where(i => i.FileSize >= minSizeBytes);
                result.AddRange(images);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting large embedded images with minimum size {MinSizeBytes}", minSizeBytes);
            throw;
        }
    }

    public async Task<IEnumerable<ImageEmbedded>> GetHighResolutionEmbeddedImagesAsync(int minWidth, int minHeight, CancellationToken cancellationToken = default)
    {
        return await GetEmbeddedImagesBySizeRangeAsync(minWidth, minHeight, cancellationToken);
    }

    #endregion

    #region Random and Navigation Operations

    public async Task<ImageEmbedded?> GetRandomEmbeddedImageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting random embedded image");
            
            var collections = await _collectionRepository.GetAllAsync();
            var allImages = new List<ImageEmbedded>();

            foreach (var collection in collections)
            {
                allImages.AddRange(collection.GetActiveImages());
            }

            if (!allImages.Any())
                return null;

            var random = new Random();
            return allImages[random.Next(allImages.Count)];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting random embedded image");
            throw;
        }
    }

    public async Task<ImageEmbedded?> GetRandomEmbeddedImageByCollectionAsync(ObjectId collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting random embedded image from collection {CollectionId}", collectionId);
            
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                _logger.LogWarning("Collection {CollectionId} not found", collectionId);
                return null;
            }

            var images = collection.GetActiveImages().ToList();
            if (!images.Any())
                return null;

            var random = new Random();
            return images[random.Next(images.Count)];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting random embedded image from collection {CollectionId}", collectionId);
            throw;
        }
    }

    public async Task<ImageEmbedded?> GetNextEmbeddedImageAsync(string currentImageId, ObjectId collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting next embedded image after {ImageId} in collection {CollectionId}", currentImageId, collectionId);
            
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                _logger.LogWarning("Collection {CollectionId} not found", collectionId);
                return null;
            }

            var images = collection.GetActiveImages().ToList();
            var currentIndex = images.FindIndex(i => i.Id == currentImageId);
            
            if (currentIndex == -1 || currentIndex >= images.Count - 1)
                return null;

            return images[currentIndex + 1];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting next embedded image after {ImageId} in collection {CollectionId}", currentImageId, collectionId);
            throw;
        }
    }

    public async Task<ImageEmbedded?> GetPreviousEmbeddedImageAsync(string currentImageId, ObjectId collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting previous embedded image before {ImageId} in collection {CollectionId}", currentImageId, collectionId);
            
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                _logger.LogWarning("Collection {CollectionId} not found", collectionId);
                return null;
            }

            var images = collection.GetActiveImages().ToList();
            var currentIndex = images.FindIndex(i => i.Id == currentImageId);
            
            if (currentIndex <= 0)
                return null;

            return images[currentIndex - 1];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting previous embedded image before {ImageId} in collection {CollectionId}", currentImageId, collectionId);
            throw;
        }
    }

    public async Task<CrossCollectionNavigationResult> GetCrossCollectionNavigationAsync(string currentImageId, ObjectId collectionId, string direction, string sortBy = "updatedAt", string sortDirection = "desc", CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting cross-collection navigation for image {ImageId} in collection {CollectionId}, direction: {Direction}", 
                currentImageId, collectionId, direction);

            var result = new CrossCollectionNavigationResult();

            // Get current collection
            var currentCollection = await _collectionRepository.GetByIdAsync(collectionId);
            if (currentCollection == null)
            {
                result.ErrorMessage = "Current collection not found";
                return result;
            }

            var currentImages = currentCollection.GetActiveImages().ToList();
            var currentIndex = currentImages.FindIndex(i => i.Id == currentImageId);
            
            if (currentIndex == -1)
            {
                result.ErrorMessage = "Current image not found in collection";
                return result;
            }

            // Check if we can navigate within current collection
            if (direction == "next")
            {
                if (currentIndex < currentImages.Count - 1)
                {
                    // Stay in current collection
                    var nextImage = currentImages[currentIndex + 1];
                    result.TargetImageId = nextImage.Id;
                    result.TargetCollectionId = collectionId.ToString();
                    result.IsCrossCollection = false;
                    result.TargetCollectionName = currentCollection.Name;
                    result.TargetImagePosition = currentIndex + 1;
                    result.TotalImagesInTargetCollection = currentImages.Count;
                    result.HasTarget = true;
                    return result;
                }
            }
            else if (direction == "prev")
            {
                if (currentIndex > 0)
                {
                    // Stay in current collection
                    var prevImage = currentImages[currentIndex - 1];
                    result.TargetImageId = prevImage.Id;
                    result.TargetCollectionId = collectionId.ToString();
                    result.IsCrossCollection = false;
                    result.TargetCollectionName = currentCollection.Name;
                    result.TargetImagePosition = currentIndex - 1;
                    result.TotalImagesInTargetCollection = currentImages.Count;
                    result.HasTarget = true;
                    return result;
                }
            }

            // Need to cross collections - get collection navigation info
            var collectionNav = await _collectionService.GetCollectionNavigationAsync(collectionId, sortBy, sortDirection);
            
            if (direction == "next")
            {
                if (collectionNav.HasNext && collectionNav.NextCollectionId != null)
                {
                    // Navigate to first image of next collection
                    var nextCollectionId = ObjectId.Parse(collectionNav.NextCollectionId);
                    var nextCollection = await _collectionRepository.GetByIdAsync(nextCollectionId);
                    
                    if (nextCollection != null)
                    {
                        var nextImages = nextCollection.GetActiveImages().ToList();
                        if (nextImages.Any())
                        {
                            var firstImage = nextImages[0];
                            result.TargetImageId = firstImage.Id;
                            result.TargetCollectionId = nextCollectionId.ToString();
                            result.IsCrossCollection = true;
                            result.TargetCollectionName = nextCollection.Name;
                            result.TargetImagePosition = 0;
                            result.TotalImagesInTargetCollection = nextImages.Count;
                            result.HasTarget = true;
                            return result;
                        }
                    }
                }
            }
            else if (direction == "prev")
            {
                if (collectionNav.HasPrevious && collectionNav.PreviousCollectionId != null)
                {
                    // Navigate to last image of previous collection
                    var prevCollectionId = ObjectId.Parse(collectionNav.PreviousCollectionId);
                    var prevCollection = await _collectionRepository.GetByIdAsync(prevCollectionId);
                    
                    if (prevCollection != null)
                    {
                        var prevImages = prevCollection.GetActiveImages().ToList();
                        if (prevImages.Any())
                        {
                            var lastImage = prevImages[prevImages.Count - 1];
                            result.TargetImageId = lastImage.Id;
                            result.TargetCollectionId = prevCollectionId.ToString();
                            result.IsCrossCollection = true;
                            result.TargetCollectionName = prevCollection.Name;
                            result.TargetImagePosition = prevImages.Count - 1;
                            result.TotalImagesInTargetCollection = prevImages.Count;
                            result.HasTarget = true;
                            return result;
                        }
                    }
                }
            }

            // No navigation possible
            result.HasTarget = false;
            result.ErrorMessage = $"Cannot navigate {direction} - no more collections available";
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cross-collection navigation for image {ImageId} in collection {CollectionId}", 
                currentImageId, collectionId);
            return new CrossCollectionNavigationResult 
            { 
                HasTarget = false, 
                ErrorMessage = $"Navigation error: {ex.Message}" 
            };
        }
    }

    #endregion

    #region File Operations

    public async Task<byte[]?> GetImageFileAsync(string imageId, ObjectId collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting image file for {ImageId} from collection {CollectionId}", imageId, collectionId);

            var image = await GetEmbeddedImageByIdAsync(imageId, collectionId, cancellationToken);
            if (image == null)
            {
                _logger.LogWarning("Image {ImageId} not found in collection {CollectionId}", imageId, collectionId);
                return null;
            }

            // Get the collection to find the full path
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                _logger.LogWarning("Collection {CollectionId} not found", collectionId);
                return null;
            }

            //var fullPath = image.GetFullPath(collection.Path);
            var archiveEntry = new ArchiveEntryInfo()
            {
                ArchivePath = collection.Path,
                EntryName = image.Filename,
                IsDirectory = Directory.Exists(collection.Path),
            };
            
            // For archive entries, skip File.Exists check
            if (archiveEntry.IsDirectory && !File.Exists(archiveEntry.GetPhysicalFileFullPath()))
            {
                _logger.LogWarning("Image file does not exist: {FullPath}", archiveEntry.GetPhysicalFileFullPath());
                return null;
            }

            return await File.ReadAllBytesAsync(archiveEntry.GetPhysicalFileFullPath(), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting image file for {ImageId} from collection {CollectionId}", imageId, collectionId);
            throw;
        }
    }

    public async Task<byte[]?> GetThumbnailAsync(string imageId, ObjectId collectionId, int? width = null, int? height = null, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting thumbnail for {ImageId} from collection {CollectionId}", imageId, collectionId);

            var thumbnailInfo = await GetThumbnailInfoAsync(imageId, collectionId, width, height, cancellationToken);
            if (thumbnailInfo == null)
            {
                _logger.LogWarning("Thumbnail info not found for {ImageId} in collection {CollectionId}", imageId, collectionId);
                return null;
            }

            if (!File.Exists(thumbnailInfo.ThumbnailPath))
            {
                _logger.LogWarning("Thumbnail file does not exist: {ThumbnailPath}", thumbnailInfo.ThumbnailPath);
                return null;
            }

            return await File.ReadAllBytesAsync(thumbnailInfo.ThumbnailPath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting thumbnail for {ImageId} from collection {CollectionId}", imageId, collectionId);
            throw;
        }
    }

    public async Task<ThumbnailEmbedded?> GetThumbnailInfoAsync(string imageId, ObjectId collectionId, int? width = null, int? height = null, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting thumbnail info for {ImageId} from collection {CollectionId}", imageId, collectionId);
            
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                _logger.LogWarning("Collection {CollectionId} not found", collectionId);
                return null;
            }

            var targetWidth = width ?? _sizeOptions.ThumbnailWidth;
            var targetHeight = height ?? _sizeOptions.ThumbnailHeight;

            return collection.GetThumbnailForImage(imageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting thumbnail info for {ImageId} from collection {CollectionId}", imageId, collectionId);
            throw;
        }
    }

    public async Task<byte[]?> GetCachedImageAsync(string imageId, ObjectId collectionId, int? width = null, int? height = null, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting cached image for {ImageId} from collection {CollectionId}", imageId, collectionId);

            // Get collection to check cache images
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                _logger.LogWarning("Collection {CollectionId} not found", collectionId);
                return null;
            }

            // Find cache image for this image ID
            var cacheImage = collection.GetCacheImageForImage(imageId);
            if (cacheImage == null)
            {
                _logger.LogDebug("No cache image found for {ImageId} in collection {CollectionId}", imageId, collectionId);
                return null;
            }

            // Check if cache file exists on disk
            if (!File.Exists(cacheImage.CachePath))
            {
                _logger.LogWarning("Cache file does not exist: {CachePath} for image {ImageId}", cacheImage.CachePath, imageId);
                return null;
            }

            _logger.LogDebug("Loading cached image from {CachePath}", cacheImage.CachePath);
            return await File.ReadAllBytesAsync(cacheImage.CachePath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cached image for {ImageId} from collection {CollectionId}", imageId, collectionId);
            throw;
        }
    }

    #endregion

    #region CRUD Operations on Embedded Images

    public async Task<ImageEmbedded> CreateEmbeddedImageAsync(ObjectId collectionId, string filename, string relativePath, long fileSize, int width, int height, string format, ArchiveEntryInfo? archiveEntry = null, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Creating embedded image {Filename} in collection {CollectionId}", filename, collectionId);
            
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                throw new InvalidOperationException($"Collection {collectionId} not found");
            }

            // Check if image already exists (prevent duplicates from double-scans)
            var existingImage = collection.Images?.FirstOrDefault(img => 
                img.Filename == filename && img.RelativePath == relativePath);
            
            if (existingImage != null)
            {
                _logger.LogInformation("⚠️ Image {Filename} already exists in collection {CollectionId} with ID {ExistingId}, skipping duplicate creation", 
                    filename, collectionId, existingImage.Id);
                return existingImage;
            }

            var embeddedImage = new ImageEmbedded(filename, relativePath, archiveEntry, fileSize, width, height, format);
            
            // Atomically add image to collection (thread-safe, prevents race conditions!)
            var added = await _collectionRepository.AtomicAddImageAsync(collectionId, embeddedImage);
            if (!added)
            {
                throw new InvalidOperationException($"Failed to add image to collection {collectionId} - collection might not exist");
            }
            
            _logger.LogInformation("Created embedded image {ImageId} in collection {CollectionId}", embeddedImage.Id, collectionId);
            return embeddedImage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating embedded image {Filename} in collection {CollectionId}", filename, collectionId);
            throw;
        }
    }

    public async Task UpdateEmbeddedImageMetadataAsync(string imageId, ObjectId collectionId, int width, int height, long fileSize, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Updating embedded image metadata {ImageId} in collection {CollectionId}", imageId, collectionId);
            
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                throw new InvalidOperationException($"Collection {collectionId} not found");
            }

            collection.UpdateImageMetadata(imageId, width, height, fileSize);
            await _collectionRepository.UpdateAsync(collection);
            
            _logger.LogInformation("Updated embedded image metadata {ImageId} in collection {CollectionId}", imageId, collectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating embedded image metadata {ImageId} in collection {CollectionId}", imageId, collectionId);
            throw;
        }
    }

    public async Task DeleteEmbeddedImageAsync(string imageId, ObjectId collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Deleting embedded image {ImageId} from collection {CollectionId}", imageId, collectionId);

            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                throw new InvalidOperationException($"Collection {collectionId} not found");
            }

            collection.RemoveImage(imageId);
            await _collectionRepository.UpdateAsync(collection);

            _logger.LogInformation("Deleted embedded image {ImageId} from collection {CollectionId}", imageId, collectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting embedded image {ImageId} from collection {CollectionId}", imageId, collectionId);
            throw;
        }
    }

    public async Task RestoreEmbeddedImageAsync(string imageId, ObjectId collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Restoring embedded image {ImageId} in collection {CollectionId}", imageId, collectionId);

            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                throw new InvalidOperationException($"Collection {collectionId} not found");
            }

            collection.RestoreImage(imageId);
            await _collectionRepository.UpdateAsync(collection);

            _logger.LogInformation("Restored embedded image {ImageId} in collection {CollectionId}", imageId, collectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restoring embedded image {ImageId} in collection {CollectionId}", imageId, collectionId);
            throw;
        }
    }

    #endregion

    #region Statistics

    public async Task<long> GetTotalSizeByCollectionAsync(ObjectId collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting total size for collection {CollectionId}", collectionId);
            
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                _logger.LogWarning("Collection {CollectionId} not found", collectionId);
                return 0;
            }

            return collection.GetActiveImages().Sum(i => i.FileSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting total size for collection {CollectionId}", collectionId);
            throw;
        }
    }

    public async Task<int> GetCountByCollectionAsync(ObjectId collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting count for collection {CollectionId}", collectionId);
            
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                _logger.LogWarning("Collection {CollectionId} not found", collectionId);
                return 0;
            }

            return collection.GetActiveImages().Count();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting count for collection {CollectionId}", collectionId);
            throw;
        }
    }

    #endregion

    #region Thumbnail and Cache Operations

    public async Task<ThumbnailEmbedded> GenerateThumbnailAsync(string imageId, ObjectId collectionId, int width, int height, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Generating thumbnail for {ImageId} in collection {CollectionId}", imageId, collectionId);
            
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                throw new InvalidOperationException($"Collection {collectionId} not found");
            }

            var image = collection.GetImage(imageId);
            if (image == null)
            {
                throw new InvalidOperationException($"Image {imageId} not found in collection {collectionId}");
            }

            // Get the full image path using the new DTO method
            var fullImagePath = $"{collection.Path}#{image.Filename}";
            
            // Check if this is an archive entry using the new DTO method
            bool isArchiveEntry = image.IsArchiveEntry();
            
            // For archive entries, we don't check File.Exists because the path format is "archive.zip::entry.png"
            if (!isArchiveEntry)
            {
                fullImagePath = Path.Combine(collection.Path, image.Filename);
                if (!File.Exists(fullImagePath))
                    throw new InvalidOperationException($"Image file does not exist: {fullImagePath}");
            }

            // Generate thumbnail using image processing service
            // Note: Archive entry thumbnail generation should be handled by ThumbnailGenerationConsumer
            if (isArchiveEntry)
            {
                _logger.LogDebug("Processing thumbnail for archive entry: {Path}", fullImagePath);
                throw new InvalidOperationException($"Archive entry thumbnail generation should be handled by ThumbnailGenerationConsumer, not ImageService: {fullImagePath}");
            }
            
            var thumbnailData = await _imageProcessingService.GenerateThumbnailAsync(new ArchiveEntryInfo() 
            {
                ArchivePath = collection.Path,
                EntryName = image.Filename,
                IsDirectory = !isArchiveEntry,
            }, width, height, "jpeg", 95, cancellationToken);
            
            // Determine thumbnail path using cache service
            var cacheFolders = await _cacheService.GetCacheFoldersAsync();
            var cacheFoldersList = cacheFolders.ToList();
            
            if (cacheFoldersList.Count == 0)
            {
                throw new InvalidOperationException("No cache folders available");
            }
            
            // Use hash-based distribution to select cache folder
            var hash = collectionId.GetHashCode();
            var selectedIndex = Math.Abs(hash) % cacheFoldersList.Count;
            var selectedCacheFolder = cacheFoldersList[selectedIndex];
            
            // Create proper folder structure: CacheFolder/thumbnails/CollectionId/ImageFileName_WidthxHeight.ext
            var collectionIdStr = collectionId.ToString();
            var thumbnailDir = Path.Combine(selectedCacheFolder.Path, "thumbnails", collectionIdStr);
            var thumbnailFileName = $"{Path.GetFileNameWithoutExtension(image.Filename)}_{width}x{height}{Path.GetExtension(image.Filename)}";
            var thumbnailPath = Path.Combine(thumbnailDir, thumbnailFileName);
            
            // Ensure thumbnail directory exists
            Directory.CreateDirectory(thumbnailDir);
            
            // Save thumbnail file
            await File.WriteAllBytesAsync(thumbnailPath, thumbnailData, cancellationToken);
            
            // Create thumbnail info
            var thumbnailInfo = new ThumbnailEmbedded(imageId, thumbnailPath, width, height, thumbnailData.Length, Path.GetExtension(image.Filename), 95);
            
            // Add thumbnail to collection
            collection.AddThumbnail(thumbnailInfo);
            await _collectionRepository.UpdateAsync(collection);
            
            _logger.LogInformation("Generated thumbnail for {ImageId} in collection {CollectionId}: {ThumbnailPath}", imageId, collectionId, thumbnailPath);
            return thumbnailInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating thumbnail for {ImageId} in collection {CollectionId}", imageId, collectionId);
            throw;
        }
    }

    public async Task<ImageCacheInfoEmbedded> GenerateCacheAsync(string imageId, ObjectId collectionId, int width, int height, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Generating cache for {ImageId} in collection {CollectionId}", imageId, collectionId);
            
            var collection = await _collectionRepository.GetByIdAsync(collectionId);
            if (collection == null)
            {
                throw new InvalidOperationException($"Collection {collectionId} not found");
            }

            var image = collection.GetImage(imageId);
            if (image == null)
            {
                throw new InvalidOperationException($"Image {imageId} not found in collection {collectionId}");
            }

            // Get the full image path using the new DTO method
            var fullImagePath = $"{collection.Path}#{image.Filename}";

            // Check if this is an archive entry using the new DTO method
            bool isArchiveEntry = image.IsArchiveEntry();
            
            // For archive entries, we don't check File.Exists because the path format is "archive.zip::entry.png"
            if (!isArchiveEntry)
            {
                fullImagePath = Path.Combine(collection.Path, image.Filename);
                if (!File.Exists(fullImagePath))
                    throw new InvalidOperationException($"Image file does not exist: {fullImagePath}");
            }

            // Generate cache using image processing service
            byte[] cacheData;
            if (isArchiveEntry)
            {
                // For archive entries, we need to extract and process from bytes
                // The fullImagePath is in format "archive.zip::entry.png"
                _logger.LogDebug("Processing cache for archive entry: {Path}", fullImagePath);
                
                // Note: The actual archive extraction is handled by the consumer
                // This path should have already been validated by the consumer
                // For now, we'll just note that archive processing happens in the consumer
                throw new InvalidOperationException($"Archive entry caching should be handled by CacheGenerationConsumer, not ImageService: {fullImagePath}");
            }
            else
            {
                cacheData = await _imageProcessingService.ResizeImageAsync(new ArchiveEntryInfo()
                {
                    ArchivePath = collection.Path,
                    EntryName = image.Filename,
                    IsDirectory = !isArchiveEntry,
                }, width, height, "jpeg", 95, cancellationToken);
            }
            
            // Determine cache path using cache service
            var cacheFolders = await _cacheService.GetCacheFoldersAsync();
            var cacheFoldersList = cacheFolders.ToList();
            
            if (cacheFoldersList.Count == 0)
            {
                throw new InvalidOperationException("No cache folders available");
            }
            
            // Use hash-based distribution to select cache folder
            var hash = collectionId.GetHashCode();
            var selectedIndex = Math.Abs(hash) % cacheFoldersList.Count;
            var selectedCacheFolder = cacheFoldersList[selectedIndex];
            
            // Create proper folder structure: CacheFolder/cache/CollectionId/ImageId_CacheWidthxCacheHeight.jpg
            var collectionIdStr = collectionId.ToString();
            var cacheDir = Path.Combine(selectedCacheFolder.Path, "cache", collectionIdStr);
            var fileName = $"{imageId}_cache_{width}x{height}.jpg";
            var cachePath = Path.Combine(cacheDir, fileName);
            
            // Ensure cache directory exists
            Directory.CreateDirectory(cacheDir);
            
            // Save cache file
            await File.WriteAllBytesAsync(cachePath, cacheData, cancellationToken);
            
            // Create cache info
            var cacheInfo = new ImageCacheInfoEmbedded(cachePath, cacheData.Length, ".jpg", width, height, 95);
            
            // Update image with cache info
            image.SetCacheInfo(cacheInfo);
            await _collectionRepository.UpdateAsync(collection);
            
            _logger.LogInformation("Generated cache for {ImageId} in collection {CollectionId}: {CachePath}", imageId, collectionId, cachePath);
            return cacheInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating cache for {ImageId} in collection {CollectionId}", imageId, collectionId);
            throw;
        }
    }

    public async Task CleanupExpiredThumbnailsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Cleaning up expired thumbnails");
            
            var collections = await _collectionRepository.GetAllAsync();
            var cleanedCount = 0;

            foreach (var collection in collections)
            {
                var invalidThumbnails = collection.GetInvalidThumbnails();
                if (invalidThumbnails.Any())
                {
                    collection.CleanupInvalidThumbnails();
                    await _collectionRepository.UpdateAsync(collection);
                    cleanedCount += invalidThumbnails.Count();
                }
            }

            _logger.LogInformation("Cleaned up {CleanedCount} expired thumbnails", cleanedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up expired thumbnails");
            throw;
        }
    }

    #endregion

}