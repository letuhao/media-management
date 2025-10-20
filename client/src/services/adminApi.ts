import api from './api';

/**
 * Rebuild mode options (must match backend enum values)
 */
export const RebuildMode = {
  ChangedOnly: 0,
  Verify: 1,
  Full: 2,
  ForceRebuildAll: 3
} as const;

export type RebuildMode = typeof RebuildMode[keyof typeof RebuildMode];

/**
 * Rebuild statistics response
 */
export interface RebuildStatistics {
  mode: RebuildMode;
  totalCollections: number;
  skippedCollections: number;
  rebuiltCollections: number;
  duration: string; // TimeSpan as string
  memoryPeakMB: number;
  startedAt: string;
  completedAt: string;
}

/**
 * Verify result response
 */
export interface VerifyResult {
  isConsistent: boolean;
  totalInMongoDB: number;
  totalInRedis: number;
  toAdd: number;
  toUpdate: number;
  toRemove: number;
  missingInRedis: string[];
  outdatedInRedis: string[];
  missingThumbnails: string[];
  orphanedInRedis: string[];
  duration: string;
  dryRun: boolean;
}

/**
 * Collection index state
 */
export interface CollectionIndexState {
  collectionId: string;
  indexedAt: string;
  collectionUpdatedAt: string;
  imageCount: number;
  thumbnailCount: number;
  cacheCount: number;
  hasFirstThumbnail: boolean;
  firstThumbnailPath?: string;
  indexVersion: string;
}

/**
 * Admin API for system management
 */
export const adminApi = {
  /**
   * Rebuild Redis collection index
   */
  rebuildIndex: async (
    mode: RebuildMode = RebuildMode.ChangedOnly,
    skipThumbnailCaching: boolean = false,
    dryRun: boolean = false
  ): Promise<RebuildStatistics> => {
    const response = await api.post('/admin/index/rebuild', {
      mode: Number(mode), // Ensure mode is sent as number, not string
      skipThumbnailCaching,
      dryRun
    });
    return response.data;
  },

  /**
   * Verify index consistency
   */
  verifyIndex: async (dryRun: boolean = true): Promise<VerifyResult> => {
    const response = await api.post('/admin/index/verify', {
      dryRun
    });
    return response.data;
  },

  /**
   * Get collection index state
   */
  getCollectionIndexState: async (collectionId: string): Promise<CollectionIndexState> => {
    const response = await api.get(`/admin/index/state/${collectionId}`);
    return response.data;
  },

  /**
   * Fix archive entry paths for collections with incorrect folder structure
   */
  fixArchiveEntries: async (
    dryRun: boolean = true, 
    limit?: number, 
    collectionId?: string,
    fixMode?: string, // "All", "DimensionsOnly", "PathsOnly"
    onlyCorrupted?: boolean
  ): Promise<ArchiveEntryFixResult> => {
    const response = await api.post('/admin/fix-archive-entries', {
      dryRun,
      limit,
      collectionId,
      fixMode,
      onlyCorrupted
    });
    return response.data;
  }
};

/**
 * Archive entry fix result
 */
export interface ArchiveEntryFixResult {
  totalCollectionsScanned: number;
  collectionsWithIssues: number;
  imagesFixed: number;
  fixedCollectionIds: string[];
  errorMessages: string[];
  duration: string;
  dryRun: boolean;
  startedAt: string;
  completedAt: string;
}

