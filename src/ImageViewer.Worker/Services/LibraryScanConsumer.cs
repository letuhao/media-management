using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using ImageViewer.Application.Services;
using ImageViewer.Domain.Interfaces;
using ImageViewer.Infrastructure.Data;
using ImageViewer.Infrastructure.Messaging;
using MongoDB.Bson;

namespace ImageViewer.Worker.Services;

/// <summary>
/// Consumer for library scan messages
/// Scans library folder and creates collections for discovered items
/// </summary>
public class LibraryScanConsumer : BaseMessageConsumer
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public LibraryScanConsumer(
        IConnection connection,
        IOptions<RabbitMQOptions> options,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<LibraryScanConsumer> logger)
        : base(connection, options, logger, options.Value.LibraryScanQueue ?? "library_scan_queue", "library-scan-consumer")
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    protected override async Task ProcessMessageAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("📚 Received library scan message: {Message}", message);
            
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
            var scanMessage = JsonSerializer.Deserialize<LibraryScanMessage>(message, options);
            if (scanMessage == null)
            {
                _logger.LogWarning("❌ Failed to deserialize LibraryScanMessage from: {Message}", message);
                return;
            }

            _logger.LogInformation(
                "📚 Processing library scan for library {LibraryId} at path {Path}, JobRunId: {JobRunId}",
                scanMessage.LibraryId,
                scanMessage.LibraryPath,
                scanMessage.JobRunId);

            // Try to create scope, handle disposal gracefully
            IServiceScope? scope = null;
            try
            {
                scope = _serviceScopeFactory.CreateScope();
            }
            catch (ObjectDisposedException)
            {
                _logger.LogWarning("⚠️ Service provider disposed, worker is shutting down. Skipping library scan.");
                return;
            }

            using (scope)
            {
                var libraryRepository = scope.ServiceProvider.GetRequiredService<ILibraryRepository>();
                var collectionRepository = scope.ServiceProvider.GetRequiredService<ICollectionRepository>();
                var scheduledJobRunRepository = scope.ServiceProvider.GetRequiredService<IScheduledJobRunRepository>();
                var messageQueueService = scope.ServiceProvider.GetRequiredService<IMessageQueueService>();
                var bulkService = scope.ServiceProvider.GetRequiredService<IBulkService>();

                // Get the library
                var libraryId = ObjectId.Parse(scanMessage.LibraryId);
                var library = await libraryRepository.GetByIdAsync(libraryId);
                if (library == null)
                {
                    _logger.LogWarning("❌ Library {LibraryId} not found, skipping scan", scanMessage.LibraryId);
                    
                    // Update job run status to failed
                    if (!string.IsNullOrEmpty(scanMessage.JobRunId))
                    {
                        await UpdateJobRunStatusAsync(
                            scheduledJobRunRepository,
                            scanMessage.JobRunId,
                            "Failed",
                            $"Library {scanMessage.LibraryId} not found");
                    }
                    return;
                }

                if (library.IsDeleted)
                {
                    _logger.LogWarning("⚠️ Library {LibraryId} is deleted, skipping scan", scanMessage.LibraryId);
                    
                    if (!string.IsNullOrEmpty(scanMessage.JobRunId))
                    {
                        await UpdateJobRunStatusAsync(
                            scheduledJobRunRepository,
                            scanMessage.JobRunId,
                            "Completed",
                            "Library is deleted, no scan performed");
                    }
                    return;
                }

                // Verify library path exists
                if (!Directory.Exists(scanMessage.LibraryPath))
                {
                    _logger.LogError("❌ Library path does not exist: {Path}", scanMessage.LibraryPath);
                    
                    if (!string.IsNullOrEmpty(scanMessage.JobRunId))
                    {
                        await UpdateJobRunStatusAsync(
                            scheduledJobRunRepository,
                            scanMessage.JobRunId,
                            "Failed",
                            $"Library path does not exist: {scanMessage.LibraryPath}");
                    }
                    return;
                }

                _logger.LogInformation("🔍 Scanning library folder: {Path}", library.Path);
                
                // Use BulkService to discover and create collections
                // BulkService has tested logic for nested folders, compressed files, etc.
                var bulkRequest = new BulkAddCollectionsRequest
                {
                    LibraryId = libraryId, // Associate collections with this library
                    ParentPath = library.Path, // Use path from DB, not from message
                    IncludeSubfolders = scanMessage.IncludeSubfolders,
                    CollectionPrefix = null, // No prefix filtering for library scans
                    AutoAdd = false, // We don't auto-add, we control creation
                    OverwriteExisting = scanMessage.OverwriteExisting, // Pass from message
                    ResumeIncomplete = scanMessage.ResumeIncomplete, // Pass from message
                    UseDirectFileAccess = scanMessage.UseDirectFileAccess, // Pass direct mode flag
                    AutoScan = library.Settings?.AutoScan ?? false,
                    EnableCache = library.Settings?.CacheSettings?.Enabled ?? true,
                    ThumbnailWidth = library.Settings?.ThumbnailSettings?.Width,
                    ThumbnailHeight = library.Settings?.ThumbnailSettings?.Height
                };

                var result = await bulkService.BulkAddCollectionsAsync(bulkRequest, CancellationToken.None);
                
                _logger.LogInformation(
                    "✅ Library scan completed. Total: {Total}, Created: {Created}, Updated: {Updated}, Skipped: {Skipped}, Errors: {Errors}",
                    result.TotalProcessed,
                    result.CreatedCount,
                    result.UpdatedCount,
                    result.SkippedCount,
                    result.ErrorCount);

                // Update library statistics: increment collection count and update last scan date
                if (result.CreatedCount > 0)
                {
                    await libraryRepository.IncrementLibraryStatisticsAsync(
                        libraryId, 
                        collectionCount: result.CreatedCount, 
                        mediaItemCount: 0, // Will be updated when collections are scanned
                        sizeBytes: 0); // Will be updated when collections are scanned
                    
                    _logger.LogInformation("📊 Updated library {LibraryId} statistics: +{Count} collections", 
                        libraryId, result.CreatedCount);
                }

                // Always update last scan date
                await libraryRepository.UpdateLastScanDateAsync(libraryId);

                // Update job run status to completed
                if (!string.IsNullOrEmpty(scanMessage.JobRunId))
                {
                    var status = result.ErrorCount > 0 ? "CompletedWithErrors" : "Completed";
                    var statusMessage = $"Scan completed. Total: {result.TotalProcessed}, Created: {result.CreatedCount}, Skipped: {result.SkippedCount}, Errors: {result.ErrorCount}";
                    
                    if (result.Errors.Any())
                    {
                        statusMessage += $"\nErrors: {string.Join("; ", result.Errors)}";
                    }
                    
                    await UpdateJobRunStatusAsync(
                        scheduledJobRunRepository,
                        scanMessage.JobRunId,
                        status,
                        statusMessage);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error processing library scan message: {Message}", message);
            
            // Log detailed exception information for debugging
            _logger.LogError("Exception Type: {ExceptionType}", ex.GetType().Name);
            _logger.LogError("Exception Message: {ExceptionMessage}", ex.Message);
            _logger.LogError("Stack Trace: {StackTrace}", ex.StackTrace);
            
            if (ex.InnerException != null)
            {
                _logger.LogError("Inner Exception: {InnerException}", ex.InnerException.Message);
            }
            
            // Don't re-throw - let the message be acknowledged as processed
            // This prevents message requeuing and connection corruption
            _logger.LogWarning("⚠️ Library scan failed but message will be acknowledged to prevent requeuing");
        }
    }

    private async Task UpdateJobRunStatusAsync(
        IScheduledJobRunRepository repository,
        string jobRunId,
        string status,
        string message)
    {
        try
        {
            var jobRun = await repository.GetByIdAsync(ObjectId.Parse(jobRunId));
            if (jobRun != null)
            {
                if (status == "Failed")
                {
                    jobRun.Fail(message);
                }
                else if (status == "Completed")
                {
                    jobRun.Complete(new Dictionary<string, object>
                    {
                        { "message", message }
                    });
                }
                
                await repository.UpdateAsync(jobRun);
                _logger.LogInformation("✅ Updated job run {JobRunId} status to {Status}", jobRunId, status);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to update job run status for {JobRunId}", jobRunId);
        }
    }
}

