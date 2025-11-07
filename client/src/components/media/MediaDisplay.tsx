import React, { useRef, useEffect, useState, useCallback } from 'react';
import { isVideoFile, isAnimatedImage, getMimeType } from '../../utils/mediaUtils';

interface MediaDisplayProps {
  src: string;
  alt: string;
  filename?: string; // Optional filename for type detection when URL doesn't contain it
  className?: string;
  style?: React.CSSProperties;
  loading?: 'lazy' | 'eager';
  onLoad?: () => void;
  onError?: () => void;
  controls?: boolean; // For video elements
  autoPlay?: boolean; // For video elements
  muted?: boolean; // For video elements
  loop?: boolean; // For video elements
}

/**
 * Smart media display component that renders images or videos appropriately
 * based on the file type
 * 
 * For videos, supports TikTok/YouTube Shorts style:
 * - Auto-play when loaded
 * - Unmuted by default
 * - Loop by default
 * - Better timeline scrubbing for short videos
 */
export const MediaDisplay: React.FC<MediaDisplayProps> = ({
  src,
  alt,
  filename: providedFilename,
  className = '',
  style = {},
  loading = 'lazy',
  onLoad,
  onError,
  controls = true,
  autoPlay = true, // Changed default to true for TikTok-style experience
  muted = false, // Changed default to false (unmuted) for TikTok-style experience
  loop = true, // Changed default to true for TikTok-style experience
}) => {
  const videoRef = useRef<HTMLVideoElement>(null);
  const [duration, setDuration] = useState<number>(0);
  const [isShortVideo, setIsShortVideo] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [isPlaying, setIsPlaying] = useState(autoPlay);
  const [isMutedState, setIsMutedState] = useState(muted);
  const [showControls, setShowControls] = useState(false); // Hidden by default
  const [isPointerOver, setIsPointerOver] = useState(false);
  const [isUserScrubbing, setIsUserScrubbing] = useState(false);
  const [scrubbingValue, setScrubbingValue] = useState<number | null>(null); // Track scrubbing value separately
  const hideControlsTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pendingSeekRef = useRef<number | null>(null);
  
  // Use provided filename if available, otherwise try to extract from URL
  const filename = providedFilename || src.split('/').pop() || '';
  const isVideo = isVideoFile(filename);

  const seekStep = isShortVideo ? 0.05 : 0.5;

  const getEffectiveDuration = useCallback(() => {
    const video = videoRef.current;
    if (video) {
      if (typeof video.duration === 'number' && Number.isFinite(video.duration) && video.duration > 0) {
        return video.duration;
      }
      if (video.seekable && video.seekable.length > 0) {
        const end = video.seekable.end(video.seekable.length - 1);
        if (Number.isFinite(end) && end > 0) {
          return end;
        }
      }
    }
    if (Number.isFinite(duration) && duration > 0) {
      return duration;
    }
    return null;
  }, [duration]);

  const clampToDuration = useCallback((value: number) => {
    if (!Number.isFinite(value)) {
      return 0;
    }

    const effectiveDuration = getEffectiveDuration();
    if (effectiveDuration && effectiveDuration > 0) {
      return Math.min(Math.max(0, value), effectiveDuration);
    }

    return Math.max(0, value);
  }, [getEffectiveDuration]);

  const canSeekImmediately = useCallback(() => {
    const video = videoRef.current;
    if (!video) {
      return false;
    }
    if (video.readyState >= 1 && Number.isFinite(video.duration) && video.duration > 0) {
      return true;
    }
    if (video.seekable && video.seekable.length > 0) {
      const end = video.seekable.end(video.seekable.length - 1);
      return Number.isFinite(end) && end > 0;
    }
    return false;
  }, []);

  const formatTime = (value: number): string => {
    if (!isFinite(value)) return '0:00';
    const totalSeconds = Math.max(0, Math.floor(value));
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${minutes}:${seconds.toString().padStart(2, '0')}`;
  };

  const applySeek = useCallback((video: HTMLVideoElement, target: number) => {
    const clamped = clampToDuration(target);
    video.currentTime = clamped;
    setCurrentTime(clamped);
    setScrubbingValue(null);
    pendingSeekRef.current = null;
  }, [clampToDuration]);

  const togglePlayPause = () => {
    const video = videoRef.current;
    if (!video) return;
    if (video.paused) {
      video.play().catch(() => {
        // Ignore play errors triggered by browser auto-play policies
      });
    } else {
      video.pause();
    }
  };

  const toggleMute = () => {
    const video = videoRef.current;
    if (!video) return;
    const nextMuted = !isMutedState;
    video.muted = nextMuted;
    setIsMutedState(nextMuted);
  };

  const handleSeek = useCallback((value: number) => {
    const video = videoRef.current;
    if (!video || Number.isNaN(value) || !Number.isFinite(value)) return;

    const clampedValue = clampToDuration(value);
    if (canSeekImmediately()) {
      applySeek(video, clampedValue);
    } else {
      pendingSeekRef.current = clampedValue;
      setCurrentTime(clampedValue);
      setScrubbingValue(null);
    }
  }, [applySeek, canSeekImmediately, clampToDuration]);

  const handleToggleFullscreen = () => {
    const video = videoRef.current;
    if (!video) return;

    if (document.fullscreenElement) {
      void document.exitFullscreen?.();
    } else {
      void video.requestFullscreen?.();
    }
  };
  
  // Handle video-specific logic
  useEffect(() => {
    if (!isVideo || !videoRef.current) return;
    
    const video = videoRef.current;
    
    // Detect if video is short (less than 60 seconds) for better timeline control
    const handleLoadedMetadata = () => {
      if (Number.isFinite(video.duration) && video.duration > 0) {
        setDuration(video.duration);
        setIsShortVideo(video.duration < 60);
      }

      if (pendingSeekRef.current !== null) {
        applySeek(video, pendingSeekRef.current);
      }

      if (onLoad) {
        onLoad();
      }
    };
    
    // Auto-play when video is ready (TikTok/YouTube Shorts style)
    const handleCanPlay = () => {
      if (autoPlay && video.paused) {
        video.play().catch((err) => {
          // Auto-play may fail due to browser policies, log but don't throw
          console.log('Auto-play prevented:', err);
        });
      }
    };
    
    // Handle video end - restart if looping
    const handleEnded = () => {
      if (loop && video) {
        video.currentTime = 0;
        video.play().catch(() => {
          // Ignore play errors on loop
        });
      }
    };
    
    const handleTimeUpdate = () => {
      // Don't update currentTime if user is scrubbing - wait until they finish
      if (!isUserScrubbing && scrubbingValue === null) {
        setCurrentTime(video.currentTime);
      }
    };

    const handlePlay = () => setIsPlaying(true);
    const handlePause = () => setIsPlaying(false);

    video.addEventListener('loadedmetadata', handleLoadedMetadata);
    video.addEventListener('canplay', handleCanPlay);
    video.addEventListener('ended', handleEnded);
    video.addEventListener('timeupdate', handleTimeUpdate);
    video.addEventListener('play', handlePlay);
    video.addEventListener('pause', handlePause);

    if (video.readyState >= 1) {
      handleLoadedMetadata();
      setCurrentTime(video.currentTime);
      setIsPlaying(!video.paused);
    }
    
    // Cleanup
    return () => {
      video.removeEventListener('loadedmetadata', handleLoadedMetadata);
      video.removeEventListener('canplay', handleCanPlay);
      video.removeEventListener('ended', handleEnded);
      video.removeEventListener('timeupdate', handleTimeUpdate);
      video.removeEventListener('play', handlePlay);
      video.removeEventListener('pause', handlePause);
    };
  }, [isVideo, autoPlay, loop, onLoad, src, isUserScrubbing, scrubbingValue]);
  
  // Handle src changes - restart video if it's a new source
  useEffect(() => {
    if (!isVideo || !videoRef.current) return;
    
    const video = videoRef.current;
    let isMounted = true;
    
    // When src changes, reset and play if autoPlay is enabled
    const handleSrcChange = () => {
      if (!isMounted) return;
      
      if (autoPlay) {
        video.currentTime = 0;
        // Small delay to ensure video is ready
        const playPromise = video.play();
        if (playPromise !== undefined) {
          playPromise.catch(() => {
            // Auto-play may fail, that's okay (browser policy)
          });
        }
      } else {
        // Even if not autoplaying, reset to start
        video.currentTime = 0;
      }
      setCurrentTime(0);
      setScrubbingValue(null);
      setIsPlaying(autoPlay);
      setShowControls(false); // Hide controls initially
      video.muted = isMutedState;
    };
    
    // Reset immediately and also when video can play
    handleSrcChange();
    video.addEventListener('canplay', handleSrcChange, { once: true });
    
    return () => {
      isMounted = false;
      video.removeEventListener('canplay', handleSrcChange);
    };
  }, [src, isVideo, autoPlay]); // Removed isMutedState from dependencies to prevent unnecessary resets
  
  useEffect(() => {
    setIsMutedState(muted);
  }, [muted]);

  useEffect(() => {
    if (videoRef.current) {
      videoRef.current.muted = isMutedState;
    }
  }, [isMutedState]);

  // Enhance video controls with keyboard shortcuts for precise seeking
  // Note: Only works when video is focused (clicked on) to avoid interfering with viewer navigation
  useEffect(() => {
    if (!isVideo || !videoRef.current || !controls) return;
    
    const video = videoRef.current;
    
    const handleKeyDown = (e: KeyboardEvent) => {
      // Only handle if video element itself has focus (user clicked on it)
      if (document.activeElement !== video) return;
      
      // Arrow keys for precise seeking (0.1s steps for short videos, 1s for longer)
      const step = isShortVideo ? 0.1 : 1;
      
      if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
        e.stopPropagation(); // Prevent event from bubbling to viewer navigation
        e.preventDefault();
        
        if (e.key === 'ArrowLeft') {
          video.currentTime = Math.max(0, video.currentTime - step);
        } else {
          video.currentTime = Math.min(video.duration || 0, video.currentTime + step);
        }
      }
    };
    
    // Add keyboard listener to video element
    video.addEventListener('keydown', handleKeyDown, true); // Use capture phase
    
    // Make video focusable for keyboard control (users can click video to focus it)
    if (!video.hasAttribute('tabindex')) {
      video.setAttribute('tabindex', '-1'); // -1 means focusable but not in tab order
    }
    
    return () => {
      video.removeEventListener('keydown', handleKeyDown, true);
    };
  }, [isVideo, isShortVideo, controls]);

  useEffect(() => {
    if (!controls) return;

    const clearExistingTimeout = () => {
      if (hideControlsTimeoutRef.current) {
        clearTimeout(hideControlsTimeoutRef.current);
        hideControlsTimeoutRef.current = null;
      }
    };

    // When pointer is over the video or user is scrubbing, keep controls visible
    if (isPointerOver || isUserScrubbing) {
      clearExistingTimeout();
      setShowControls(true);
      return;
    }

    // If the video is paused and the pointer has left, allow the onMouseLeave timeout to handle hiding
    if (!isPlaying) {
      // Do not force controls to reappear; simply exit
      return;
    }

    // Video is playing, pointer not over, not scrubbing: auto-hide after 2 seconds
    clearExistingTimeout();
    hideControlsTimeoutRef.current = setTimeout(() => {
      setShowControls(false);
      hideControlsTimeoutRef.current = null;
    }, 2000);

    return () => {
      clearExistingTimeout();
    };
  }, [controls, isPlaying, isPointerOver, isUserScrubbing]);

  useEffect(() => {
    if (!controls || !isUserScrubbing) return;

    if (typeof window === 'undefined') return;

    const handlePointerUp = () => {
      if (scrubbingValue !== null && videoRef.current) {
        const value = scrubbingValue;
        setIsUserScrubbing(false);
        // Small delay to ensure state is updated before seeking
        setTimeout(() => {
          handleSeek(value);
        }, 0);
      } else {
        setIsUserScrubbing(false);
      }
    };

    window.addEventListener('mouseup', handlePointerUp);
    window.addEventListener('touchend', handlePointerUp);

    return () => {
      window.removeEventListener('mouseup', handlePointerUp);
      window.removeEventListener('touchend', handlePointerUp);
    };
  }, [controls, isUserScrubbing, scrubbingValue, handleSeek]);

  const effectiveDuration = getEffectiveDuration();
  const sliderMax = effectiveDuration ?? duration ?? 0;
  const displayDuration = effectiveDuration ?? duration ?? 0;
  
  if (isVideo) {
    return (
      <div
        className="relative group"
        style={{ display: 'inline-block', width: '100%', height: '100%', pointerEvents: 'auto' }}
        onMouseEnter={() => {
          // Clear any existing hide timeout
          if (hideControlsTimeoutRef.current) {
            clearTimeout(hideControlsTimeoutRef.current);
            hideControlsTimeoutRef.current = null;
          }
          setIsPointerOver(true);
          setShowControls(true);
        }}
        onMouseLeave={() => {
          // Clear any existing hide timeout first
          if (hideControlsTimeoutRef.current) {
            clearTimeout(hideControlsTimeoutRef.current);
            hideControlsTimeoutRef.current = null;
          }
          
          setIsPointerOver(false);
          
          // Hide controls immediately when mouse leaves (regardless of play state)
          if (!isUserScrubbing) {
            setShowControls(false);
          }
        }}
        onTouchStart={() => {
          setIsPointerOver(true);
          setShowControls(true);
        }}
        onTouchEnd={() => {
          setIsPointerOver(false);
          // Clear any existing hide timeout
          if (hideControlsTimeoutRef.current) {
            clearTimeout(hideControlsTimeoutRef.current);
            hideControlsTimeoutRef.current = null;
          }
          // Hide controls immediately when touch ends (if not scrubbing)
          if (!isUserScrubbing) {
            setShowControls(false);
          }
        }}
      >
        <video
          ref={videoRef}
          src={src}
          className={`${className} ${isShortVideo ? 'short-video' : ''}`}
          style={{
            ...style,
            pointerEvents: 'auto',
            position: 'relative',
            zIndex: 0,
            outline: 'none',
            WebkitTapHighlightColor: 'transparent',
            cursor: 'pointer',
          }}
          autoPlay={autoPlay}
          muted={isMutedState}
          loop={loop}
          playsInline
          preload="auto"
          onClick={togglePlayPause}
          onError={onError}
        >
          Your browser does not support the video tag.
        </video>

        {controls && (
          <div className="pointer-events-none absolute inset-0 flex flex-col justify-end">
            <div
              className={`pointer-events-auto ${
                showControls 
                  ? 'opacity-100 transition-opacity duration-200 ease-in-out' 
                  : 'opacity-0 transition-none pointer-events-none'
              }`}
              style={{
                transition: showControls ? 'opacity 200ms ease-in-out' : 'opacity 0ms'
              }}
            >
              <div className="px-4 pb-4">
                <div className="flex items-center gap-3 rounded-2xl bg-black/70 backdrop-blur px-4 py-3 shadow-lg">
                  <button
                    type="button"
                    onClick={(event) => {
                      event.stopPropagation();
                      togglePlayPause();
                    }}
                    className="text-white text-lg leading-none focus:outline-none"
                    title={isPlaying ? 'Pause' : 'Play'}
                  >
                    {isPlaying ? '❚❚' : '▶'}
                  </button>

                  <div className="flex items-center gap-2 flex-1">
                    <span className="text-xs text-white font-mono min-w-[3rem] text-right">
                      {formatTime(currentTime)}
                    </span>
                    <input
                      type="range"
                      min={0}
                      max={sliderMax}
                      step={seekStep}
                      value={scrubbingValue !== null ? scrubbingValue : currentTime}
                      onChange={(event) => {
                        setShowControls(true);
                        const newValue = parseFloat(event.target.value);
                        if (Number.isNaN(newValue)) {
                          return;
                        }

                        const clampedValue = clampToDuration(newValue);
                        setScrubbingValue(clampedValue);

                        const video = videoRef.current;
                        if (video && canSeekImmediately()) {
                          video.currentTime = clampedValue;
                        } else {
                          pendingSeekRef.current = clampedValue;
                        }

                        // Update label immediately (especially when paused)
                        setCurrentTime(clampedValue);
                      }}
                      onMouseDown={() => {
                        setIsUserScrubbing(true);
                        setShowControls(true);
                      }}
                      onMouseUp={(event) => {
                        setIsUserScrubbing(false);
                        const value = parseFloat(event.currentTarget.value);
                        handleSeek(value);
                      }}
                      onTouchStart={() => {
                        setIsUserScrubbing(true);
                        setShowControls(true);
                      }}
                      onTouchEnd={(event) => {
                        const value = parseFloat(event.currentTarget.value);
                        setIsUserScrubbing(false);
                        // Small delay to ensure state is updated before seeking
                        setTimeout(() => {
                          handleSeek(value);
                        }, 0);
                      }}
                      disabled={!sliderMax}
                      className="flex-1 accent-primary-500 h-1 bg-white/30 rounded-full appearance-none cursor-pointer"
                    />
                    <span className="text-xs text-white font-mono min-w-[3rem]">
                      {formatTime(displayDuration)}
                    </span>
                  </div>

                  <button
                    type="button"
                    onClick={(event) => {
                      event.stopPropagation();
                      toggleMute();
                    }}
                    className="text-white text-lg leading-none focus:outline-none"
                    title={isMutedState ? 'Unmute' : 'Mute'}
                  >
                    {isMutedState ? '🔇' : '🔊'}
                  </button>

                  <button
                    type="button"
                    onClick={(event) => {
                      event.stopPropagation();
                      handleToggleFullscreen();
                    }}
                    className="text-white text-lg leading-none focus:outline-none"
                    title="Toggle fullscreen"
                  >
                    ⛶
                  </button>
                </div>
              </div>
            </div>
          </div>
        )}
      </div>
    );
  }
  
  // For images (including animated GIFs, WebP, etc.)
  return (
    <img
      src={src}
      alt={alt}
      className={className}
      style={style}
      loading={loading}
      onLoad={onLoad}
      onError={onError}
    />
  );
};

export default MediaDisplay;
