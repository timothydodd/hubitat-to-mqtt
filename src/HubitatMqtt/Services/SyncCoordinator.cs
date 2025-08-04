using System.Collections.Concurrent;

namespace HubitatMqtt.Services
{
    /// <summary>
    /// Coordinates between webhook updates and periodic sync to prevent race conditions
    /// </summary>
    public class SyncCoordinator : IDisposable
    {
        private readonly ILogger<SyncCoordinator> _logger;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceLocks;
        private readonly SemaphoreSlim _fullSyncSemaphore;
        private readonly ConcurrentDictionary<string, DateTime> _pendingWebhookUpdates;
        private volatile bool _fullSyncInProgress;
        private DateTime _lastFullSync;

        public SyncCoordinator(ILogger<SyncCoordinator> logger)
        {
            _logger = logger;
            _deviceLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
            _fullSyncSemaphore = new SemaphoreSlim(1, 1);
            _pendingWebhookUpdates = new ConcurrentDictionary<string, DateTime>();
            _fullSyncInProgress = false;
            _lastFullSync = DateTime.MinValue;
        }

        /// <summary>
        /// Acquires a lock for webhook processing. Uses per-device locking for better concurrency.
        /// </summary>
        public async Task<IDisposable> AcquireWebhookLockAsync(string deviceId, TimeSpan timeout = default)
        {
            if (timeout == default)
                timeout = TimeSpan.FromSeconds(2); // Reduced timeout since we're using per-device locks

            // If full sync is in progress, defer webhook updates
            if (_fullSyncInProgress)
            {
                _logger.LogDebug("Full sync in progress, deferring webhook update for device {DeviceId}", deviceId);
                _pendingWebhookUpdates.TryAdd(deviceId, DateTime.UtcNow);
                throw new InvalidOperationException("Full sync in progress, webhook update deferred");
            }

            // Get or create device-specific semaphore
            var deviceSemaphore = _deviceLocks.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));

            if (!await deviceSemaphore.WaitAsync(timeout))
            {
                _logger.LogWarning("Timeout waiting for webhook lock for device {DeviceId}", deviceId);
                throw new TimeoutException($"Failed to acquire webhook lock for device {deviceId} within {timeout}");
            }

            // Mark this device as having a pending webhook update
            _pendingWebhookUpdates.TryAdd(deviceId, DateTime.UtcNow);

            return new WebhookLockReleaser(this, deviceId, deviceSemaphore);
        }

        /// <summary>
        /// Acquires a lock for full sync, preventing conflicts with webhook processing
        /// </summary>
        public async Task<IDisposable> AcquireFullSyncLockAsync(TimeSpan timeout = default)
        {
            if (timeout == default)
                timeout = TimeSpan.FromSeconds(30);

            if (!await _fullSyncSemaphore.WaitAsync(timeout))
            {
                _logger.LogWarning("Timeout waiting for full sync lock");
                throw new TimeoutException($"Failed to acquire full sync lock within {timeout}");
            }

            _fullSyncInProgress = true;
            _logger.LogDebug("Full sync lock acquired");

            return new FullSyncLockReleaser(this);
        }

        /// <summary>
        /// Gets devices that had webhook updates during the last full sync
        /// </summary>
        public HashSet<string> GetDevicesWithPendingWebhookUpdates()
        {
            var cutoffTime = _lastFullSync;
            var pendingDevices = new HashSet<string>();

            foreach (var kvp in _pendingWebhookUpdates)
            {
                if (kvp.Value > cutoffTime)
                {
                    pendingDevices.Add(kvp.Key);
                }
            }

            return pendingDevices;
        }

        /// <summary>
        /// Clears pending webhook updates for devices that were successfully synced
        /// </summary>
        public void ClearPendingWebhookUpdates(HashSet<string> syncedDeviceIds)
        {
            foreach (var deviceId in syncedDeviceIds)
            {
                _pendingWebhookUpdates.TryRemove(deviceId, out _);
            }
            
            _logger.LogDebug("Cleared pending webhook updates for {Count} devices", syncedDeviceIds.Count);
        }

        private void ReleaseWebhookLock(string deviceId, SemaphoreSlim deviceSemaphore)
        {
            _logger.LogDebug("Released webhook lock for device {DeviceId}", deviceId);
            deviceSemaphore.Release();
        }

        private void ReleaseFullSyncLock()
        {
            _fullSyncInProgress = false;
            _lastFullSync = DateTime.UtcNow;
            _logger.LogDebug("Released full sync lock");
            _fullSyncSemaphore.Release();
        }

        public bool IsFullSyncInProgress => _fullSyncInProgress;
        public DateTime LastFullSync => _lastFullSync;

        /// <summary>
        /// Cleanup unused device locks to prevent memory leaks
        /// </summary>
        public void CleanupUnusedDeviceLocks(HashSet<string> activeDeviceIds)
        {
            var locksToRemove = new List<string>();
            
            foreach (var kvp in _deviceLocks)
            {
                var deviceId = kvp.Key;
                var semaphore = kvp.Value;
                
                // If device is not active and semaphore is not in use, remove it
                if (!activeDeviceIds.Contains(deviceId) && semaphore.CurrentCount == 1)
                {
                    locksToRemove.Add(deviceId);
                }
            }
            
            foreach (var deviceId in locksToRemove)
            {
                if (_deviceLocks.TryRemove(deviceId, out var removedSemaphore))
                {
                    removedSemaphore.Dispose();
                    _logger.LogDebug("Cleaned up unused device lock for {DeviceId}", deviceId);
                }
            }
            
            if (locksToRemove.Count > 0)
            {
                _logger.LogDebug("Cleaned up {Count} unused device locks", locksToRemove.Count);
            }
        }

        public void Dispose()
        {
            _fullSyncSemaphore?.Dispose();
            
            foreach (var kvp in _deviceLocks)
            {
                kvp.Value.Dispose();
            }
            
            _deviceLocks.Clear();
        }

        private class WebhookLockReleaser : IDisposable
        {
            private readonly SyncCoordinator _coordinator;
            private readonly string _deviceId;
            private readonly SemaphoreSlim _deviceSemaphore;
            private bool _disposed;

            public WebhookLockReleaser(SyncCoordinator coordinator, string deviceId, SemaphoreSlim deviceSemaphore)
            {
                _coordinator = coordinator;
                _deviceId = deviceId;
                _deviceSemaphore = deviceSemaphore;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _coordinator.ReleaseWebhookLock(_deviceId, _deviceSemaphore);
                    _disposed = true;
                }
            }
        }

        private class FullSyncLockReleaser : IDisposable
        {
            private readonly SyncCoordinator _coordinator;
            private bool _disposed;

            public FullSyncLockReleaser(SyncCoordinator coordinator)
            {
                _coordinator = coordinator;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _coordinator.ReleaseFullSyncLock();
                    _disposed = true;
                }
            }
        }
    }
}