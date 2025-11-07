using ImageViewer.Domain.ValueObjects;

namespace ImageViewer.Api.DTOs;

/// <summary>
/// Represents a media item (either image or video) exposed to the client.
/// </summary>
public class MediaItemDto
{
    public string Id { get; set; } = string.Empty;
    public string CollectionId { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsVideo { get; set; }
    public string MediaType { get; set; } = "image"; // "image" or "video"
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string MediaUrl { get; set; } = string.Empty;
    public ImageEmbedded Source { get; set; } = null!; // For clients needing raw embedded data
}

