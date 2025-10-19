import { useState, useEffect } from 'react';
import { Server, RefreshCw, CheckCircle, XCircle, Clock, Database, AlertCircle, Search, Zap } from 'lucide-react';
import Button from '../ui/Button';
import SettingsSection from './SettingsSection';
import SettingItem from './SettingItem';
import LoadingSpinner from '../ui/LoadingSpinner';
import { api } from '../../services/api';
import { adminApi, RebuildMode, RebuildStatistics, VerifyResult } from '../../services/adminApi';
import toast from 'react-hot-toast';

interface RedisIndexStats {
  totalCollections: number;
  lastRebuildTime: string | null;
  isValid: boolean;
  redisConnected: boolean;
}

/**
 * Redis Index Management Component
 * 
 * Manage Redis collection index for fast search and sorting
 */
const RedisIndexManagement: React.FC = () => {
  const [stats, setStats] = useState<RedisIndexStats | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [isRebuilding, setIsRebuilding] = useState(false);
  const [isValidating, setIsValidating] = useState(false);
  const [isVerifying, setIsVerifying] = useState(false);
  
  // Rebuild options
  const [rebuildMode, setRebuildMode] = useState<RebuildMode>(RebuildMode.ChangedOnly);
  const [skipThumbnails, setSkipThumbnails] = useState(false);
  const [dryRun, setDryRun] = useState(false);
  
  // Results
  const [lastRebuildStats, setLastRebuildStats] = useState<RebuildStatistics | null>(null);
  const [verifyResult, setVerifyResult] = useState<VerifyResult | null>(null);

  // Fetch index stats
  const fetchStats = async () => {
    setIsLoading(true);
    try {
      const response = await api.get<RedisIndexStats>('/collections/index/stats');
      setStats(response.data);
    } catch (error: any) {
      console.error('Failed to fetch index stats:', error);
      toast.error(error.response?.data?.message || 'Failed to fetch index statistics');
    } finally {
      setIsLoading(false);
    }
  };

  // Check if index is valid
  const validateIndex = async () => {
    setIsValidating(true);
    try {
      const response = await api.get<{ isValid: boolean }>('/collections/index/validate');
      if (response.data.isValid) {
        toast.success('✅ Redis index is valid and up-to-date');
      } else {
        toast.error('⚠️ Redis index is invalid or outdated');
      }
      // Refresh stats
      await fetchStats();
    } catch (error: any) {
      console.error('Failed to validate index:', error);
      toast.error(error.response?.data?.message || 'Failed to validate index');
    } finally {
      setIsValidating(false);
    }
  };

  // Rebuild index with smart modes
  const rebuildIndex = async () => {
    const modeNames = {
      [RebuildMode.ChangedOnly]: 'ChangedOnly',
      [RebuildMode.Verify]: 'Verify',
      [RebuildMode.Full]: 'Full Rebuild (Clear All)',
      [RebuildMode.ForceRebuildAll]: 'Force Rebuild All'
    };
    
    if (!confirm(`Are you sure you want to rebuild the Redis index?\n\nMode: ${modeNames[rebuildMode]}\nSkip Thumbnails: ${skipThumbnails ? 'Yes' : 'No'}\nDry Run: ${dryRun ? 'Yes' : 'No'}`)) {
      return;
    }

    setIsRebuilding(true);
    try {
      const stats = await adminApi.rebuildIndex(rebuildMode, skipThumbnails, dryRun);
      setLastRebuildStats(stats);
      
      const duration = parseFloat(stats.duration.split(':')[2] || '0');
      
      if (dryRun) {
        toast.success(`🔍 Dry run complete: ${stats.rebuiltCollections} would be rebuilt, ${stats.skippedCollections} would be skipped`, {
          duration: 5000
        });
      } else {
        toast.success(`✅ Index rebuilt successfully! ${stats.rebuiltCollections} rebuilt, ${stats.skippedCollections} skipped in ${duration.toFixed(1)}s`, {
          duration: 5000
        });
      }
      
      await fetchStats();
    } catch (error: any) {
      console.error('Failed to rebuild index:', error);
      toast.error(error.response?.data?.message || 'Failed to rebuild index');
    } finally {
      setIsRebuilding(false);
    }
  };
  
  // Verify index consistency
  const verifyIndex = async () => {
    setIsVerifying(true);
    try {
      const result = await adminApi.verifyIndex(dryRun);
      setVerifyResult(result);
      
      const duration = parseFloat(result.duration.split(':')[2] || '0');
      
      if (result.isConsistent) {
        toast.success(`✅ Index is consistent! Checked in ${duration.toFixed(1)}s`, {
          duration: 3000
        });
      } else {
        if (dryRun) {
          toast.warning(`⚠️ Found ${result.toAdd + result.toUpdate + result.toRemove} inconsistencies (Add=${result.toAdd}, Update=${result.toUpdate}, Remove=${result.toRemove})`, {
            duration: 5000
          });
        } else {
          toast.success(`✅ Fixed ${result.toAdd + result.toUpdate + result.toRemove} inconsistencies! (Added=${result.toAdd}, Updated=${result.toUpdate}, Removed=${result.toRemove})`, {
            duration: 5000
          });
        }
      }
      
      await fetchStats();
    } catch (error: any) {
      console.error('Failed to verify index:', error);
      toast.error(error.response?.data?.message || 'Failed to verify index');
    } finally {
      setIsVerifying(false);
    }
  };

  // Load stats on mount
  useEffect(() => {
    fetchStats();
  }, []);

  const formatDate = (dateString: string | null): string => {
    if (!dateString) return 'Never';
    try {
      return new Date(dateString).toLocaleString();
    } catch {
      return 'Invalid date';
    }
  };
  
  const formatDuration = (durationString: string): string => {
    try {
      // Parse TimeSpan format (HH:MM:SS or HH:MM:SS.fffffff)
      const parts = durationString.split(':');
      const hours = parseInt(parts[0]);
      const minutes = parseInt(parts[1]);
      const seconds = parseFloat(parts[2] || '0');
      
      if (hours > 0) return `${hours}h ${minutes}m`;
      if (minutes > 0) return `${minutes}m ${seconds.toFixed(0)}s`;
      return `${seconds.toFixed(1)}s`;
    } catch {
      return durationString;
    }
  };
  
  const getModeDescription = (mode: RebuildMode): string => {
    switch (mode) {
      case RebuildMode.ChangedOnly:
        return 'Only rebuilds collections that have been updated since last index. Fast and safe. (Recommended for regular use)';
      case RebuildMode.Verify:
        return 'Checks consistency between MongoDB and Redis. Adds missing, updates outdated, removes deleted collections. Recommended after manual DB changes.';
      case RebuildMode.Full:
        return 'Clears ALL Redis data and rebuilds from scratch. Use only if index is corrupted or for first-time setup. (~30 minutes)';
      case RebuildMode.ForceRebuildAll:
        return 'Rebuilds all collections without clearing Redis structure. Use after index schema changes. (~30 minutes)';
      default:
        return '';
    }
  };

  return (
    <SettingsSection
      title="Redis Index Management"
      description="Manage the Redis collection index for fast search and sorting"
    >
      {/* Statistics Cards */}
      {isLoading && !stats ? (
        <div className="py-8">
          <LoadingSpinner text="Loading index statistics..." />
        </div>
      ) : stats ? (
        <div className="space-y-4">
          {/* Stats Grid */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            {/* Total Collections */}
            <div className="bg-slate-700/30 rounded-lg p-4 border border-slate-600/50">
              <div className="flex items-center justify-between mb-2">
                <span className="text-sm text-slate-400">Total Collections</span>
                <Database className="h-5 w-5 text-blue-400" />
              </div>
              <div className="text-2xl font-bold text-white">
                {stats.totalCollections.toLocaleString()}
              </div>
            </div>

            {/* Index Status */}
            <div className="bg-slate-700/30 rounded-lg p-4 border border-slate-600/50">
              <div className="flex items-center justify-between mb-2">
                <span className="text-sm text-slate-400">Index Status</span>
                {stats.isValid ? (
                  <CheckCircle className="h-5 w-5 text-green-400" />
                ) : (
                  <XCircle className="h-5 w-5 text-red-400" />
                )}
              </div>
              <div className={`text-xl font-semibold ${
                stats.isValid ? 'text-green-400' : 'text-red-400'
              }`}>
                {stats.isValid ? 'Valid' : 'Invalid'}
              </div>
            </div>

            {/* Last Rebuild */}
            <div className="bg-slate-700/30 rounded-lg p-4 border border-slate-600/50">
              <div className="flex items-center justify-between mb-2">
                <span className="text-sm text-slate-400">Last Rebuild</span>
                <Clock className="h-5 w-5 text-purple-400" />
              </div>
              <div className="text-sm font-medium text-white">
                {formatDate(stats.lastRebuildTime)}
              </div>
            </div>

            {/* Redis Connection */}
            <div className="bg-slate-700/30 rounded-lg p-4 border border-slate-600/50">
              <div className="flex items-center justify-between mb-2">
                <span className="text-sm text-slate-400">Redis Connection</span>
                <Server className={`h-5 w-5 ${
                  stats.redisConnected ? 'text-green-400' : 'text-red-400'
                }`} />
              </div>
              <div className={`text-xl font-semibold ${
                stats.redisConnected ? 'text-green-400' : 'text-red-400'
              }`}>
                {stats.redisConnected ? 'Connected' : 'Disconnected'}
              </div>
            </div>
          </div>

          {/* Rebuild Options */}
          <SettingItem
            label="Rebuild Index"
            description="Smart rebuild with multiple modes and options"
            vertical
          >
            <div className="space-y-4">
              {/* Mode Selection */}
              <div>
                <label className="block text-sm font-medium mb-2 text-slate-300">Rebuild Mode</label>
                <select 
                  value={rebuildMode} 
                  onChange={(e) => setRebuildMode(e.target.value as RebuildMode)}
                  className="w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-white focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  <option value={RebuildMode.ChangedOnly}>Changed Only (Recommended) ⭐</option>
                  <option value={RebuildMode.Verify}>Verify & Fix Consistency 🔍</option>
                  <option value={RebuildMode.Full}>Full Rebuild (Clear All) 🔄</option>
                  <option value={RebuildMode.ForceRebuildAll}>Force Rebuild All (No Clear) 🔨</option>
                </select>
                
                {/* Mode Description */}
                <p className="text-xs text-slate-400 mt-2">
                  {getModeDescription(rebuildMode)}
                </p>
              </div>
              
              {/* Options */}
              <div className="space-y-2">
                <label className="flex items-center text-sm text-slate-300">
                  <input 
                    type="checkbox" 
                    checked={skipThumbnails} 
                    onChange={(e) => setSkipThumbnails(e.target.checked)}
                    className="mr-2 rounded"
                  />
                  Skip thumbnail caching (faster, but collection cards won't show thumbnails)
                </label>
                
                <label className="flex items-center text-sm text-slate-300">
                  <input 
                    type="checkbox" 
                    checked={dryRun} 
                    onChange={(e) => setDryRun(e.target.checked)}
                    className="mr-2 rounded"
                  />
                  Dry run (preview changes without applying)
                </label>
              </div>
              
              {/* Action Buttons */}
              <div className="flex flex-wrap gap-3">
                <Button
                  variant="primary"
                  onClick={rebuildIndex}
                  disabled={isRebuilding || isLoading || isVerifying}
                  className="flex items-center space-x-2"
                >
                  <Zap className={`h-4 w-4 ${isRebuilding ? 'animate-spin' : ''}`} />
                  <span>{isRebuilding ? 'Processing...' : 'Start Rebuild'}</span>
                </Button>
                
                {rebuildMode === RebuildMode.Verify && (
                  <Button
                    variant="secondary"
                    onClick={verifyIndex}
                    disabled={isVerifying || isLoading || isRebuilding}
                    className="flex items-center space-x-2"
                  >
                    <Search className={`h-4 w-4 ${isVerifying ? 'animate-spin' : ''}`} />
                    <span>{isVerifying ? 'Verifying...' : 'Verify Only'}</span>
                  </Button>
                )}
                
                <Button
                  variant="ghost"
                  onClick={validateIndex}
                  disabled={isValidating || isLoading}
                  className="flex items-center space-x-2"
                >
                  <CheckCircle className="h-4 w-4" />
                  <span>{isValidating ? 'Validating...' : 'Check Validity'}</span>
                </Button>
                
                <Button
                  variant="ghost"
                  onClick={fetchStats}
                  disabled={isLoading}
                  className="flex items-center space-x-2"
                >
                  <RefreshCw className={`h-4 w-4 ${isLoading ? 'animate-spin' : ''}`} />
                  <span>Refresh</span>
                </Button>
              </div>
            </div>
          </SettingItem>
          
          {/* Last Rebuild Statistics */}
          {lastRebuildStats && (
            <div className="bg-slate-800 p-4 rounded-lg border border-slate-700">
              <h3 className="font-semibold mb-3 text-white">Last Rebuild Results</h3>
              <div className="grid grid-cols-2 md:grid-cols-3 gap-4 text-sm">
                <div>
                  <span className="text-slate-400">Mode:</span>
                  <span className="ml-2 font-mono text-white">{lastRebuildStats.mode}</span>
                </div>
                <div>
                  <span className="text-slate-400">Duration:</span>
                  <span className="ml-2 font-mono text-white">{formatDuration(lastRebuildStats.duration)}</span>
                </div>
                <div>
                  <span className="text-slate-400">Total:</span>
                  <span className="ml-2 font-mono text-white">{lastRebuildStats.totalCollections}</span>
                </div>
                <div>
                  <span className="text-slate-400">Rebuilt:</span>
                  <span className="ml-2 font-mono text-blue-400">{lastRebuildStats.rebuiltCollections}</span>
                </div>
                <div>
                  <span className="text-slate-400">Skipped:</span>
                  <span className="ml-2 font-mono text-green-400">{lastRebuildStats.skippedCollections}</span>
                </div>
                <div>
                  <span className="text-slate-400">Memory Peak:</span>
                  <span className="ml-2 font-mono text-white">{lastRebuildStats.memoryPeakMB} MB</span>
                </div>
              </div>
            </div>
          )}
          
          {/* Verify Results */}
          {verifyResult && (
            <div className={`p-4 rounded-lg border ${
              verifyResult.isConsistent 
                ? 'bg-green-900/20 border-green-700' 
                : 'bg-yellow-900/20 border-yellow-700'
            }`}>
              <h3 className="font-semibold mb-3 text-white">
                {verifyResult.isConsistent ? '✅ Index Consistent' : '⚠️ Inconsistencies Found'}
              </h3>
              <div className="space-y-2 text-sm">
                <div className="grid grid-cols-2 gap-4">
                  <div>
                    <span className="text-slate-400">MongoDB Collections:</span>
                    <span className="ml-2 font-mono text-white">{verifyResult.totalInMongoDB}</span>
                  </div>
                  <div>
                    <span className="text-slate-400">Redis Indexed:</span>
                    <span className="ml-2 font-mono text-white">{verifyResult.totalInRedis}</span>
                  </div>
                </div>
                
                {!verifyResult.isConsistent && (
                  <div className="mt-3 space-y-1">
                    {verifyResult.toAdd > 0 && (
                      <div className="text-blue-300">➕ Missing in Redis: <strong>{verifyResult.toAdd}</strong></div>
                    )}
                    {verifyResult.toUpdate > 0 && (
                      <div className="text-yellow-300">🔄 Outdated in Redis: <strong>{verifyResult.toUpdate}</strong></div>
                    )}
                    {verifyResult.toRemove > 0 && (
                      <div className="text-red-300">🗑️ Orphaned in Redis: <strong>{verifyResult.toRemove}</strong></div>
                    )}
                  </div>
                )}
                
                {verifyResult.isConsistent && (
                  <div className="text-green-400 mt-2">All collections properly indexed ✨</div>
                )}
                
                <div className="text-slate-400 text-xs mt-3">
                  Checked in {formatDuration(verifyResult.duration)}
                  {verifyResult.dryRun && ' (dry run - no changes made)'}
                </div>
              </div>
            </div>
          )}

          {/* Info Box */}
          <div className="bg-blue-900/20 border border-blue-500/30 rounded-lg p-4">
            <div className="flex items-start space-x-3">
              <AlertCircle className="h-5 w-5 text-blue-400 flex-shrink-0 mt-0.5" />
              <div className="flex-1">
                <p className="text-blue-300 text-sm font-medium mb-2">
                  Rebuild Modes:
                </p>
                <ul className="text-blue-300/80 text-sm space-y-1 list-disc list-inside">
                  <li><strong>Changed Only</strong>: Daily use, only rebuilds updated collections (~2-5 min)</li>
                  <li><strong>Verify</strong>: After manual DB changes, fixes inconsistencies (~10 min)</li>
                  <li><strong>Full</strong>: First time or corruption, clears all (~30 min)</li>
                  <li><strong>Force Rebuild All</strong>: Schema changes, rebuilds all (~30 min)</li>
                </ul>
                <p className="text-blue-300 text-sm font-medium mt-3 mb-1">
                  Options:
                </p>
                <ul className="text-blue-300/80 text-sm space-y-1 list-disc list-inside">
                  <li><strong>Skip Thumbnails</strong>: ~40% faster, but collection cards won't show thumbnails</li>
                  <li><strong>Dry Run</strong>: Preview what would be rebuilt without making changes</li>
                </ul>
              </div>
            </div>
          </div>
        </div>
      ) : (
        <div className="text-center py-8 text-slate-400">
          No statistics available
        </div>
      )}
    </SettingsSection>
  );
};

export default RedisIndexManagement;

