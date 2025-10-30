namespace ImageViewer.Application.DTOs.Maintenance;

/// <summary>
/// Summary of deduplication results
/// / Tóm tắt kết quả khử trùng lặp
/// </summary>
public class DeduplicationSummaryDto
{
    public int CollectionsScanned { get; set; }
    public int CollectionsWithDuplicates { get; set; }
    public int CollectionsProcessed { get; set; }
    public int TotalThumbDuplicatesFound { get; set; }
    public int TotalCacheDuplicatesFound { get; set; }
    public int TotalThumbsRemoved { get; set; }
    public int TotalCachesRemoved { get; set; }
}

