namespace ImageViewer.Infrastructure.Data;

/// <summary>
/// RabbitMQ configuration options
/// </summary>
public class RabbitMQOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public string ConnectionString { get; set; } = "amqp://guest:guest@localhost:5672/";
    
    // Queue configurations
    public string CollectionScanQueue { get; set; } = "collection.scan";
    public string ThumbnailGenerationQueue { get; set; } = "thumbnail.generation";
    public string CacheGenerationQueue { get; set; } = "cache.generation";
    public string CollectionCreationQueue { get; set; } = "collection.creation";
    public string BulkOperationQueue { get; set; } = "bulk.operation";
    public string ImageProcessingQueue { get; set; } = "image.processing";
    public string LibraryScanQueue { get; set; } = "library_scan_queue";
    
    // Exchange configurations
    public string DefaultExchange { get; set; } = "imageviewer.exchange";
    public string DeadLetterExchange { get; set; } = "imageviewer.dlx";
    
    // Retry and timeout configurations
    public int MaxRetryCount { get; set; } = 3;
    public TimeSpan MessageTimeout { get; set; } = TimeSpan.FromHours(24); // Default to 24 hours, configurable via appsettings
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    
    // Consumer configurations
    public int PrefetchCount { get; set; } = 100;
    public bool AutoAck { get; set; } = false;
    
    // Bulk operation configurations
    public int MessageBatchSize { get; set; } = 1000;
    public int MaxQueueLength { get; set; } = 50000000;
    
    // Image processing limits
    public long MaxImageSizeBytes { get; set; } = 500 * 1024 * 1024; // 500MB default for regular images
    public long MaxZipEntrySizeBytes { get; set; } = 20L * 1024 * 1024 * 1024; // 20GB for ZIP entries
}
