using System.Collections.Concurrent;

namespace HubitatMqtt.Services
{
    /// <summary>
    /// Coordinates between webhook updates and periodic sync to prevent race conditions
    /// </summary>
    public class SyncCoordinator
    {
        private readonly ILogger<SyncCoordinator> _logger;
        private readonly SemaphoreSlim _syncSemaphore;
        private readonly ConcurrentDictionary<string, DateTime> _pendingWebhookUpdates;
        private bool _fullSyncInProgress;
        private DateTime _lastFullSync;

        public SyncCoordinator(ILogger<SyncCoordinator> logger)
        {
            _logger = logger;
            _syncSemaphore = new SemaphoreSlim(1, 1);
            _pendingWebhookUpdates = new ConcurrentDictionary<string, DateTime>();
            _fullSyncInProgress = false;
            _lastFullSync = DateTime.MinValue;
        }

        /// <summary>
        /// Acquires a lock for webhook processing, preventing conflicts with full sync
        /// </summary>
        public async Task<IDisposable> AcquireWebhookLockAsync(string deviceId, TimeSpan timeout = default)
        {
            if (timeout == default)
                timeout = TimeSpan.FromSeconds(5);

            if (!await _syncSemaphore.WaitAsync(timeout))
            {
                _logger.LogWarning("Timeout waiting for webhook lock for device {DeviceId}", deviceId);
                throw new TimeoutException($"Failed to acquire webhook lock for device {deviceId} within {timeout}");
            }

            if (_fullSyncInProgress)
            {
                _logger.LogDebug("Full sync in progress, deferring webhook update for device {DeviceId}", deviceId);
                _syncSemaphore.Release();
                
                // Record this webhook update as pending
                _pendingWebhookUpdates.TryAdd(deviceId, DateTime.UtcNow);
                
                throw new InvalidOperationException("Full sync in progress, webhook update deferred");
            }

            // Mark this device as having a pending webhook update
            _pendingWebhookUpdates.TryAdd(deviceId, DateTime.UtcNow);

            return new WebhookLockReleaser(this, deviceId);
        }

        /// <summary>
        /// Acquires a lock for full sync, preventing conflicts with webhook processing
        /// </summary>
        public async Task<IDisposable> AcquireFullSyncLockAsync(TimeSpan timeout = default)
        {
            if (timeout == default)
                timeout = TimeSpan.FromSeconds(30);

            if (!await _syncSemaphore.WaitAsync(timeout))
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

        private void ReleaseWebhookLock(string deviceId)
        {
            _logger.LogDebug("Released webhook lock for device {DeviceId}", deviceId);
            _syncSemaphore.Release();
        }

        private void ReleaseFullSyncLock()
        {
            _fullSyncInProgress = false;
            _lastFullSync = DateTime.UtcNow;
            _logger.LogDebug("Released full sync lock");
            _syncSemaphore.Release();
        }

        public bool IsFullSyncInProgress => _fullSyncInProgress;
        public DateTime LastFullSync => _lastFullSync;

        private class WebhookLockReleaser : IDisposable
        {
            private readonly SyncCoordinator _coordinator;
            private readonly string _deviceId;
            private bool _disposed;

            public WebhookLockReleaser(SyncCoordinator coordinator, string deviceId)
            {
                _coordinator = coordinator;
                _deviceId = deviceId;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _coordinator.ReleaseWebhookLock(_deviceId);
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