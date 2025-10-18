using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

#pragma warning disable CS8618 // MongoDB value objects are initialized by the driver

namespace ImageViewer.Domain.ValueObjects;

/// <summary>
/// Embedded cache image value object for MongoDB collections
/// Stores generated cache files separately from source images
/// </summary>
public class CacheImageEmbedded
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; private set; } = ObjectId.GenerateNewId().ToString();
    
    [BsonElement("imageId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ImageId { get; private set; }
    
    [BsonElement("cachePath")]
    public string CachePath { get; private set; }
    
    [BsonElement("width")]
    public int Width { get; private set; }
    
    [BsonElement("height")]
    public int Height { get; private set; }
    
    [BsonElement("fileSize")]
    public long FileSize { get; private set; }
    
    [BsonElement("format")]
    public string Format { get; private set; }
    
    [BsonElement("quality")]
    public int Quality { get; private set; }
    
    [BsonElement("isGenerated")]
    public bool IsGenerated { get; private set; }
    
    [BsonElement("generatedAt")]
    public DateTime? GeneratedAt { get; private set; }
    
    [BsonElement("lastAccessed")]
    public DateTime? LastAccessed { get; private set; }
    
    [BsonElement("accessCount")]
    public int AccessCount { get; private set; }
    
    [BsonElement("isValid")]
    public bool IsValid { get; private set; }
    
    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; private set; }
    
    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; private set; }
    
    // Error tracking fields
    [BsonElement("isDummy")]
    public bool IsDummy { get; private set; }
    
    [BsonElement("errorMessage")]
    public string? ErrorMessage { get; private set; }
    
    [BsonElement("errorType")]
    public string? ErrorType { get; private set; }
    
    [BsonElement("failedAt")]
    public DateTime? FailedAt { get; private set; }

    // Private constructor for MongoDB
    private CacheImageEmbedded() { }

    public CacheImageEmbedded(string imageId, string cachePath, int width, int height, 
        long fileSize, string format, int quality = 85)
    {
        ImageId = imageId ?? throw new ArgumentNullException(nameof(imageId));
        CachePath = cachePath ?? throw new ArgumentNullException(nameof(cachePath));
        Width = width;
        Height = height;
        FileSize = fileSize;
        Format = format ?? throw new ArgumentNullException(nameof(format));
        Quality = quality;
        IsGenerated = true;
        GeneratedAt = DateTime.UtcNow;
        AccessCount = 0;
        IsValid = true;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateAccess()
    {
        LastAccessed = DateTime.UtcNow;
        AccessCount++;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateCacheInfo(string cachePath, int width, int height, long fileSize)
    {
        CachePath = cachePath;
        Width = width;
        Height = height;
        FileSize = fileSize;
        GeneratedAt = DateTime.UtcNow;
        IsValid = true;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkAsInvalid()
    {
        IsValid = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkAsValid()
    {
        IsValid = true;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateQuality(int quality)
    {
        Quality = quality;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Create a dummy cache entry for failed processing
    /// </summary>
    public static CacheImageEmbedded CreateDummy(string imageId, string errorMessage, string errorType)
    {
        var dummy = new CacheImageEmbedded();
        dummy.ImageId = imageId ?? throw new ArgumentNullException(nameof(imageId));
        dummy.CachePath = ""; // Empty path for dummy
        dummy.Width = 0;
        dummy.Height = 0;
        dummy.FileSize = 0;
        dummy.Format = "unknown";
        dummy.Quality = 0;
        dummy.IsGenerated = false;
        dummy.GeneratedAt = null;
        dummy.AccessCount = 0;
        dummy.IsValid = false;
        dummy.IsDummy = true;
        dummy.ErrorMessage = errorMessage;
        dummy.ErrorType = errorType;
        dummy.FailedAt = DateTime.UtcNow;
        dummy.CreatedAt = DateTime.UtcNow;
        dummy.UpdatedAt = DateTime.UtcNow;
        return dummy;
    }

    /// <summary>
    /// Mark cache image as failed with error details
    /// </summary>
    public void MarkAsFailed(string errorMessage, string errorType)
    {
        IsDummy = true;
        IsValid = false;
        IsGenerated = false;
        ErrorMessage = errorMessage;
        ErrorType = errorType;
        FailedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}

