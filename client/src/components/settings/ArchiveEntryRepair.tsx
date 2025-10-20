import { useState } from 'react';
import { Wrench, CheckCircle, XCircle, AlertTriangle } from 'lucide-react';
import Button from '../ui/Button';
import SettingsSection from './SettingsSection';
import LoadingSpinner from '../ui/LoadingSpinner';
import { adminApi, ArchiveEntryFixResult } from '../../services/adminApi';
import toast from 'react-hot-toast';

/**
 * Archive Entry Repair Component
 * 
 * Fix corrupted archive entry paths in collections where folder structure is missing.
 * This repairs the bug where entryName/entryPath don't include folders inside archives.
 */
const ArchiveEntryRepair: React.FC = () => {
  const [isScanning, setIsScanning] = useState(false);
  const [isFixing, setIsFixing] = useState(false);
  const [scanResult, setScanResult] = useState<ArchiveEntryFixResult | null>(null);
  const [limit, setLimit] = useState<number>(1000);
  const [fixMode, setFixMode] = useState<string>('DimensionsOnly'); // "All", "DimensionsOnly", "PathsOnly"
  const [onlyCorrupted, setOnlyCorrupted] = useState<boolean>(true); // Filter for collections with dimension issues
  const [collectionId, setCollectionId] = useState<string>(''); // Specific collection ID for debugging

  // Scan for corrupted data (dry run)
  const handleScan = async () => {
    if (!confirm('This will scan archive collections to detect corrupted entry paths. No data will be modified. Continue?')) {
      return;
    }

    setIsScanning(true);
    setScanResult(null);

    try {
      const actualCollectionId = collectionId.trim() || undefined;
      const result = await adminApi.fixArchiveEntries(true, limit, actualCollectionId, fixMode, onlyCorrupted);
      setScanResult(result);

      if (result.collectionsWithIssues > 0) {
        toast.success(
          `Found ${result.collectionsWithIssues} collections with issues (${result.imagesFixed} images need fixing)`,
          { duration: 5000 }
        );
      } else {
        toast.success('No corrupted archive entries found! All data is correct.', { duration: 3000 });
      }
    } catch (error: any) {
      console.error('Scan failed:', error);
      toast.error(error.response?.data?.message || 'Failed to scan for corrupted entries');
    } finally {
      setIsScanning(false);
    }
  };

  // Fix corrupted data (after scan)
  const handleFix = async () => {
    if (!confirm(
      `This will fix ${scanResult?.imagesFixed || 'all'} corrupted archive entries.\n\n` +
      `Collections: ${scanResult?.collectionsWithIssues || '?'}\n` +
      `Images: ${scanResult?.imagesFixed || '?'} entries\n\n` +
      `Continue?`
    )) {
      return;
    }

    setIsFixing(true);

    try {
      const actualCollectionId = collectionId.trim() || undefined;
      const result = await adminApi.fixArchiveEntries(false, limit, actualCollectionId, fixMode, onlyCorrupted);

      toast.success(
        `✅ Fixed ${result.imagesFixed} images in ${result.collectionsWithIssues} collections! Duration: ${result.duration}`,
        { duration: 8000 }
      );

      setScanResult(result);
    } catch (error: any) {
      console.error('Fix failed:', error);
      toast.error(error.response?.data?.message || 'Failed to fix corrupted entries');
    } finally {
      setIsFixing(false);
    }
  };

  // Fix directly without scanning first (one pass)
  const handleFixDirectly = async () => {
    if (!confirm(
      `⚠️ WARNING: This will scan and fix ALL corrupted entries in one pass.\n\n` +
      `Limit: ${limit} collections\n` +
      `Duration: ~15-30 minutes for 25k collections\n\n` +
      `This will UPDATE MongoDB directly.\n\n` +
      `Continue?`
    )) {
      return;
    }

    setIsFixing(true);
    setScanResult(null);

    try {
      const actualCollectionId = collectionId.trim() || undefined;
      const result = await adminApi.fixArchiveEntries(false, limit, actualCollectionId, fixMode, onlyCorrupted);

      toast.success(
        `✅ Fixed ${result.imagesFixed} images in ${result.collectionsWithIssues} collections! Duration: ${result.duration}`,
        { duration: 10000 }
      );

      setScanResult(result);
    } catch (error: any) {
      console.error('Fix failed:', error);
      toast.error(error.response?.data?.message || 'Failed to fix corrupted entries');
    } finally {
      setIsFixing(false);
    }
  };

  return (
    <SettingsSection
      title="Archive Entry Repair"
      description="Fix corrupted archive entry paths where folder structure is missing"
      icon={Wrench}
    >
      <div className="space-y-4">
        {/* Options */}
        <div className="space-y-4">
          {/* Collection Limit */}
          <div className="bg-slate-800/50 rounded-lg p-4 border border-slate-700">
            <label className="block text-sm font-medium text-slate-300 mb-2">
              Collection Limit
            </label>
            <input
              type="number"
              value={limit}
              onChange={(e) => setLimit(parseInt(e.target.value) || 1000)}
              className="w-full px-3 py-2 bg-slate-700 border border-slate-600 rounded text-white"
              min="1"
              max="100000"
            />
            <p className="text-xs text-slate-500 mt-1">
              Set to 100000 to process all collections. Current: {limit} collections
            </p>
          </div>

          {/* Fix Mode */}
          <div className="bg-slate-800/50 rounded-lg p-4 border border-slate-700">
            <label className="block text-sm font-medium text-slate-300 mb-2">
              Fix Mode
            </label>
            <select
              value={fixMode}
              onChange={(e) => setFixMode(e.target.value)}
              className="w-full px-3 py-2 bg-slate-700 border border-slate-600 rounded text-white"
            >
              <option value="All">All - Fix paths and dimensions</option>
              <option value="DimensionsOnly">Dimensions Only - Extract missing dimensions</option>
              <option value="PathsOnly">Paths Only - Fix archive entry paths</option>
            </select>
            <p className="text-xs text-slate-500 mt-1">
              {fixMode === 'DimensionsOnly' && '📊 Only extract image dimensions (faster, recommended for dimension issues)'}
              {fixMode === 'PathsOnly' && '📂 Only fix archive entry paths (folder structure)'}
              {fixMode === 'All' && '🔧 Fix both paths and dimensions (slower but comprehensive)'}
            </p>
          </div>

          {/* Only Corrupted Filter */}
          <div className="bg-slate-800/50 rounded-lg p-4 border border-slate-700">
            <label className="flex items-center space-x-3 cursor-pointer">
              <input
                type="checkbox"
                checked={onlyCorrupted}
                onChange={(e) => setOnlyCorrupted(e.target.checked)}
                className="w-5 h-5 bg-slate-700 border-slate-600 rounded text-blue-600 focus:ring-2 focus:ring-blue-500"
              />
              <span className="text-sm font-medium text-slate-300">
                Only Process Collections with Dimension Issues
              </span>
            </label>
            <p className="text-xs text-slate-500 mt-2 ml-8">
              {onlyCorrupted 
                ? '🎯 Filter for collections with width=0 or height=0 (excludes __MACOSX). Much faster!'
                : '⚠️ Process all collections (slower, scans 26k+ collections)'}
            </p>
          </div>

          {/* Debug: Specific Collection ID */}
          <div className="bg-slate-800/50 rounded-lg p-4 border border-amber-700">
            <label className="block text-sm font-medium text-amber-300 mb-2">
              🔍 Debug: Specific Collection ID (Optional)
            </label>
            <input
              type="text"
              value={collectionId}
              onChange={(e) => setCollectionId(e.target.value)}
              placeholder="e.g., 68f2a388ff19d7b375b40da9"
              className="w-full px-3 py-2 bg-slate-700 border border-slate-600 rounded text-white font-mono text-sm"
            />
            <p className="text-xs text-amber-500 mt-2">
              {collectionId 
                ? '🎯 Will ONLY process this specific collection (ignores limit and filters)'
                : '💡 Leave empty to process multiple collections'}
            </p>
          </div>
        </div>

        {/* Action Buttons */}
        <div className="grid grid-cols-2 gap-4">
          {/* Option 1: Scan Only (Preview) */}
          <div className="bg-blue-900/20 rounded-lg p-4 border border-blue-700">
            <h4 className="text-sm font-semibold text-blue-400 mb-2">📋 Option 1: Preview</h4>
            <p className="text-xs text-blue-300 mb-3">
              Scan only, no changes. See what would be fixed.
            </p>
            <Button
              onClick={handleScan}
              disabled={isScanning || isFixing}
              className="w-full bg-blue-600 hover:bg-blue-700"
            >
              {isScanning ? <LoadingSpinner size="sm" text="Scanning..." /> : '🔍 Scan Only (Safe)'}
            </Button>
          </div>

          {/* Option 2: Fix Directly (One Pass) */}
          <div className="bg-yellow-900/20 rounded-lg p-4 border border-yellow-700">
            <h4 className="text-sm font-semibold text-yellow-400 mb-2">⚡ Option 2: Fix Now</h4>
            <p className="text-xs text-yellow-300 mb-3">
              Scan and fix in one pass. Updates MongoDB.
            </p>
            <Button
              onClick={handleFixDirectly}
              disabled={isScanning || isFixing}
              className="w-full bg-yellow-600 hover:bg-yellow-700"
            >
              {isFixing ? <LoadingSpinner size="sm" text="Fixing..." /> : '🔧 Scan & Fix Now'}
            </Button>
          </div>
        </div>

        {/* Scan Results */}
        {scanResult && (
          <div className={`bg-slate-800/50 rounded-lg p-4 border ${
            scanResult.collectionsWithIssues > 0 ? 'border-yellow-600' : 'border-green-600'
          }`}>
            <div className="flex items-center gap-2 mb-3">
              {scanResult.collectionsWithIssues > 0 ? (
                <AlertTriangle className="h-5 w-5 text-yellow-500" />
              ) : (
                <CheckCircle className="h-5 w-5 text-green-500" />
              )}
              <h4 className="text-lg font-semibold text-white">
                {scanResult.dryRun ? 'Scan Results' : 'Fix Results'}
              </h4>
            </div>

            <div className="grid grid-cols-2 gap-4 text-sm">
              <div>
                <p className="text-slate-400">Collections Scanned:</p>
                <p className="text-white font-semibold">{scanResult.totalCollectionsScanned}</p>
              </div>
              <div>
                <p className="text-slate-400">With Issues:</p>
                <p className="text-yellow-400 font-semibold">{scanResult.collectionsWithIssues}</p>
              </div>
              <div>
                <p className="text-slate-400">{scanResult.dryRun ? 'Images Needing Fix:' : 'Images Fixed:'}</p>
                <p className={`font-semibold ${scanResult.dryRun ? 'text-yellow-400' : 'text-green-400'}`}>
                  {scanResult.imagesFixed}
                </p>
              </div>
              <div>
                <p className="text-slate-400">Duration:</p>
                <p className="text-white font-semibold">{scanResult.duration}</p>
              </div>
            </div>

            {!scanResult.dryRun && scanResult.collectionsWithIssues > 0 && (
              <div className="mt-3 p-3 bg-green-900/20 border border-green-700 rounded">
                <p className="text-sm text-green-400 font-semibold">
                  ✅ MongoDB has been updated! Changes are permanent.
                </p>
              </div>
            )}

            {scanResult.errorMessages && scanResult.errorMessages.length > 0 && (
              <div className="mt-3 p-3 bg-red-900/20 border border-red-700 rounded">
                <p className="text-sm text-red-400 font-semibold mb-1">Errors:</p>
                <ul className="text-xs text-red-300 space-y-1">
                  {scanResult.errorMessages.slice(0, 5).map((error, idx) => (
                    <li key={idx}>• {error}</li>
                  ))}
                  {scanResult.errorMessages.length > 5 && (
                    <li className="text-red-400">... and {scanResult.errorMessages.length - 5} more</li>
                  )}
                </ul>
              </div>
            )}

            {scanResult.dryRun && scanResult.collectionsWithIssues > 0 && (
              <Button
                onClick={handleFix}
                disabled={isFixing}
                className="w-full mt-4 bg-yellow-600 hover:bg-yellow-700"
              >
                {isFixing ? <LoadingSpinner size="sm" text="Fixing..." /> : `🔧 Fix ${scanResult.imagesFixed} Corrupted Entries`}
              </Button>
            )}
          </div>
        )}

        {/* Info Box */}
        <div className="bg-blue-900/20 border border-blue-700 rounded-lg p-4">
          <h4 className="text-sm font-semibold text-blue-400 mb-2">ℹ️ How To Use</h4>
          <div className="text-xs text-blue-300 space-y-2">
            <div>
              <strong className="text-blue-200">Option 1 (Recommended):</strong> Click "Scan Only" first to preview what will be fixed. 
              No changes made. Then click the "Fix" button that appears.
            </div>
            <div>
              <strong className="text-blue-200">Option 2 (Faster):</strong> Click "Scan & Fix Now" to detect and fix in ONE pass. 
              Updates MongoDB directly. Use if you trust the tool.
            </div>
          </div>

          <h4 className="text-sm font-semibold text-blue-400 mt-3 mb-2">⚡ Performance</h4>
          <ul className="text-xs text-blue-300 space-y-1">
            <li>• ~20-30ms per collection (very fast)</li>
            <li>• 1,000 collections: ~3-5 minutes</li>
            <li>• 25,000 collections: ~15-30 minutes</li>
            <li>• Option 2 is 2x faster (one pass instead of two)</li>
          </ul>

          <h4 className="text-sm font-semibold text-blue-400 mt-3 mb-2">🔧 What Gets Fixed</h4>
          <ul className="text-xs text-blue-300 space-y-1">
            <li>• Missing folder structure in archive paths</li>
            <li>• Null ArchiveEntry (legacy data)</li>
            <li>• Inconsistent path fields</li>
            <li>• Works for both folders and archives</li>
          </ul>
        </div>
      </div>
    </SettingsSection>
  );
};

export default ArchiveEntryRepair;

