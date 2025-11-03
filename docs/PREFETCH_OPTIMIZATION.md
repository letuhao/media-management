# RabbitMQ Prefetch Optimization for HDD Performance

## Overview

Reduced RabbitMQ prefetch count from **100 to 2** to limit concurrent disk I/O operations and prevent HDD thrashing during collection scans.

## Problem

**Before:** 
- 5 consumers × 100 prefetch = **500 concurrent operations**
- Overwhelming HDD with random read/write operations
- Disk thrashing and performance degradation

**After:**
- 5 consumers × 2 prefetch = **10 concurrent operations maximum**
- Controlled, sequential disk access
- Better HDD performance

## Changes Made

### Modified Files:
1. `src/ImageViewer.Worker/appsettings.json` - PrefetchCount: 100 → 2
2. `src/ImageViewer.Worker/appsettings.Development.json` - PrefetchCount: 100 → 2
3. `src/ImageViewer.Worker/appsettings.BatchProcessing.json` - PrefetchCount: 100 → 2

### Configuration Location
```json
"RabbitMQ": {
  "PrefetchCount": 2,  // ← Reduced from 100
  "Concurrency": 8,
  // ... other settings
}
```

## How It Works

### Current Architecture
```
5 Active Consumers:
1. LibraryScanConsumer (prefetch=2)
2. CollectionScanConsumer (prefetch=2)
3. ImageProcessingConsumer (prefetch=2)
4. BatchThumbnailGenerationConsumer (prefetch=2)
5. BatchCacheGenerationConsumer (prefetch=2)
```

### BaseMessageConsumer Implementation
```csharp
// Line 40 in BaseMessageConsumer.cs
_channel.BasicQosAsync(0, (ushort)_options.PrefetchCount, false);
//                                             ↑
//                                      Now reads from config: 2
```

### Result
- Each consumer processes **maximum 2 messages at a time**
- RabbitMQ queues control delivery rate
- Total system-wide: **10 concurrent operations max**
- Perfect for HDD I/O optimization

## Testing Plan

1. **Monitor Disk I/O**
   - Check Task Manager → Performance → Disk
   - Should see lower queue depth
   - Sequential reads instead of random seeks

2. **Measure Processing Time**
   - Time a complete collection scan
   - Compare before/after performance
   - May take slightly longer but shouldn't thrash

3. **Watch Logs**
   - Monitor worker logs for backpressure
   - Check for timeout errors
   - Verify steady processing

## Tuning Guidelines

### Conservative (Current)
```json
"PrefetchCount": 2  // Safe for HDD
```
**Best for:** HDD storage, limited resources

### Balanced
```json
"PrefetchCount": 4-8  // Mix of safety and speed
```
**Best for:** SSD storage, moderate resources

### Aggressive
```json
"PrefetchCount": 10-20  // Maximum throughput
```
**Best for:** NVMe SSD, high-end hardware

## Rollback

If performance degrades significantly:
```json
"PrefetchCount": 10  // Safe middle ground
```

Or revert to original:
```json
"PrefetchCount": 100  // Original aggressive setting
```

## Related Documentation

- `docs/BULK_FLOW_ANALYSIS.md` - Complete pipeline analysis
- `docs/JOB_TRACKING_FLOW_ANALYSIS.md` - Job tracking details
- `src/ImageViewer.Worker/Services/BaseMessageConsumer.cs` - Implementation

## Future Improvements

If this works well, consider:
1. ✅ **Already Done** - Prefetch limit
2. 🔄 **Next** - Archive extraction to SSD temp folder
3. 🔄 **Later** - Resume/checkpoint logic
4. 🔄 **Future** - Intelligent queue prioritization

## Status

✅ **Complete** - Configuration changes applied
🔄 **In Testing** - Need to verify performance impact
⏭️ **Next** - Monitor and adjust as needed

