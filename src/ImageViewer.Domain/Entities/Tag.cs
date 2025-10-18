using ImageViewer.Domain.ValueObjects;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

#pragma warning disable CS8618 // MongoDB entities/value objects are initialized by the driver

namespace ImageViewer.Domain.Entities;

/// <summary>
/// Tag entity - represents a tag that can be applied to collections
/// </summary>
public class Tag : BaseEntity
{
    [BsonElement("name")]
    public string Name { get; private set; }
    
    [BsonElement("description")]
    public string Description { get; private set; }
    
    [BsonElement("color")]
    public TagColor Color { get; private set; }
    
    [BsonElement("usageCount")]
    public int UsageCount { get; private set; }

    // Navigation properties
    [BsonIgnore]
    private readonly List<CollectionTag> _collectionTags = new();
    
    [BsonIgnore]
    public IReadOnlyCollection<CollectionTag> CollectionTags => _collectionTags.AsReadOnly();

    // Private constructor for EF Core
    private Tag() { }

    public Tag(string name, string description = "", TagColor? color = null)
    {
        Id = ObjectId.GenerateNewId();
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description ?? "";
        Color = color ?? TagColor.Default;
        UsageCount = 0;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be null or empty", nameof(name));

        Name = name;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateDescription(string description)
    {
        Description = description ?? "";
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateColor(TagColor color)
    {
        Color = color ?? throw new ArgumentNullException(nameof(color));
        UpdatedAt = DateTime.UtcNow;
    }

    public void IncrementUsage()
    {
        UsageCount++;
        UpdatedAt = DateTime.UtcNow;
    }

    public void DecrementUsage()
    {
        UsageCount = Math.Max(0, UsageCount - 1);
        UpdatedAt = DateTime.UtcNow;
    }

    public void AddCollectionTag(CollectionTag collectionTag)
    {
        if (collectionTag == null)
            throw new ArgumentNullException(nameof(collectionTag));

        if (_collectionTags.Any(ct => ct.CollectionId == collectionTag.CollectionId))
            throw new InvalidOperationException($"Collection '{collectionTag.CollectionId}' already has this tag");

        _collectionTags.Add(collectionTag);
        IncrementUsage();
    }

    public void RemoveCollectionTag(ObjectId collectionId)
    {
        var collectionTag = _collectionTags.FirstOrDefault(ct => ct.CollectionId == collectionId);
        if (collectionTag == null)
            throw new InvalidOperationException($"Collection '{collectionId}' does not have this tag");

        _collectionTags.Remove(collectionTag);
        DecrementUsage();
    }

    public bool IsPopular(int threshold = 10)
    {
        return UsageCount >= threshold;
    }

    public bool IsUnused()
    {
        return UsageCount == 0;
    }
}