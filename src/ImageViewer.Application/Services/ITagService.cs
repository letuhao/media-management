using ImageViewer.Application.DTOs.Tags;
using MongoDB.Bson;

namespace ImageViewer.Application.Services;

/// <summary>
/// Tag service interface for managing tags operations
/// </summary>
public interface ITagService
{
    /// <summary>
    /// Get collection tags
    /// </summary>
    Task<IEnumerable<CollectionTagDto>> GetCollectionTagsAsync(ObjectId collectionId);

    /// <summary>
    /// Add tag to collection
    /// </summary>
    Task<CollectionTagDto> AddTagToCollectionAsync(ObjectId collectionId, AddTagToCollectionDto dto);

    /// <summary>
    /// Remove tag from collection
    /// </summary>
    Task RemoveTagFromCollectionAsync(ObjectId collectionId, string tagName);

    /// <summary>
    /// Get all tags
    /// </summary>
    Task<IEnumerable<TagDto>> GetAllTagsAsync();

    /// <summary>
    /// Get tag by ID
    /// </summary>
    Task<TagDto> GetTagAsync(ObjectId tagId);

    /// <summary>
    /// Create new tag
    /// </summary>
    Task<TagDto> CreateTagAsync(CreateTagDto dto);

    /// <summary>
    /// Update tag
    /// </summary>
    Task<TagDto> UpdateTagAsync(ObjectId tagId, UpdateTagDto dto);

    /// <summary>
    /// Delete tag
    /// </summary>
    Task DeleteTagAsync(ObjectId tagId);

    /// <summary>
    /// Get tag statistics
    /// </summary>
    Task<TagStatisticsDto> GetTagStatisticsAsync();

    /// <summary>
    /// Search tags
    /// </summary>
    Task<IEnumerable<TagDto>> SearchTagsAsync(string query, int limit = 20);

    /// <summary>
    /// Get popular tags
    /// </summary>
    Task<IEnumerable<PopularTagDto>> GetPopularTagsAsync(int limit = 20);

    /// <summary>
    /// Get tag suggestions for collection
    /// </summary>
    Task<IEnumerable<TagSuggestionDto>> GetTagSuggestionsAsync(ObjectId collectionId, int limit = 10);

    /// <summary>
    /// Bulk add tags to collection
    /// </summary>
    Task<IEnumerable<CollectionTagDto>> BulkAddTagsToCollectionAsync(ObjectId collectionId, IEnumerable<string> tagNames);

    /// <summary>
    /// Bulk remove tags from collection
    /// </summary>
    Task BulkRemoveTagsFromCollectionAsync(ObjectId collectionId, IEnumerable<string> tagNames);
}
