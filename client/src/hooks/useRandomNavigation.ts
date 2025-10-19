import { useState, useEffect, useCallback } from 'react';
import { useNavigate, useLocation } from 'react-router-dom';
import { randomApi } from '../services/randomApi';
import toast from 'react-hot-toast';

// ✅ FIX: Global flag to ensure only ONE event listener is active
// Prevents double-call when multiple components use the hook on the same page
let globalHotkeyRegistered = false;

/**
 * Custom hook for random collection navigation with hotkey support
 * 
 * Hotkey: Ctrl+Shift+R (avoids Chrome's Ctrl+R reload)
 * 
 * Behavior:
 * - From Collection List: Navigate to random collection detail
 * - From Collection Detail: Navigate to random collection detail
 * - From Image Viewer: Navigate to first image of random collection (stay in viewer)
 * 
 * @param enabled - Whether the hotkey should be active (default: true)
 * @param registerHotkey - Whether to register global hotkey (default: true, set false for non-Header components)
 * @returns { handleRandom, isLoading }
 */
export const useRandomNavigation = (enabled: boolean = true, registerHotkey: boolean = true) => {
  const navigate = useNavigate();
  const location = useLocation();
  const [isLoading, setIsLoading] = useState(false);

  const handleRandom = useCallback(async () => {
    if (isLoading) return; // Prevent double-clicks
    
    setIsLoading(true);
    try {
      const randomCollection = await randomApi.getRandomCollection();
      
      // Check if we're in Image Viewer screen
      const isImageViewerScreen = location.pathname.includes('/viewer/') || 
                                   location.pathname.includes('/collections/') && location.pathname.includes('/viewer');
      
      if (isImageViewerScreen && randomCollection.firstImageId) {
        // Stay in Image Viewer, navigate to first image of random collection
        navigate(`/collections/${randomCollection.id}/viewer?imageId=${randomCollection.firstImageId}`);
        toast.success(`🎲 Random: ${randomCollection.name}`, {
          duration: 2000,
          position: 'bottom-center',
        });
      } else {
        // Navigate to collection detail screen
        navigate(`/collections/${randomCollection.id}`);
        toast.success(`🎲 Random collection: ${randomCollection.name}`, {
          duration: 2000,
          position: 'bottom-center',
        });
      }
    } catch (error: any) {
      toast.error(error.response?.data?.message || 'Failed to get random collection', {
        duration: 3000,
      });
    } finally {
      setIsLoading(false);
    }
  }, [navigate, location.pathname, isLoading]);

  // Keyboard shortcut: Ctrl+Shift+R for random
  // ✅ FIX: Only register if requested AND not already registered globally
  useEffect(() => {
    if (!enabled || !registerHotkey) return;
    
    // ✅ FIX: Check global flag to prevent multiple registrations
    if (globalHotkeyRegistered) {
      console.log('[useRandomNavigation] Hotkey already registered globally, skipping');
      return;
    }

    console.log('[useRandomNavigation] Registering global hotkey');
    globalHotkeyRegistered = true;

    const handleKeyDown = (e: KeyboardEvent) => {
      // Ctrl+Shift+R or Cmd+Shift+R (Mac)
      if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key === 'R') {
        e.preventDefault(); // Prevent any browser action
        handleRandom();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    
    return () => {
      console.log('[useRandomNavigation] Unregistering global hotkey');
      window.removeEventListener('keydown', handleKeyDown);
      globalHotkeyRegistered = false;
    };
  }, [enabled, registerHotkey, handleRandom]);

  return {
    handleRandom,
    isLoading,
  };
};

