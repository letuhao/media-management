# Random Collection Hotkey Implementation

## 🎯 Task Summary

**Goal**: Add random collection navigation with **Ctrl+Shift+R** hotkey to:
1. Collection List screen
2. Collection Detail screen  
3. Image Viewer screen

**Hotkey**: `Ctrl+Shift+R` (avoids Chrome's `Ctrl+R` reload conflict)

---

## ✅ Implementation Progress

### 1. Created Reusable Hook ✅

**File**: `client/src/hooks/useRandomNavigation.ts`

**Features**:
- Global hotkey: `Ctrl+Shift+R` 
- Context-aware navigation:
  - From Collection List → Navigate to random collection detail
  - From Collection Detail → Navigate to random collection detail
  - From Image Viewer → Navigate to first image of random collection (stay in viewer)
- Loading state management
- Toast notifications
- Can be disabled per screen

---

### 2. Updated Header Component ✅

**File**: `client/src/components/layout/Header.tsx`

**Changes**:
- ❌ Removed old `Ctrl+R` hotkey (conflicts with Chrome reload)
- ✅ Replaced with `useRandomNavigation` hook
- ✅ Updated button title to show `Ctrl+Shift+R`
- ✅ Cleaned up imports (removed unused `randomApi`, `toast`, `useNavigate`, `useEffect`)

---

### 3. Updated Collections List Page ✅

**File**: `client/src/pages/Collections.tsx`

**Changes**:
- ✅ Added `useRandomNavigation` hook import
- ✅ Added `Shuffle` icon import
- ✅ Added random button next to "Add" and "Bulk" buttons
- ✅ Button shows "Random" label on large screens
- ✅ Purple theme for consistency
- ✅ Spin animation when loading
- ✅ Hotkey works globally on this screen

---

### 4. Collection Detail Page - TODO

**File**: `client/src/pages/CollectionDetail.tsx`

**Changes Needed**:
- [ ] Add `useRandomNavigation` hook
- [ ] Add `Shuffle` icon import
- [ ] Add random button to action bar (near "Rescan" and "Open Viewer")
- [ ] Hotkey will work automatically

---

### 5. Image Viewer Page - TODO

**File**: `client/src/pages/ImageViewer.tsx`

**Changes Needed**:
- [ ] Add `useRandomNavigation` hook
- [ ] Hotkey will work automatically (stays in viewer, navigates to random collection's first image)

---

## 🎨 UI Design

### Button Appearance

```tsx
<Button
  variant="ghost"
  size="sm"
  onClick={handleRandom}
  disabled={isRandomLoading}
  className="flex items-center space-x-1.5 rounded-md text-purple-400 hover:text-purple-300 hover:bg-purple-500/10"
  title="Random Collection (Ctrl+Shift+R)"
>
  <Shuffle className={`h-4 w-4 ${isRandomLoading ? 'animate-spin' : ''}`} />
  <span className="hidden lg:inline text-xs">Random</span>
</Button>
```

**Visual**:
- 🎲 Purple shuffle icon
- Spins when loading
- Shows "Random" text on larger screens
- Tooltip shows hotkey: "Random Collection (Ctrl+Shift+R)"

---

## ⌨️ Hotkey Design

### Why Ctrl+Shift+R?

**Chrome Reserved Hotkeys** (must avoid):
- `Ctrl+R` or `F5` = Reload page ❌
- `Ctrl+T` = New tab ❌
- `Ctrl+W` = Close tab ❌
- `Ctrl+N` = New window ❌
- `Ctrl+H` = History ❌
- `Ctrl+D` = Bookmark ❌

**Chosen**: `Ctrl+Shift+R` ✅
- Not used by Chrome
- Easy to remember (R for Random)
- Shift modifier makes it unique
- Works on both Windows (Ctrl) and Mac (Cmd)

### Alternative Considered

- `Alt+R` - Could work but less discoverable
- Single `R` key - Conflicts when typing in search boxes
- `Ctrl+Shift+X` - X for "shuffle" but less intuitive

---

## 🔄 Navigation Behavior

### From Collection List

```
User presses Ctrl+Shift+R:
├─ API call: GET /api/v1/random
├─ Response: { id, name, firstImageId, ... }
├─ Navigate to: /collections/{id}
└─ Toast: "🎲 Random collection: {name}"
```

### From Collection Detail

```
User presses Ctrl+Shift+R:
├─ API call: GET /api/v1/random
├─ Response: { id, name, firstImageId, ... }
├─ Navigate to: /collections/{id}
└─ Toast: "🎲 Random collection: {name}"
```

### From Image Viewer

```
User presses Ctrl+Shift+R:
├─ API call: GET /api/v1/random
├─ Response: { id, name, firstImageId, ... }
├─ Navigate to: /collections/{id}/viewer?imageId={firstImageId}
└─ Toast: "🎲 Random: {name}"
```

**Key Feature**: Stays in viewer mode for quick browsing! 🚀

---

## 📝 Remaining Work

### Collection Detail Page

```tsx
// 1. Add imports
import { useRandomNavigation } from '../hooks/useRandomNavigation';
import { Shuffle } from 'lucide-react';

// 2. Add hook in component
const { handleRandom, isLoading: isRandomLoading } = useRandomNavigation();

// 3. Add button near "Rescan" and "Open Viewer"
<Button
  variant="ghost"
  size="sm"
  onClick={handleRandom}
  disabled={isRandomLoading}
  className="flex items-center space-x-1.5 text-purple-400 hover:text-purple-300 hover:bg-purple-500/10"
  title="Random Collection (Ctrl+Shift+R)"
>
  <Shuffle className={`h-4 w-4 ${isRandomLoading ? 'animate-spin' : ''}`} />
  <span className="hidden sm:inline">Random</span>
</Button>
```

### Image Viewer Page

```tsx
// 1. Add import
import { useRandomNavigation } from '../hooks/useRandomNavigation';

// 2. Add hook in component
const { handleRandom, isLoading: isRandomLoading } = useRandomNavigation();

// Note: Hotkey works automatically, button optional
// Could add button to toolbar if desired
```

---

## ✅ Testing Checklist

After completing remaining work:

- [ ] **Header**: Click random button → navigates correctly
- [ ] **Header**: Press `Ctrl+Shift+R` → navigates correctly
- [ ] **Collections List**: Click random button → navigates correctly
- [ ] **Collections List**: Press `Ctrl+Shift+R` → navigates correctly
- [ ] **Collection Detail**: Click random button → navigates correctly
- [ ] **Collection Detail**: Press `Ctrl+Shift+R` → navigates correctly
- [ ] **Image Viewer**: Press `Ctrl+Shift+R` → stays in viewer, loads random collection
- [ ] **All screens**: `Ctrl+R` still reloads page (Chrome default)
- [ ] **Mac**: `Cmd+Shift+R` works instead of `Ctrl+Shift+R`
- [ ] **Loading state**: Button shows spinning animation
- [ ] **Toast**: Shows collection name on successful navigation
- [ ] **Error handling**: Shows error toast if API fails

---

## 🎉 Benefits

1. ✅ **Fast random navigation** - Single hotkey press
2. ✅ **No Chrome conflicts** - Uses `Ctrl+Shift+R` instead of `Ctrl+R`
3. ✅ **Context-aware** - Smart behavior based on current screen
4. ✅ **Consistent UI** - Same purple theme across all screens
5. ✅ **Reusable hook** - Easy to add to any screen
6. ✅ **Loading feedback** - Spinning animation + disabled state
7. ✅ **User-friendly** - Tooltip shows hotkey hint

---

## 📊 Status

| Screen | Hook Added | Button Added | Hotkey Works | Status |
|--------|-----------|--------------|--------------|--------|
| Header | ✅ | ✅ | ✅ | **Complete** |
| Collections List | ✅ | ✅ | ✅ | **Complete** |
| Collection Detail | ❌ | ❌ | ❌ | **TODO** |
| Image Viewer | ❌ | ❌ | ❌ | **TODO** |

**Next**: Add to Collection Detail and Image Viewer pages


