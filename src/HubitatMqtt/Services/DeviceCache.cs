using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;

namespace HubitatMqtt.Services
{
    public class DeviceCache : IDisposable
    {
        private readonly ConcurrentDictionary<string, Device> _devices;
        private readonly ILogger<DeviceCache> _logger;

        public DeviceCache(ILogger<DeviceCache> logger)
        {
            _devices = new ConcurrentDictionary<string, Device>();
            _logger = logger;
        }

        public void Clear()
        {
            var count = _devices.Count;
            _devices.Clear();
            _logger.LogDebug("Cleared {Count} devices from cache", count);
        }

        public Device? GetDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return null;
            }

            return _devices.TryGetValue(deviceId, out var device) ? device : null;
        }

        public List<Device> GetAllDevices()
        {
            return _devices.Values.ToList();
        }

        public HashSet<string> GetAllDeviceIds()
        {
            return _devices.Keys.ToHashSet();
        }

        public void AddDevice(Device device)
        {
            if (device?.Id == null)
            {
                _logger.LogWarning("Attempted to add device with null ID to cache");
                return;
            }

            var wasUpdated = _devices.ContainsKey(device.Id);
            _devices.AddOrUpdate(device.Id, device, (key, existingDevice) => device);
            
            _logger.LogDebug("{Action} device {DeviceId} ({DeviceName}) in cache", 
                wasUpdated ? "Updated" : "Added", device.Id, device.Label ?? device.Name);
        }

        public bool UpdateDeviceAttribute(string deviceId, string attributeName, object? attributeValue)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(attributeName))
            {
                _logger.LogWarning("Attempted to update device attribute with invalid parameters: DeviceId={DeviceId}, AttributeName={AttributeName}", 
                    deviceId, attributeName);
                return false;
            }

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

        public int GetDeviceCount()
        {
            return _devices.Count;
        }

        public void Dispose()
        {
            // ConcurrentDictionary doesn't need disposal
        }
    }
}
