using MongoDB.Bson.Serialization.Attributes;

namespace ImageViewer.Domain.ValueObjects;

/// <summary>
/// Collection statistics value object
/// </summary>
public class CollectionStatistics
{
    [BsonElement("totalItems")]
    public long TotalItems { get; private set; }
    
    [BsonElement("totalSize")]
    public long TotalSize { get; set; }
    
    [BsonElement("totalViews")]
    public long TotalViews { get; private set; }
    
    [BsonElement("totalDownloads")]
    public long TotalDownloads { get; private set; }
    
    [BsonElement("totalShares")]
    public long TotalShares { get; private set; }
    
    [BsonElement("totalLikes")]
    public long TotalLikes { get; private set; }
    
    [BsonElement("totalComments")]
    public long TotalComments { get; private set; }
    
    [BsonElement("lastScanDate")]
    public DateTime? LastScanDate { get; private set; }
    
    [BsonElement("scanCount")]
    public long ScanCount { get; private set; }
    
    [BsonElement("lastActivity")]
    public DateTime? LastActivity { get; private set; }
    
    [BsonElement("totalCollections")]
    public long TotalCollections { get; set; }
    
    [BsonElement("activeCollections")]
    public long ActiveCollections { get; set; }
    
    [BsonElement("totalImages")]
    public long TotalImages { get; set; }
    
    [BsonElement("averageImagesPerCollection")]
    public double AverageImagesPerCollection { get; set; }
    
    [BsonElement("averageSizePerCollection")]
    public double AverageSizePerCollection { get; set; }
    
    [BsonElement("lastViewed")]
    public DateTime? LastViewed { get; set; }

    public CollectionStatistics()
    {
        TotalItems = 0;
        TotalSize = 0;
        TotalViews = 0;
        TotalDownloads = 0;
        TotalShares = 0;
        TotalLikes = 0;
        TotalComments = 0;
        ScanCount = 0;
        TotalCollections = 0;
        ActiveCollections = 0;
        TotalImages = 0;
        AverageImagesPerCollection = 0;
        AverageSizePerCollection = 0;
        LastViewed = null;
    }

    public void UpdateStats(long totalItems, long totalSize)
    {
        TotalItems = totalItems;
        TotalSize = totalSize;
        UpdateLastActivity();
    }

    public void IncrementItems(long count = 1)
    {
        TotalItems += count;
        UpdateLastActivity();
    }

    public void DecrementItems(long count = 1)
    {
        TotalItems = Math.Max(0, TotalItems - count);
        UpdateLastActivity();
    }

    public void IncrementSize(long size)
    {
        TotalSize += size;
        UpdateLastActivity();
    }

    public void DecrementSize(long size)
    {
        TotalSize = Math.Max(0, TotalSize - size);
        UpdateLastActivity();
    }

    public void IncrementViews(long count = 1)
    {
        TotalViews += count;
        UpdateLastActivity();
    }

    public void IncrementDownloads(long count = 1)
    {
        TotalDownloads += count;
        UpdateLastActivity();
    }

    public void IncrementShares(long count = 1)
    {
        TotalShares += count;
        UpdateLastActivity();
    }

    public void IncrementLikes(long count = 1)
    {
        TotalLikes += count;
        UpdateLastActivity();
    }

    public void IncrementComments(long count = 1)
    {
        TotalComments += count;
        UpdateLastActivity();
    }

    public void UpdateLastScanDate(DateTime scanDate)
    {
        LastScanDate = scanDate;
        ScanCount++;
        UpdateLastActivity();
    }

    public void UpdateLastActivity()
    {
        LastActivity = DateTime.UtcNow;
    }

    public void UpdateItemCount(int count)
    {
        TotalItems = count;
        TotalImages = count;
        UpdateLastActivity();
    }

    public void UpdateTotalSize(long size)
    {
        TotalSize = size;
        UpdateLastActivity();
    }

    public void IncrementViewCount()
    {
        TotalViews++;
        UpdateLastActivity();
    }
}
