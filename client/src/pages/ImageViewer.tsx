import { useState, useEffect, useCallback, useRef } from 'react';
import { useParams, useNavigate, useSearchParams } from 'react-router-dom';
import { useImages, useImage } from '../hooks/useImages';
import { useCollection } from '../hooks/useCollections';
import { useCollectionNavigation } from '../hooks/useCollectionNavigation';
import { useCrossCollectionNavigation } from '../hooks/useCrossCollectionNavigation';
import { useUserSettings } from '../hooks/useSettings';
import { useHotkeys, CommonHotkeys } from '../hooks/useHotkeys';
import LoadingSpinner from '../components/ui/LoadingSpinner';
import MediaDisplay from '../components/media/MediaDisplay';
import { useUI } from '../contexts/UIContext';
import CollectionNavigationSidebar from '../components/collections/CollectionNavigationSidebar';
import ImagePreviewSidebar from '../components/viewer/ImagePreviewSidebar';
import {
  X,
  ChevronLeft,
  ChevronRight,
  ZoomIn,
  ZoomOut,
  RotateCw,
  RotateCcw,
  Play,
  Pause,
  Grid,
  Maximize2,
  Monitor,
  Layout,
  Settings,
  Info,
  HelpCircle,
  Shuffle,
  Expand,
  Scan,
  ArrowDownUp,
  PanelLeft,
  PanelRight,
  Images,
  Link2,
  Smartphone,
  Laptop,
} from 'lucide-react';

/**
 * View modes for the image viewer
 */
type ViewMode = 'single' | 'double' | 'triple' | 'quad';

/**
 * Navigation modes
 */
type NavigationMode = 'paging' | 'scroll';

/**
 * Image Viewer
 * 
 * Full-screen image viewer with:
 * - Multiple view modes (single, double, triple, quad)
 * - Keyboard navigation (Arrow keys, Esc)
 * - Zoom controls
 * - Slideshow mode
 * - Image info overlay
 * - Fullscreen support
 */
const ImageViewer: React.FC = () => {
  const { id: collectionId } = useParams<{ id: string }>();
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();

  const initialImageId = searchParams.get('imageId');
  const goToLast = searchParams.get('goToLast') === 'true';
  const { data: collection } = useCollection(collectionId!);
  
  // Get user settings for pageSize
  const { data: userSettingsData } = useUserSettings();
  const batchSize = userSettingsData?.imageViewerPageSize || 200;
  
  // Batch loading state
  const [currentBatch, setCurrentBatch] = useState(1);
  const [loadedImages, setLoadedImages] = useState<any[]>([]);
  const loadedForCollectionRef = useRef<string | null>(null);
  
  // Load current batch
  const { data: batchData } = useImages({ 
    collectionId: collectionId!, 
    page: currentBatch,
    limit: batchSize
  });
  
  // Use local state for current image ID to avoid URL changes on every navigation
  const [currentImageId, setCurrentImageId] = useState(initialImageId || '');
  
  // Only fetch initial image for metadata, then use cached list
  const { data: initialImage } = useImage(collectionId!, initialImageId!);

  const [zoom, setZoom] = useState(1);
  const [rotation, setRotation] = useState(0);
  const [isSlideshow, setIsSlideshow] = useState(false);
  const [showInfo, setShowInfo] = useState(false);
  const [showHelp, setShowHelp] = useState(false);
  const [viewMode, setViewMode] = useState<ViewMode>(() => 
    (localStorage.getItem('imageViewerViewMode') as ViewMode) || 'single'
  );
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [slideshowInterval, setSlideshowInterval] = useState(() => 
    parseInt(localStorage.getItem('slideshowInterval') || '3000')
  );
  const [isShuffleMode, setIsShuffleMode] = useState(false);
  const [panPosition, setPanPosition] = useState({ x: 0, y: 0 });
  const [imageLoading, setImageLoading] = useState(true);
  const [imageError, setImageError] = useState(false);
  const [fitToScreen, setFitToScreen] = useState(() => 
    localStorage.getItem('imageViewerFitToScreen') === 'true'
  );
  const [navigationMode, setNavigationMode] = useState<NavigationMode>(() => 
    (localStorage.getItem('imageViewerNavigationMode') as NavigationMode) || 'paging'
  );
  const [showCollectionSidebar, setShowCollectionSidebar] = useState(() => 
    localStorage.getItem('imageViewerShowSidebar') === 'true' // Default: hidden
  );
  const [showImagePreviewSidebar, setShowImagePreviewSidebar] = useState(() => 
    localStorage.getItem('imageViewerShowPreviewSidebar') === 'true' // Default: hidden
  );
  const [crossCollectionNav, setCrossCollectionNav] = useState(() => 
    localStorage.getItem('imageViewerCrossCollectionNav') === 'true' // Default: disabled
  );
  
  // Get global UI state for hiding DevTools
  const { hideDevTools, setHideDevTools } = useUI();
  
  // Hide all controls state
  const [hideAllControls, setHideAllControls] = useState(false);
  
  // Auto-rotate modes
  type AutoRotateMode = 'none' | 'portrait' | 'landscape';
  const [autoRotateMode, setAutoRotateMode] = useState<AutoRotateMode>(() => 
    (localStorage.getItem('imageViewerAutoRotateMode') as AutoRotateMode) || 'none'
  );
  
  const slideshowRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const imageContainerRef = useRef<HTMLDivElement>(null);
  const preloadedImagesRef = useRef<Map<string, HTMLImageElement>>(new Map());

  // Calculate auto-rotation based on image dimensions, mode, and viewport optimization
  const getAutoRotation = useCallback((imageWidth: number, imageHeight: number): number => {
    if (autoRotateMode === 'none') return 0;
    
    const isPortrait = imageHeight > imageWidth;
    
    if (autoRotateMode === 'portrait' && !isPortrait) {
      // Image is landscape but we want portrait - rotate 90 degrees
      return 90;
    } else if (autoRotateMode === 'landscape' && isPortrait) {
      // Image is portrait but we want landscape - rotate 90 degrees
      return 90;
    }
    
    return 0; // No rotation needed
  }, [autoRotateMode]);

  // Calculate scale factor for auto-rotated images based on viewport fitting
  const getAutoRotatedScale = useCallback((imageWidth: number, imageHeight: number): number => {
    // TODO: Commented out scale logic - causing issues with 1:1 ratio images being scaled up unnecessarily
    // Need to find better approach tomorrow
    return 1;
    
    /* 
    if (autoRotateMode === 'none') return 1;
    
    const isPortrait = imageHeight > imageWidth;
    const shouldRotate = (autoRotateMode === 'portrait' && !isPortrait) || (autoRotateMode === 'landscape' && isPortrait);
    
    if (!shouldRotate) return 1;
    
    // Get viewport dimensions (accounting for UI controls)
    const viewportWidth = window.innerWidth;
    const viewportHeight = window.innerHeight - 100; // Subtract space for controls
    
    // After rotation: imageHeight becomes width, imageWidth becomes height
    const rotatedImageWidth = imageHeight;
    const rotatedImageHeight = imageWidth;
    
    // Calculate how the original image would fit in viewport
    const originalScaleForWidth = viewportWidth / imageWidth;
    const originalScaleForHeight = viewportHeight / imageHeight;
    const originalScale = Math.min(originalScaleForWidth, originalScaleForHeight);
    
    // Calculate how the rotated image would fit in viewport
    const rotatedScaleForWidth = viewportWidth / rotatedImageWidth;
    const rotatedScaleForHeight = viewportHeight / rotatedImageHeight;
    const rotatedScale = Math.min(rotatedScaleForWidth, rotatedScaleForHeight);
    
    // The scale factor is how much bigger the rotated image should be
    // compared to the original to achieve the same viewport coverage
    const scaleFactor = rotatedScale / originalScale;
    
    // Apply a conservative factor to avoid scrollbars (0.8 = 80% of calculated scale)
    return Math.max(0.5, Math.min(2.0, scaleFactor * 0.8));
    */
  }, [autoRotateMode]);

  // Fetch collection navigation info for cross-collection navigation
  const { data: collectionNav } = useCollectionNavigation(
    crossCollectionNav ? collectionId : undefined
  );

  // Cross-collection navigation hook
  const { navigate: navigateCrossCollection, isNavigating: isCrossNavigating } = useCrossCollectionNavigation();

  // Reset when collection changes
  useEffect(() => {
    setLoadedImages([]);
    setCurrentBatch(1);
    loadedForCollectionRef.current = null;
    preloadedImagesRef.current.clear();
  }, [collectionId]);
  
  // Accumulate batch data into loadedImages
  useEffect(() => {
    if (batchData?.data && batchData.data.length > 0) {
      // Mark which collection these images belong to
      loadedForCollectionRef.current = collectionId!;
      
      setLoadedImages(prev => {
        // For batch 1, replace. For batch 2+, append
        if (currentBatch === 1) {
          return batchData.data;
        } else {
          // Append and deduplicate
          const existingIds = new Set(prev.map(img => img.id));
          const newImages = batchData.data.filter(img => !existingIds.has(img.id));
          return [...prev, ...newImages];
        }
      });
    }
  }, [batchData, currentBatch, collectionId]);
  
  // Only use loadedImages if they're for the current collection (prevent 404s!)
  const images = (loadedForCollectionRef.current === collectionId) ? loadedImages : [];
  const totalImages = batchData?.total || 0;
  const currentIndex = images.findIndex((img) => img.id === currentImageId);
  const currentImage = currentIndex >= 0 ? images[currentIndex] : null;
  
  // Auto-load next batch when near end of loaded images
  useEffect(() => {
    if (images.length === 0 || totalImages === 0) return;
    
    const threshold = 20;
    const hasMoreImages = images.length < totalImages;
    const nearEnd = currentIndex >= images.length - threshold;
    
    if (nearEnd && hasMoreImages) {
      const nextBatch = currentBatch + 1;
      const maxBatch = Math.ceil(totalImages / batchSize);
      
      if (nextBatch <= maxBatch) {
        setCurrentBatch(nextBatch);
      }
    }
  }, [currentIndex, images.length, totalImages, currentBatch, batchSize]);

  // Aggressive loading when looking for a specific image that's not found
  useEffect(() => {
    if (currentIndex === -1 && currentImageId && images.length > 0 && totalImages > 0) {
      // We're looking for a specific image that's not in the current batch
      const hasMoreImages = images.length < totalImages;
      const maxBatch = Math.ceil(totalImages / batchSize);
      const nextBatch = currentBatch + 1;
      
      if (hasMoreImages && nextBatch <= maxBatch) {
        console.log(`Loading next batch (${nextBatch}/${maxBatch}) to find target image ${currentImageId}`);
        setCurrentBatch(nextBatch);
      }
    }
  }, [currentIndex, currentImageId, images.length, totalImages, currentBatch, batchSize]);

  // Handle goToLast parameter for cross-collection navigation from previous collection
  useEffect(() => {
    if (goToLast && images.length > 0 && !currentImageId) {
      // Navigate to last image
      const lastImage = images[images.length - 1];
      setCurrentImageId(lastImage.id);
    }
  }, [goToLast, images, currentImageId]);
  
  // Sync currentImageId with URL parameter
  useEffect(() => {
    if (initialImageId && initialImageId !== currentImageId) {
      setCurrentImageId(initialImageId);
    }
  }, [initialImageId]);
  
  // Handle invalid currentIndex - only fallback if we've loaded all images
  useEffect(() => {
    if (images.length > 0 && currentIndex === -1 && currentImageId) {
      // Check if we have all images loaded (no more batches to load)
      const hasAllImages = !batchData?.hasNext || (batchData && batchData.total && images.length >= batchData.total);
      
      if (hasAllImages) {
        // We have all images loaded and the target image is not found, so it doesn't exist
        // Fall back to first image
        console.warn(`Target image ${currentImageId} not found in collection ${collectionId}, falling back to first image`);
        setCurrentImageId(images[0].id);
      }
      // If we don't have all images loaded, keep trying to load more batches
      // The target image might be in a future batch
    }
  }, [currentIndex, images, currentImageId, batchData, collectionId]);

  // Reset loading/error state when image changes
  useEffect(() => {
    setImageLoading(true);
    setImageError(false);
  }, [currentImageId]);

  // Save view mode to localStorage
  const saveViewMode = useCallback((mode: ViewMode) => {
    setViewMode(mode);
    localStorage.setItem('imageViewerViewMode', mode);
  }, []);

  // Toggle fit to screen mode
  const toggleFitToScreen = useCallback(() => {
    const newValue = !fitToScreen;
    setFitToScreen(newValue);
    localStorage.setItem('imageViewerFitToScreen', newValue.toString());
  }, [fitToScreen]);

  // Toggle navigation mode
  const toggleNavigationMode = useCallback(() => {
    const newMode: NavigationMode = navigationMode === 'paging' ? 'scroll' : 'paging';
    setNavigationMode(newMode);
    localStorage.setItem('imageViewerNavigationMode', newMode);
  }, [navigationMode]);

  // Toggle cross-collection navigation
  const toggleCrossCollectionNav = useCallback(() => {
    const newValue = !crossCollectionNav;
    setCrossCollectionNav(newValue);
    localStorage.setItem('imageViewerCrossCollectionNav', newValue.toString());
  }, [crossCollectionNav]);

  // Toggle collection sidebar
  const toggleCollectionSidebar = useCallback(() => {
    const newValue = !showCollectionSidebar;
    setShowCollectionSidebar(newValue);
    localStorage.setItem('imageViewerShowSidebar', newValue.toString());
  }, [showCollectionSidebar]);

  // Toggle image preview sidebar
  const toggleImagePreviewSidebar = useCallback(() => {
    const newValue = !showImagePreviewSidebar;
    setShowImagePreviewSidebar(newValue);
    localStorage.setItem('imageViewerShowPreviewSidebar', newValue.toString());
  }, [showImagePreviewSidebar]);

  // Toggle auto-rotate mode
  const toggleAutoRotateMode = useCallback(() => {
    const modes: AutoRotateMode[] = ['none', 'portrait', 'landscape'];
    const currentIndex = modes.indexOf(autoRotateMode);
    const nextIndex = (currentIndex + 1) % modes.length;
    const newMode = modes[nextIndex];
    
    setAutoRotateMode(newMode);
    localStorage.setItem('imageViewerAutoRotateMode', newMode);
  }, [autoRotateMode]);

  // Get icon for current auto-rotate mode
  const getAutoRotateIcon = useCallback(() => {
    switch (autoRotateMode) {
      case 'none':
        return <Monitor className="h-5 w-5 text-white" />; // Desktop/Original orientation
      case 'portrait':
        return <Smartphone className="h-5 w-5 text-white" />; // Portrait orientation
      case 'landscape':
        return <Laptop className="h-5 w-5 text-white" />; // Landscape orientation
      default:
        return <Monitor className="h-5 w-5 text-white" />;
    }
  }, [autoRotateMode]);

  // Handle mouse wheel for zoom (Ctrl+Wheel)
  const handleWheel = useCallback((e: WheelEvent) => {
    // Only zoom when Ctrl key is pressed
    if (e.ctrlKey || e.metaKey) {
      e.preventDefault();
      
      const zoomDelta = e.deltaY > 0 ? -0.1 : 0.1;
      setZoom((prevZoom) => {
        const newZoom = Math.max(0.5, Math.min(5, prevZoom + zoomDelta));
        return Math.round(newZoom * 10) / 10; // Round to 1 decimal
      });
    }
  }, []);

  // Add wheel event listener for zoom in scroll mode
  useEffect(() => {
    const container = imageContainerRef.current;
    if (!container) return;

    // Add passive: false to allow preventDefault for Ctrl+Wheel
    container.addEventListener('wheel', handleWheel, { passive: false });

    return () => {
      container.removeEventListener('wheel', handleWheel);
    };
  }, [handleWheel]);

  // Get image class based on fit mode and screen orientation
  const getImageClass = useCallback((imageWidth?: number, imageHeight?: number) => {
    if (!fitToScreen) {
      return "max-w-full max-h-[calc(100vh-8rem)] object-contain transition-transform duration-200";
    }

    // For auto-rotated images, we need special handling
    if (imageWidth && imageHeight && autoRotateMode !== 'none') {
      const isPortrait = imageHeight > imageWidth;
      const shouldRotate = (autoRotateMode === 'portrait' && !isPortrait) || (autoRotateMode === 'landscape' && isPortrait);
      
      if (shouldRotate) {
        // For rotated images, use viewport dimensions to fill properly
        const isLandscapeScreen = window.innerWidth > window.innerHeight;
        if (isLandscapeScreen) {
          return "h-[100vh] w-auto object-contain transition-transform duration-200";
        } else {
          return "w-[100vw] h-auto object-contain transition-transform duration-200";
        }
      }
    }

    // Fit to screen mode: use screen orientation, not image orientation
    // Landscape screen (like 32:9 ultrawide): use 100vh (fill height)
    // Portrait screen (like vertical monitor): use 100vw (fill width)
    const isLandscapeScreen = window.innerWidth > window.innerHeight;
    if (isLandscapeScreen) {
      return "h-[100vh] w-auto object-contain transition-transform duration-200";
    } else {
      return "w-[100vw] h-auto object-contain transition-transform duration-200";
    }
  }, [fitToScreen, autoRotateMode]);

  // Toggle fullscreen
  const toggleFullscreen = useCallback(() => {
    if (!document.fullscreenElement) {
      document.documentElement.requestFullscreen();
      setIsFullscreen(true);
    } else {
      document.exitFullscreen();
      setIsFullscreen(false);
    }
  }, []);

  // Get images for current view mode
  const getVisibleImages = useCallback(() => {
    if (images.length === 0) {
      return [];
    }

    const imagesPerView = {
      single: 1,
      double: 2,
      triple: 3,
      quad: 4,
    }[viewMode];

    const visibleImages = [];
    for (let i = 0; i < imagesPerView; i++) {
      const index = (currentIndex + i) % images.length;
      const image = images[index];
      if (image) {
        visibleImages.push(image);
      }
    }
    return visibleImages;
  }, [images, currentIndex, viewMode]);

  // Optimized navigation with efficient cross-collection handling
  const navigateToImage = useCallback(
    async (direction: 'next' | 'prev') => {
      if (images.length === 0 || !currentImageId) return;

      const imagesPerView = {
        single: 1,
        double: 2,
        triple: 3,
        quad: 4,
      }[viewMode];

      let newIndex = currentIndex;
      
      if (direction === 'next') {
        newIndex = currentIndex + imagesPerView;
        if (newIndex >= images.length) {
          // At end of collection - handle cross-collection if enabled
          if (crossCollectionNav && navigationMode === 'paging' && !isShuffleMode && collectionNav?.hasNext) {
            try {
              const result = await navigateCrossCollection({
                collectionId: collectionId!,
                imageId: currentImageId,
                direction,
                sortBy: 'updatedAt',
                sortDirection: 'desc'
              });

              if (result.hasTarget && result.targetImageId) {
                // Navigate to specific image in next collection
                navigate(`/collections/${result.targetCollectionId}/viewer?imageId=${result.targetImageId}`);
              }
            } catch (error) {
              console.error('Cross-collection navigation failed:', error);
              // Fallback to simple navigation
              navigate(`/collections/${collectionNav.nextCollectionId}/viewer`);
            }
            return;
          }
          return; // Stay at current image
        }
      } else {
        newIndex = currentIndex - imagesPerView;
        if (newIndex < 0) {
          // At start of collection - handle cross-collection if enabled
          if (crossCollectionNav && navigationMode === 'paging' && !isShuffleMode && collectionNav?.hasPrevious) {
            try {
              const result = await navigateCrossCollection({
                collectionId: collectionId!,
                imageId: currentImageId,
                direction,
                sortBy: 'updatedAt',
                sortDirection: 'desc'
              });

              if (result.hasTarget && result.targetImageId) {
                // Navigate to specific image in previous collection (last image)
                navigate(`/collections/${result.targetCollectionId}/viewer?imageId=${result.targetImageId}`);
              }
            } catch (error) {
              console.error('Cross-collection navigation failed:', error);
              // Fallback to simple navigation
              navigate(`/collections/${collectionNav.previousCollectionId}/viewer`);
            }
            return;
          }
          return; // Stay at current image
        }
      }

      // Update to new image (synchronous, fast)
      const newImageId = images[newIndex].id;
      setCurrentImageId(newImageId);
      setZoom(1);
      setRotation(0);
      setPanPosition({ x: 0, y: 0 });
    },
    [images, currentIndex, currentImageId, viewMode, crossCollectionNav, navigationMode, isShuffleMode, collectionNav, navigate, navigateCrossCollection, collectionId]
  );


  // Keyboard navigation
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      switch (e.key) {
        case 'Escape':
          navigate(`/collections/${collectionId}`);
          break;
        case 'ArrowLeft':
          navigateToImage('prev');
          break;
        case 'ArrowRight':
          navigateToImage('next');
          break;
        case '+':
        case '=':
          setZoom((z) => Math.min(z + 0.25, 5));
          break;
        case '-':
          setZoom((z) => Math.max(z - 0.25, 0.25));
          break;
        case '0':
          if (e.ctrlKey) {
            // Ctrl+0: Hide/show all controls and DevTools
            const newHideState = !hideAllControls;
            setHideAllControls(newHideState);
            setHideDevTools(newHideState);
          } else {
            // 0: Reset zoom and rotation
            setZoom(1);
            setRotation(0);
          }
          break;
        case 'r':
          setRotation((r) => (r + 90) % 360);
          break;
        case 'f':
          if (e.ctrlKey || e.metaKey) {
            setRotation((r) => (r + 180) % 360);
          }
          break;
        case 'i':
          setShowInfo((s) => !s);
          break;
        case '?':
        case 'h':
          setShowHelp((s) => !s);
          break;
        case 't':
        case 'T':
          toggleImagePreviewSidebar();
          break;
        case ' ': 
          e.preventDefault();
          setIsSlideshow((s) => !s);
          break;
        case '1':
          saveViewMode('single');
          break;
        case '2':
          saveViewMode('double');
          break;
        case '3':
          saveViewMode('triple');
          break;
        case '4':
          saveViewMode('quad');
          break;
        case 'f':
        case 'F11':
          e.preventDefault();
          toggleFullscreen();
          break;
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [collectionId, navigate, navigateToImage, saveViewMode, toggleFullscreen, toggleImagePreviewSidebar, hideAllControls, setHideDevTools]);

  // Hotkey handlers for collection navigation (always move to first image)
  const handleNextCollection = useCallback(() => {
    if (collectionNav?.hasNext && collectionNav.nextCollectionId) {
      // Navigate to next collection and load first image
      navigate(`/collections/${collectionNav.nextCollectionId}/viewer`);
    }
  }, [collectionNav?.hasNext, collectionNav?.nextCollectionId, navigate]);

  const handlePrevCollection = useCallback(() => {
    if (collectionNav?.hasPrevious && collectionNav.previousCollectionId) {
      // Navigate to previous collection and load first image
      navigate(`/collections/${collectionNav.previousCollectionId}/viewer`);
    }
  }, [collectionNav?.hasPrevious, collectionNav?.previousCollectionId, navigate]);

  // Setup hotkeys for collection navigation (Ctrl+Arrow keys)
  useHotkeys([
    CommonHotkeys.nextCollection(handleNextCollection),
    CommonHotkeys.prevCollection(handlePrevCollection),
  ], {
    enabled: !isSlideshow,
  });

  // Slideshow
  useEffect(() => {
    if (isSlideshow) {
      slideshowRef.current = setInterval(() => {
        if (isShuffleMode && images.length > 0) {
          // Navigate to random image
          const randomIndex = Math.floor(Math.random() * images.length);
          const randomImage = images[randomIndex];
          if (randomImage) {
            setCurrentImageId(randomImage.id);
          }
        } else {
          navigateToImage('next');
        }
      }, slideshowInterval);
    } else {
      if (slideshowRef.current) {
        clearInterval(slideshowRef.current);
      }
    }

    return () => {
      if (slideshowRef.current) {
        clearInterval(slideshowRef.current);
      }
    };
  }, [isSlideshow, slideshowInterval, isShuffleMode, navigateToImage, images, collectionId, navigate]);

  // Listen for fullscreen changes
  useEffect(() => {
    const handleFullscreenChange = () => {
      setIsFullscreen(!!document.fullscreenElement);
    };

    document.addEventListener('fullscreenchange', handleFullscreenChange);
    return () => document.removeEventListener('fullscreenchange', handleFullscreenChange);
  }, []);

  // Scroll to current image in scroll mode
  useEffect(() => {
    if (navigationMode === 'scroll' && currentImageId && currentIndex >= 0) {
      // Small delay to ensure DOM is ready
      setTimeout(() => {
        const imageElement = document.getElementById(`image-${currentImageId}`);
        if (imageElement) {
          imageElement.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
      }, 100);
    }
  }, [navigationMode, currentImageId, currentIndex]);

  // Image preloading with persistent cache (only in paging mode)
  useEffect(() => {
    if (navigationMode === 'scroll' || currentIndex === -1 || images.length === 0) {
      return;
    }

    const maxPreload = parseInt(localStorage.getItem('maxPreloadImages') || '20');
    const preloadCache = preloadedImagesRef.current;

    // Determine which images to preload (next N images from current position)
    const imagesToPreload: string[] = [];
    for (let i = 1; i <= Math.min(maxPreload, images.length - 1); i++) {
      const nextIndex = (currentIndex + i) % images.length;
      const nextImage = images[nextIndex];
      if (nextImage && !preloadCache.has(nextImage.id)) {
        imagesToPreload.push(nextImage.id);
      }
    }

    // Preload only new images (not already in cache)
    imagesToPreload.forEach(imageId => {
      const img = new Image();
      img.src = `/api/v1/images/${collectionId}/${imageId}/file`;
      
      img.onload = () => {
        // console.log(`Preloaded image ${imageId}`);
      };
      
      img.onerror = () => {
        console.warn(`Failed to preload image ${imageId}`);
        preloadCache.delete(imageId);
      };
      
      preloadCache.set(imageId, img);
    });

    // Cleanup: Remove old images that are too far from current position
    const minKeepIndex = Math.max(0, currentIndex - 10);
    const maxKeepIndex = Math.min(images.length - 1, currentIndex + maxPreload + 10);
    
    preloadCache.forEach((img, imageId) => {
      const imgIndex = images.findIndex(i => i.id === imageId);
      if (imgIndex !== -1 && (imgIndex < minKeepIndex || imgIndex > maxKeepIndex)) {
        img.onload = null;
        img.onerror = null;
        img.src = '';
        preloadCache.delete(imageId);
      }
    });
  }, [currentIndex, images, collectionId, navigationMode]);

  if (!currentImage && images.length === 0) {
    return <LoadingSpinner fullScreen text="Loading images..." />;
  }
  
  if (!currentImage && images.length > 0) {
    // Images loaded but current not found, will auto-redirect
    return <LoadingSpinner fullScreen text="Loading..." />;
  }

  return (
    <div key={`${collectionId}-${initialImageId}`} className="fixed inset-0 bg-black z-50 flex">
      {/* Collection Navigation Sidebar (toggleable) */}
      {showCollectionSidebar && !hideAllControls && (
        <CollectionNavigationSidebar
          key={`collection-sidebar-${collectionId}`}
          collectionId={collectionId!}
          sortBy="updatedAt"
          sortDirection="desc"
          onNavigate={(newCollectionId, firstImageId) => {
            // console.log(`[ImageViewer Sidebar] Navigating to collection ${newCollectionId}, image ${firstImageId}`);
            // Navigate directly to viewer with first image if available
            if (firstImageId) {
              navigate(`/collections/${newCollectionId}/viewer?imageId=${firstImageId}`);
            } else {
              // Fallback to collection detail if no firstImageId
              navigate(`/collections/${newCollectionId}`);
            }
          }}
        />
      )}
      
      {/* Main Viewer Area */}
      <div className="flex-1 flex">
        {/* Image Display Area */}
        <div className="flex-1 flex flex-col">
      {/* Header */}
      {!hideAllControls && (
        <div className="absolute top-0 left-0 right-0 z-10 bg-gradient-to-b from-black/80 to-transparent p-4">
        <div className="flex items-center justify-between">
          <div className="flex items-center space-x-4">
            <button
              onClick={toggleCollectionSidebar}
              className={`p-2 hover:bg-white/10 rounded-lg transition-colors ${
                showCollectionSidebar ? 'bg-primary-500' : ''
              }`}
              title={showCollectionSidebar ? 'Hide Collections' : 'Show Collections'}
            >
              <PanelLeft className="h-6 w-6 text-white" />
            </button>
            <button
              onClick={() => navigate(`/collections/${collectionId}`)}
              className="p-2 hover:bg-white/10 rounded-lg transition-colors"
            >
              <X className="h-6 w-6 text-white" />
            </button>
            <div className="text-white">
              <h2 className="font-semibold">{currentImage.filename}</h2>
              <p className="text-sm text-slate-300">
                {currentIndex + 1} of {totalImages > 0 ? totalImages : images.length}
                {totalImages > images.length && (
                  <span className="text-primary-400"> • Loaded: {images.length}</span>
                )}
                {' • '}{currentImage.width} × {currentImage.height}
              </p>
            </div>
          </div>

          <div className="flex items-center space-x-2">
            {/* Navigation Mode Toggle */}
            <button
              onClick={toggleNavigationMode}
              className={`p-2 hover:bg-white/10 rounded-lg transition-colors ${
                navigationMode === 'scroll' ? 'bg-primary-500' : ''
              }`}
              title={navigationMode === 'paging' ? 'Switch to Scroll Mode' : 'Switch to Paging Mode'}
            >
              <ArrowDownUp className="h-5 w-5 text-white" />
            </button>

            {/* Cross-Collection Navigation Toggle */}
            <button
              onClick={toggleCrossCollectionNav}
              disabled={navigationMode === 'scroll' || isShuffleMode}
              className={`p-2 hover:bg-white/10 rounded-lg transition-colors disabled:opacity-30 disabled:cursor-not-allowed ${
                crossCollectionNav ? 'bg-purple-500' : ''
              }`}
              title={
                navigationMode === 'scroll' 
                  ? 'Cross-collection navigation not available in scroll mode' 
                  : isShuffleMode
                  ? 'Cross-collection navigation not available in shuffle mode'
                  : crossCollectionNav 
                  ? 'Disable cross-collection navigation (wrap within current collection)' 
                  : 'Enable cross-collection navigation (move to prev/next collection at boundaries)'
              }
            >
              <Link2 className="h-5 w-5 text-white" />
            </button>

            {/* Auto-Rotate Mode Toggle */}
            <button
              onClick={toggleAutoRotateMode}
              className={`p-2 hover:bg-white/10 rounded-lg transition-colors ${
                autoRotateMode === 'portrait' ? 'bg-blue-500' : 
                autoRotateMode === 'landscape' ? 'bg-green-500' : ''
              }`}
              title={
                autoRotateMode === 'none' 
                  ? 'Auto-rotate: None (keep original orientation)' 
                  : autoRotateMode === 'portrait'
                  ? 'Auto-rotate: Portrait (rotate landscape images to portrait)'
                  : 'Auto-rotate: Landscape (rotate portrait images to landscape)'
              }
            >
              {getAutoRotateIcon()}
            </button>

            {/* View Mode Controls */}
            <div className="flex items-center gap-1 bg-black/20 rounded-lg p-1">
              <button
                onClick={() => saveViewMode('single')}
                className={`p-1.5 rounded transition-colors ${
                  viewMode === 'single'
                    ? 'bg-primary-500 text-white'
                    : 'text-slate-300 hover:text-white hover:bg-white/10'
                }`}
                title="Single View (1)"
              >
                <Monitor className="h-4 w-4" />
              </button>
              <button
                onClick={() => saveViewMode('double')}
                className={`p-1.5 rounded transition-colors ${
                  viewMode === 'double'
                    ? 'bg-primary-500 text-white'
                    : 'text-slate-300 hover:text-white hover:bg-white/10'
                }`}
                title="Double Page View (2)"
              >
                <Layout className="h-4 w-4" />
              </button>
              <button
                onClick={() => saveViewMode('triple')}
                className={`p-1.5 rounded transition-colors ${
                  viewMode === 'triple'
                    ? 'bg-primary-500 text-white'
                    : 'text-slate-300 hover:text-white hover:bg-white/10'
                }`}
                title="Triple Page View (3)"
              >
                <Grid className="h-4 w-4" />
              </button>
              <button
                onClick={() => saveViewMode('quad')}
                className={`p-1.5 rounded transition-colors ${
                  viewMode === 'quad'
                    ? 'bg-primary-500 text-white'
                    : 'text-slate-300 hover:text-white hover:bg-white/10'
                }`}
                title="Quad View (4)"
              >
                <Maximize2 className="h-4 w-4" />
              </button>
            </div>

            <button
              onClick={() => setShowInfo(!showInfo)}
              className="p-2 hover:bg-white/10 rounded-lg transition-colors"
              title="Toggle Info (I)"
            >
              <Info className="h-5 w-5 text-white" />
            </button>
            <button
              onClick={toggleFitToScreen}
              className={`p-2 hover:bg-white/10 rounded-lg transition-colors ${
                fitToScreen ? 'bg-primary-500' : ''
              }`}
              title={fitToScreen ? "Fit to Viewport" : "Fit to Screen (100vh/100vw)"}
            >
              <Expand className="h-5 w-5 text-white" />
            </button>
            <button
              onClick={() => setRotation((r) => (r + 90) % 360)}
              className="p-2 hover:bg-white/10 rounded-lg transition-colors"
              title="Rotate (R)"
            >
              <RotateCw className="h-5 w-5 text-white" />
            </button>
            <button
              onClick={() => setRotation((r) => (r + 180) % 360)}
              className="p-2 hover:bg-white/10 rounded-lg transition-colors"
              title="Flip (Ctrl+F)"
            >
              <RotateCcw className="h-5 w-5 text-white" />
            </button>
            <button
              onClick={() => setZoom((z) => Math.max(z - 0.25, 0.25))}
              className="p-2 hover:bg-white/10 rounded-lg transition-colors"
              title="Zoom Out (-)"
            >
              <ZoomOut className="h-5 w-5 text-white" />
            </button>
            <span 
              className="text-white text-sm px-2" 
              title={navigationMode === 'scroll' ? 'Ctrl+Wheel to zoom in scroll mode' : 'Zoom level'}
            >
              {Math.round(zoom * 100)}%
            </span>
            <button
              onClick={() => setZoom((z) => Math.min(z + 0.25, 5))}
              className="p-2 hover:bg-white/10 rounded-lg transition-colors"
              title="Zoom In (+)"
            >
              <ZoomIn className="h-5 w-5 text-white" />
            </button>
            <button
              onClick={toggleFullscreen}
              className="p-2 hover:bg-white/10 rounded-lg transition-colors"
              title="Fullscreen (F)"
            >
              <Maximize2 className="h-5 w-5 text-white" />
            </button>

            {/* Slideshow Controls */}
            <div className="flex items-center gap-1 bg-black/20 rounded-lg p-1">
              <input
                type="number"
                min="500"
                max="60000"
                step="500"
                value={slideshowInterval}
                onChange={(e) => {
                  const newInterval = parseInt(e.target.value) || 3000;
                  setSlideshowInterval(newInterval);
                  localStorage.setItem('slideshowInterval', newInterval.toString());
                }}
                className="w-16 px-2 py-1 bg-slate-700 border border-slate-600 rounded text-white text-xs text-center focus:outline-none focus:ring-1 focus:ring-primary-500"
                title="Slideshow interval (ms)"
              />
              <button
                onClick={() => setIsShuffleMode(!isShuffleMode)}
                className={`p-1.5 rounded transition-colors ${
                  isShuffleMode ? 'bg-primary-500 text-white' : 'text-slate-300 hover:text-white hover:bg-white/10'
                }`}
                title="Shuffle Mode"
              >
                <Shuffle className="h-4 w-4" />
              </button>
              <button
                onClick={() => setIsSlideshow(!isSlideshow)}
                className={`p-1.5 rounded transition-colors ${
                  isSlideshow ? 'bg-primary-500 text-white' : 'text-slate-300 hover:text-white hover:bg-white/10'
                }`}
                title="Slideshow (Space)"
              >
                {isSlideshow ? (
                  <Pause className="h-4 w-4 text-white" />
                ) : (
                  <Play className="h-4 w-4 text-white" />
                )}
              </button>
            </div>

            {/* Image Preview Sidebar Toggle */}
            <button
              onClick={toggleImagePreviewSidebar}
              className={`p-2 hover:bg-white/10 rounded-lg transition-colors ${
                showImagePreviewSidebar ? 'bg-primary-500' : ''
              }`}
              title={showImagePreviewSidebar ? 'Hide Thumbnails (T)' : 'Show Thumbnails (T)'}
            >
              <Images className="h-5 w-5 text-white" />
            </button>

            {/* Help Button */}
            <button
              onClick={() => setShowHelp(!showHelp)}
              className="p-2 hover:bg-white/10 rounded-lg transition-colors"
              title="Help (? or H)"
            >
              <HelpCircle className="h-5 w-5 text-white" />
            </button>
          </div>
        </div>
        </div>
      )}

      {/* Main Images */}
      <div 
        ref={imageContainerRef}
        className={`flex-1 overflow-auto ${navigationMode === 'scroll' ? 'flex items-start justify-center' : 'flex items-center justify-center'}`}
      >
        {navigationMode === 'scroll' ? (
          // Scroll Mode: Show all images in a vertical list
          <div className="flex flex-col gap-4 py-4 w-full max-w-[100vw] items-center">
            {images.map((image, index) => (
              <div 
                key={image.id} 
                id={`image-${image.id}`}
                className="flex flex-col items-center relative"
              >
                {/* Highlight current image */}
                {currentIndex === index && (
                  <div className="absolute -inset-2 border-2 border-primary-500 rounded-lg pointer-events-none"></div>
                )}
                <MediaDisplay
                  src={`/api/v1/images/${collectionId}/${image.id}/file`}
                  alt={image.filename}
                  className={getImageClass()}
                  style={{
                    transform: `scale(${zoom * getAutoRotatedScale(image.width, image.height)}) rotate(${rotation + getAutoRotation(image.width, image.height)}deg)`,
                    transformOrigin: 'center center',
                    transition: 'transform 0.2s ease-out',
                  }}
                  loading="lazy"
                  controls={true}
                  autoPlay={false}
                  muted={true}
                  loop={false}
                />
                <div className="mt-2 text-white text-sm text-center">
                  {index + 1} / {images.length} - {image.filename}
                </div>
              </div>
            ))}
          </div>
        ) : (
          // Paging Mode: Show images based on view mode
          <div 
            className={`flex items-center justify-center gap-2 ${
              viewMode === 'single' ? 'flex-col' : 
              viewMode === 'double' ? 'flex-row' :
              viewMode === 'triple' ? 'flex-row' :
              'grid grid-cols-2 gap-2'
            }`}
            style={{
              transform: `scale(${zoom})`,
              transformOrigin: 'center center',
              maxWidth: '100%',
              maxHeight: '100%',
            }}
          >
            {getVisibleImages().map((image, index) => (
            <div
              key={`${image.id}-${index}`}
              className={`relative ${
                viewMode === 'single' ? 'w-full h-full' :
                viewMode === 'double' ? 'w-1/2 h-full' :
                viewMode === 'triple' ? 'w-1/3 h-full' :
                'w-full h-full'
              }`}
            >
              {imageLoading && index === 0 && (
                <div className="absolute inset-0 flex items-center justify-center bg-black/50">
                  <LoadingSpinner text="Loading..." />
                </div>
              )}
              {imageError && index === 0 && (
                <div className="absolute inset-0 flex items-center justify-center bg-black/50 text-white">
                  <div className="text-center">
                    <p className="text-red-500 text-lg mb-2">Failed to load image</p>
                    <button
                      onClick={() => {
                        setImageError(false);
                        setImageLoading(true);
                      }}
                      className="px-4 py-2 bg-primary-500 rounded-lg hover:bg-primary-600"
                    >
                      Retry
                    </button>
                  </div>
                </div>
              )}
              <MediaDisplay
                src={`/api/v1/images/${collectionId}/${image.id}/file`}
                alt={image.filename}
                className={getImageClass()}
                style={{
                  transform: `scale(${zoom * getAutoRotatedScale(image.width, image.height)}) rotate(${rotation + getAutoRotation(image.width, image.height)}deg)`,
                }}
                onLoad={() => index === 0 && setImageLoading(false)}
                onError={() => {
                  if (index === 0) {
                    setImageLoading(false);
                    setImageError(true);
                  }
                }}
                controls={true}
                autoPlay={false}
                muted={true}
                loop={false}
              />
              {/* Image index indicator for multi-view */}
              {viewMode !== 'single' && (
                <div className="absolute top-2 left-2 bg-black/70 text-white text-xs px-2 py-1 rounded">
                  {images.findIndex(img => img.id === image.id) + 1}
                </div>
              )}
            </div>
            ))}
          </div>
        )}
      </div>

      {/* Navigation Buttons (Only in paging mode) */}
        {navigationMode === 'paging' && !hideAllControls && (
          <>
            <button
              onClick={() => navigateToImage('prev')}
              disabled={isCrossNavigating}
              className="absolute left-4 top-1/2 -translate-y-1/2 p-3 bg-black/50 hover:bg-black/70 disabled:opacity-50 disabled:cursor-not-allowed rounded-full transition-colors"
              title="Previous (←)"
            >
              {isCrossNavigating ? (
                <LoadingSpinner size="sm" />
              ) : (
                <ChevronLeft className="h-8 w-8 text-white" />
              )}
            </button>
            <button
              onClick={() => navigateToImage('next')}
              disabled={isCrossNavigating}
              className="absolute right-4 top-1/2 -translate-y-1/2 p-3 bg-black/50 hover:bg-black/70 disabled:opacity-50 disabled:cursor-not-allowed rounded-full transition-colors"
              title="Next (→)"
            >
              {isCrossNavigating ? (
                <LoadingSpinner size="sm" />
              ) : (
                <ChevronRight className="h-8 w-8 text-white" />
              )}
            </button>
          </>
        )}

      {/* Info Panel */}
      {showInfo && (
        <div className="absolute bottom-0 left-0 right-0 bg-gradient-to-t from-black/80 to-transparent p-6">
          <div className="grid grid-cols-2 md:grid-cols-4 gap-4 text-white">
            <div>
              <p className="text-sm text-slate-400">Filename</p>
              <p className="font-medium">{currentImage.filename}</p>
            </div>
            <div>
              <p className="text-sm text-slate-400">Dimensions</p>
              <p className="font-medium">
                {currentImage.width} × {currentImage.height}
              </p>
            </div>
            <div>
              <p className="text-sm text-slate-400">Size</p>
              <p className="font-medium">{(currentImage.fileSize / 1024 / 1024).toFixed(2)} MB</p>
            </div>
            <div>
              <p className="text-sm text-slate-400">Path</p>
              <p className="font-medium truncate">{currentImage.relativePath}</p>
            </div>
          </div>
        </div>
      )}

      {/* Keyboard Shortcuts Help (Only show when help button clicked) */}
      {showHelp && (
        <div className="absolute bottom-4 left-1/2 -translate-x-1/2 bg-black/90 rounded-lg p-4 max-w-2xl">
          <div className="text-white text-sm space-y-2">
            <p className="font-bold text-center mb-2">Keyboard Shortcuts</p>
            <div className="grid grid-cols-2 gap-x-6 gap-y-1">
              <p>← → : Navigate</p>
              <p>Esc : Close</p>
              <p>+/- : Zoom</p>
              <p>0 : Reset Zoom/Rotation</p>
              <p>Ctrl+Wheel : Zoom (Scroll Mode)</p>
              <p>R : Rotate</p>
              <p>Ctrl+F : Flip (180°)</p>
              <p>I : Info</p>
              <p>T : Thumbnails</p>
              <p>Space : Slideshow</p>
              <p>1-4 : View Modes</p>
              <p>F : Fullscreen</p>
              <p>Ctrl+0 : Hide Controls</p>
              <p>Ctrl+←/→ : Collection Nav</p>
            </div>
            <p className="text-center text-xs text-slate-400 mt-2">
              Press ? or click Help icon to toggle
            </p>
            {crossCollectionNav && navigationMode === 'paging' && !isShuffleMode && (
              <div className="mt-2 pt-2 border-t border-slate-700">
                <p className="text-center text-xs text-purple-400">
                  🔗 Cross-Collection Mode: Navigate to prev/next collection at boundaries
                </p>
              </div>
            )}
          </div>
        </div>
      )}
        </div>
        {/* End Image Display Area */}
        
        {/* Image Preview Sidebar (thumbnails strip on right) */}
        {showImagePreviewSidebar && !hideAllControls && (
          <ImagePreviewSidebar
            key={`preview-${collectionId}`}
            images={images}
            currentImageId={currentImageId}
            collectionId={collectionId!}
            onImageClick={(imageId) => setCurrentImageId(imageId)}
          />
        )}
      </div>
      {/* End Main Viewer Area */}
    </div>
  );
};

export default ImageViewer;
