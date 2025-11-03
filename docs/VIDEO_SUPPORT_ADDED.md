# Video File Support Added to Collection Scanning

## Summary

Added video file support to the collection scanning system. Videos are now detected during scan and handled properly in direct access mode.

## Changes Made

### 1. Extended Media File Detection
**File:** `src/ImageViewer.Worker/Services/CollectionScanConsumer.cs`

**Before:**
```csharp
var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".webp", ".svg" };
```

**After:**
```csharp
var supportedExtensions = new[] { 
    ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".webp", ".svg",
    ".mp4", ".avi", ".mov", ".wmv", ".mkv", ".flv", ".webm", ".m4v", ".3gp", ".mpg", ".mpeg" 
};
```

### 2. Added FFMpegCore Support
```csharp
using FFMpegCore; // Added to CollectionScanConsumer.cs
```

### 3. Extended Dimension Extraction
Created dual-mode dimension extraction that handles both images and videos:

```csharp
private async Task<(int width, int height)> ExtractImageDimensions(ArchiveEntryInfo archiveEntry)
{
    // Check if video file
    if (isVideo)
    {
        // Use FFProbe.Analyse() for videos
        var videoInfo = GetVideoInfo(filePath);
        return (videoInfo.Width, videoInfo.Height);
    }
    else
    {
        // Use ImageProcessingService for images
        var dimensions = await imageProcessingService.GetImageDimensionsAsync(archiveEntry);
        return (dimensions.Width, dimensions.Height);
    }
}
```

### 4. Added Helper Methods
- `IsVideoFile()` - Checks if extension is a video format
- `GetVideoInfo()` - Extracts video dimensions using FFProbe

## How It Works

### Direct Access Mode (✅ Fully Supported)
When `UseDirectFileAccess = true` for folder collections:

1. **Scan:** Videos are detected alongside images
2. **Dimensions:** FFProbe extracts real video width/height
3. **Storage:** Videos stored as direct references (no processing)
4. **Display:** Frontend `MediaDisplay` renders with `<video>` tags

### Standard Mode (⚠️ Partial Support)
When direct access is disabled:

1. **Scan:** ✅ Videos are detected
2. **Queue:** ✅ Videos queued for processing
3. **Processing:** ❌ Will fail (image processing service can't handle videos)
4. **Needed:** Additional video processing consumers

## Supported Video Formats

| Format | Extension | Status |
|--------|-----------|--------|
| MP4 | .mp4 | ✅ Supported |
| AVI | .avi | ✅ Supported |
| MOV | .mov | ✅ Supported |
| WMV | .wmv | ✅ Supported |
| MKV | .mkv | ✅ Supported |
| FLV | .flv | ✅ Supported |
| WebM | .webm | ✅ Supported |
| M4V | .m4v | ✅ Supported |
| 3GP | .3gp | ✅ Supported |
| MPEG | .mpg, .mpeg | ✅ Supported |

## Frontend Compatibility

The frontend already has full video support:

- ✅ `MediaDisplay.tsx` - Auto-detects videos and renders with `<video>` tags
- ✅ `mediaUtils.ts` - Video format detection
- ✅ Proper MIME type mapping for all video formats
- ✅ Video controls, autoplay, looping support

## Testing Checklist

- [ ] Scan folder collection with mixed images and videos
- [ ] Verify videos appear in collection
- [ ] Check video dimensions are extracted correctly
- [ ] Test video playback in ImageViewer
- [ ] Verify direct access mode works for video-only collections

## Future Enhancements

If you want to support videos in **Standard Mode** (with thumbnail/cache generation):

1. **Video Thumbnail Generation:**
   - Use FFmpeg to extract frames
   - Generate first frame as thumbnail
   - Similar to animated GIF handling

2. **Video Processing Consumer:**
   - Create `VideoProcessingConsumer`
   - Queue videos separately from images
   - Generate thumbnails/cache for videos

3. **Archive Support:**
   - Extract videos from ZIP archives
   - Handle video entries in archives

## Dependencies

- **FFMpegCore** 5.2.0 (already installed in Application layer)
- **FFmpeg binary** (system requirement - must be installed for FFProbe to work)

### FFmpeg Installation
FFmpeg binary is required for FFProbe to work. Should be installed on the system running the worker.

**Windows:** Download from https://ffmpeg.org/download.html

## Limitations

1. **Archive Collections:** Videos inside ZIP/CBZ not yet supported
2. **Standard Mode:** No thumbnail generation for videos
3. **Processing:** Videos skip image processing consumers
4. **FFmpeg Dependency:** System must have FFmpeg installed for dimension extraction

## Commit

```
ad32507 Add video file support to collection scanning
```

## Related Files

- `src/ImageViewer.Worker/Services/CollectionScanConsumer.cs` - Main changes
- `client/src/components/media/MediaDisplay.tsx` - Already supports videos
- `client/src/utils/mediaUtils.ts` - Video format detection
- `src/ImageViewer.Application/Services/WindowsDriveService.cs` - Reference implementation

