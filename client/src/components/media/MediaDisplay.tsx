import React from 'react';
import MuxPlayer from '@mux/mux-player-react';
import '@mux/mux-player/themes/microvideo';
import { isVideoFile } from '../../utils/mediaUtils';

interface MediaDisplayProps {
  src: string;
  alt: string;
  filename?: string;
  className?: string;
  style?: React.CSSProperties;
  loading?: 'lazy' | 'eager';
  onLoad?: () => void;
  onError?: () => void;
  autoPlay?: boolean;
  muted?: boolean;
  loop?: boolean;
}

/**
 * Smart media display component that renders images or videos appropriately
 * based on the file type. Videos leverage Mux Player for a polished, Shorts-like
 * playback experience with high-quality controls.
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
  autoPlay = true,
  muted = true,
  loop = true,
}) => {
  const filename = providedFilename || src.split('/').pop() || '';
  const isVideo = isVideoFile(filename);

  if (isVideo) {
    return (
      <MuxPlayer
        className={className}
        style={style}
        streamType="on-demand"
        src={src}
        metadata={{
          video_id: filename,
          video_title: filename || alt,
        }}
        theme="microvideo"
        autoPlay={autoPlay}
        muted={muted}
        loop={loop}
        playsInline
        preload="auto"
        thumbnailTime={0}
        onLoadedData={() => onLoad?.()}
        onError={() => onError?.()}
      />
    );
  }

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
