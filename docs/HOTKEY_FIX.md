# Hotkey Conflict Fix - Ctrl+Arrow Keys

## Problem

In the ImageViewer component, `Ctrl+Left Arrow` and `Left Arrow` were both firing at the same time, causing unwanted navigation behavior.

### Root Cause

Two separate keyboard event handlers existed:
1. **Direct switch handler** (lines 562-638) - Handles basic arrow keys without modifier checks
2. **useHotkeys hook** (lines 656-661) - Handles Ctrl+Arrow for collection navigation

When `Ctrl+Left Arrow` was pressed:
- The switch handler fired first (no Ctrl check)
- Then useHotkeys fired
- Result: Both image navigation AND collection navigation occurred

## Solution

Added `ctrlKey` checks to the arrow key cases in the switch handler:

```typescript
case 'ArrowLeft':
  // Skip if Ctrl is pressed (handled by useHotkeys for collection navigation)
  if (e.ctrlKey) break;
  navigateToImage('prev');
  break;
case 'ArrowRight':
  // Skip if Ctrl is pressed (handled by useHotkeys for collection navigation)
  if (e.ctrlKey) break;
  navigateToImage('next');
  break;
```

## Result

Now the behavior is correct:
- **Left Arrow** → Previous image in current collection
- **Ctrl+Left Arrow** → Previous collection (first image)
- **Right Arrow** → Next image in current collection
- **Ctrl+Right Arrow** → Next collection (first image)

## Files Modified

- `client/src/pages/ImageViewer.tsx` (lines 568-577)

## Testing

✅ Tested: Left Arrow navigation
✅ Tested: Right Arrow navigation  
✅ Tested: Ctrl+Left Arrow collection navigation
✅ Tested: Ctrl+Right Arrow collection navigation
✅ Verified: No double-fires occur

