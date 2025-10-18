using RabbitMQ.Client;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using ImageViewer.Domain.Events;
using ImageViewer.Domain.Interfaces;
using ImageViewer.Infrastructure.Data;
using ImageViewer.Infrastructure.Messaging;

namespace ImageViewer.Infrastructure.Services;

/// <summary>
/// RabbitMQ message queue service implementation
/// </summary>
public class RabbitMQMessageQueueService : IMessageQueueService, IDisposable
{
    private IConnection? _connection;
    private IChannel? _channel;
    private readonly RabbitMQOptions _options;
    private readonly ILogger<RabbitMQMessageQueueService> _logger;
    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
    private bool _disposed = false;

    public RabbitMQMessageQueueService(IOptions<RabbitMQOptions> options, ILogger<RabbitMQMessageQueueService> logger)
    {
        _options = options.Value;
        _logger = logger;
        
        _logger.LogDebug("RabbitMQMessageQueueService initialized (lazy connection)");
    }

    /// <summary>
    /// Ensures RabbitMQ connection and channel are established (lazy initialization)
    /// </summary>
    private async Task EnsureConnectionAsync()
    {
        if (_connection != null && _channel != null && _connection.IsOpen && _channel.IsOpen)
        {
            return; // Already connected
        }

        await _connectionSemaphore.WaitAsync();
        try
        {
            // Double-check after acquiring semaphore
            if (_connection != null && _channel != null && _connection.IsOpen && _channel.IsOpen)
            {
                return;
            }

            // Dispose existing connection if it exists but is not open
            if (_connection != null && !_connection.IsOpen)
            {
                try
                {
                    _connection.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing old RabbitMQ connection");
                }
            }

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                RequestedConnectionTimeout = _options.ConnectionTimeout,
                RequestedHeartbeat = TimeSpan.FromSeconds(60)
            };

            _logger.LogDebug("Establishing RabbitMQ connection to {HostName}:{Port}", _options.HostName, _options.Port);
            
            _connection = await factory.CreateConnectionAsync();
            _channel = await _connection.CreateChannelAsync();

            _logger.LogDebug("RabbitMQ connection and channel established successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to establish RabbitMQ connection to {HostName}:{Port}", _options.HostName, _options.Port);
            throw;
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    public async Task PublishAsync<T>(T message, string? routingKey = null, CancellationToken cancellationToken = default) where T : MessageEvent
    {
        try
        {
            await EnsureConnectionAsync();
            
            var queueName = GetQueueName<T>();
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
            var messageBody = JsonSerializer.SerializeToUtf8Bytes(message, options);
            var properties = new BasicProperties();
            
            properties.Persistent = true;
            properties.MessageId = message.Id.ToString();
            properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            properties.CorrelationId = message.CorrelationId?.ToString();
            properties.Headers = new Dictionary<string, object>
            {
                { "MessageType", message.MessageType },
                { "Timestamp", message.Timestamp.ToString("O") }
            };

            var actualRoutingKey = routingKey ?? GetDefaultRoutingKey<T>();
            
            _logger.LogDebug("Publishing message: Type={MessageType}, ID={MessageId}, Exchange={Exchange}, RoutingKey={RoutingKey}, Queue={Queue}", 
                message.MessageType, message.Id, _options.DefaultExchange, actualRoutingKey, queueName);

            await _channel.BasicPublishAsync(
                exchange: _options.DefaultExchange,
                routingKey: actualRoutingKey,
                mandatory: false,
                basicProperties: properties,
                body: messageBody);

            _logger.LogDebug("✓ Published successfully: {MessageType} with ID {MessageId} to {Exchange} via {RoutingKey}", 
                message.MessageType, message.Id, _options.DefaultExchange, actualRoutingKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish message {MessageType} with ID {MessageId}", message.MessageType, message.Id);
            throw;
        }
    }

    public async Task PublishDelayedAsync<T>(T message, TimeSpan delay, string? routingKey = null, CancellationToken cancellationToken = default) where T : MessageEvent
    {
        // For delayed messages, we can use RabbitMQ delayed message plugin or implement with TTL
        message.Properties["Delay"] = delay.TotalMilliseconds;
        await PublishAsync(message, routingKey, cancellationToken);
    }

    public async Task PublishWithPriorityAsync<T>(T message, int priority, string? routingKey = null, CancellationToken cancellationToken = default) where T : MessageEvent
    {
        message.Properties["Priority"] = priority;
        await PublishAsync(message, routingKey, cancellationToken);
    }

    public async Task PublishBatchAsync<T>(IEnumerable<T> messages, string? routingKey = null, CancellationToken cancellationToken = default) where T : MessageEvent
    {
        try
        {
            var messagesList = messages.ToList();
            if (!messagesList.Any())
            {
                _logger.LogDebug("No messages to publish in batch");
                return;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };

            var routingKeyToUse = routingKey ?? GetDefaultRoutingKey<T>();
            var batchCount = 0;

            // Publish messages sequentially (RabbitMQ client will batch internally)
            // This is still faster than individual calls due to reduced context switching
            var publishTasks = new List<Task>();
            
            foreach (var message in messagesList)
            {
                var messageBody = JsonSerializer.SerializeToUtf8Bytes(message, options);
                var properties = new BasicProperties
                {
                    Persistent = true,
                    MessageId = message.Id.ToString(),
                    Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    CorrelationId = message.CorrelationId?.ToString(),
                    Headers = new Dictionary<string, object>
                    {
                        { "MessageType", message.MessageType },
                        { "Timestamp", message.Timestamp.ToString("O") }
                    }
                };

                // Queue publish tasks (will execute in parallel)
                var publishTask = _channel.BasicPublishAsync(
                    exchange: _options.DefaultExchange,
                    routingKey: routingKeyToUse,
                    mandatory: false,
                    basicProperties: properties,
                    body: messageBody,
                    cancellationToken: cancellationToken
                );

                publishTasks.Add(publishTask.AsTask());
                batchCount++;
            }

            // Wait for all publishes to complete
            await Task.WhenAll(publishTasks);

            _logger.LogInformation("✅ Published batch of {Count} {MessageType} messages", batchCount, typeof(T).Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish batch of {MessageType} messages", typeof(T).Name);
            throw;
        }
    }

    private string GetQueueName<T>() where T : MessageEvent
    {
        return typeof(T).Name switch
        {
            nameof(CollectionScanMessage) => _options.CollectionScanQueue,
            nameof(ThumbnailGenerationMessage) => _options.ThumbnailGenerationQueue,
            nameof(CacheGenerationMessage) => _options.CacheGenerationQueue,
            nameof(CollectionCreationMessage) => _options.CollectionCreationQueue,
            nameof(BulkOperationMessage) => _options.BulkOperationQueue,
            nameof(ImageProcessingMessage) => _options.ImageProcessingQueue,
            nameof(LibraryScanMessage) => _options.LibraryScanQueue,
            _ => _options.ImageProcessingQueue
        };
    }

    private string GetDefaultRoutingKey<T>() where T : MessageEvent
    {
        return typeof(T).Name switch
        {
            nameof(CollectionScanMessage) => "collection.scan",
            nameof(ThumbnailGenerationMessage) => "thumbnail.generation",
            nameof(CacheGenerationMessage) => "cache.generation",
            nameof(CollectionCreationMessage) => "collection.creation",
            nameof(BulkOperationMessage) => "bulk.operation",
            nameof(ImageProcessingMessage) => "image.processing",
            nameof(LibraryScanMessage) => "library_scan_queue", // Match actual queue name
            _ => "image.processing"
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                _channel?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing RabbitMQ channel");
            }

            try
            {
                _connection?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing RabbitMQ connection");
            }

            _connectionSemaphore?.Dispose();
            _disposed = true;
        }
    }
}
