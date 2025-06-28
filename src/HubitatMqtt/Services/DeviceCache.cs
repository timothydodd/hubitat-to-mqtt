using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;

namespace HubitatMqtt.Services
{
    public class DeviceCache
    {
        private readonly ConcurrentDictionary<string, Device> _devices;
        private readonly ILogger<DeviceCache> _logger;
        private readonly ReaderWriterLockSlim _lock;

        public DeviceCache(ILogger<DeviceCache> logger)
        {
            _devices = new ConcurrentDictionary<string, Device>();
            _logger = logger;
            _lock = new ReaderWriterLockSlim();
        }

        public void Clear()
        {
            _lock.EnterWriteLock();
            try
            {
                var count = _devices.Count;
                _devices.Clear();
                _logger.LogDebug("Cleared {Count} devices from cache", count);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public Device? GetDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return null;
            }

            _lock.EnterReadLock();
            try
            {
                return _devices.TryGetValue(deviceId, out var device) ? device : null;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public List<Device> GetAllDevices()
        {
            _lock.EnterReadLock();
            try
            {
                return _devices.Values.ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public HashSet<string> GetAllDeviceIds()
        {
            _lock.EnterReadLock();
            try
            {
                return _devices.Keys.ToHashSet();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public void AddDevice(Device device)
        {
            if (device?.Id == null)
            {
                _logger.LogWarning("Attempted to add device with null ID to cache");
                return;
            }

            _lock.EnterWriteLock();
            try
            {
                var wasUpdated = _devices.ContainsKey(device.Id);
                _devices.AddOrUpdate(device.Id, device, (key, existingDevice) => device);
                
                _logger.LogDebug("{Action} device {DeviceId} ({DeviceName}) in cache", 
                    wasUpdated ? "Updated" : "Added", device.Id, device.Label ?? device.Name);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public bool UpdateDeviceAttribute(string deviceId, string attributeName, object? attributeValue)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(attributeName))
            {
                _logger.LogWarning("Attempted to update device attribute with invalid parameters: DeviceId={DeviceId}, AttributeName={AttributeName}", 
                    deviceId, attributeName);
                return false;
            }

            _lock.EnterWriteLock();
            try
            {
                if (_devices.TryGetValue(deviceId, out var cachedDevice))
                {
                    if (cachedDevice.Attributes == null)
                    {
                        cachedDevice.Attributes = new Dictionary<string, object?>();
                    }

                    cachedDevice.Attributes[attributeName] = attributeValue;
                    
                    _logger.LogDebug("Updated attribute {AttributeName}={AttributeValue} for device {DeviceId}", 
                        attributeName, attributeValue, deviceId);
                    return true;
                }
                else
                {
                    _logger.LogDebug("Device {DeviceId} not found in cache for attribute update", deviceId);
                    return false;
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public int GetDeviceCount()
        {
            _lock.EnterReadLock();
            try
            {
                return _devices.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public void Dispose()
        {
            _lock?.Dispose();
        }
    }
}
