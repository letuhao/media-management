import api from './api';

/**
 * Rebuild mode options
 */
export enum RebuildMode {
  ChangedOnly = 'ChangedOnly',
  Verify = 'Verify',
  Full = 'Full',
  ForceRebuildAll = 'ForceRebuildAll'
}

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
      mode,
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
  }
};

