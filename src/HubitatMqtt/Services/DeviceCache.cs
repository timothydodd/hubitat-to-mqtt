using Microsoft.Extensions.Caching.Memory;

namespace HubitatMqtt.Services
{
    public class DeviceCache
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<DeviceCache> _logger;
        const string CACHE_KEY = "Devices";

        public DeviceCache(IMemoryCache cache, ILogger<DeviceCache> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        public void Clear()
        {
            _cache.Remove(CACHE_KEY);
        }

        public Device? GetDevice(string deviceId)
        {
            var cacheKey = $"{CACHE_KEY}_{deviceId}";
            if (_cache.TryGetValue(cacheKey, out Device? device))
            {
                return device;
            }
            return null;
        }

        public List<Device> GetAllDevices()
        {
            var devices = new List<Device>();

            // We need to scan the cache for all device keys
            // This is a bit inefficient, but MemoryCache doesn't provide a way to enumerate all keys
            // In a production app, you might want to maintain a separate list of device IDs

            // Get all cache entries that start with CACHE_KEY
            var allCacheKeys = GetAllKeysFromCache();

            foreach (var key in allCacheKeys)
            {
                if (key.StartsWith(CACHE_KEY) && _cache.TryGetValue(key, out Device? device) && device != null)
                {
                    devices.Add(device);
                }
            }

            return devices;
        }

        private List<string> GetAllKeysFromCache()
        {
            // This is a helper method to get all keys from the MemoryCache
            // Note: This uses reflection and is not ideal for production use
            // In a real-world app, you would maintain a separate collection of device IDs

            var keys = new List<string>();

            // Get the entries field via reflection
            var field = typeof(MemoryCache).GetField("_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                var entriesCollection = field.GetValue(_cache);
                if (entriesCollection != null)
                {
                    // Get the Keys property via reflection
                    var entriesType = entriesCollection.GetType();
                    var keysProperty = entriesType.GetProperty("Keys");
                    if (keysProperty != null)
                    {
                        var keysCollection = keysProperty.GetValue(entriesCollection) as System.Collections.ICollection;
                        if (keysCollection != null)
                        {
                            foreach (var key in keysCollection)
                            {
                                keys.Add(key.ToString());
                            }
                        }
                    }
                }
            }

            return keys;
        }

        public void AddDevice(Device device)
        {
            if (device?.Id == null)
                return;

            var cacheKey = $"{CACHE_KEY}_{device.Id}";
            _cache.Set(cacheKey, device);
        }

        public void UpdateCache(DeviceEvent data)
        {
            if (data.Content == null)
                return;

            var cacheKey = $"{CACHE_KEY}_{data.Content.DeviceId}";
            if (_cache.TryGetValue(cacheKey, out Device? cachedDevice))
            {
                if (cachedDevice == null)
                {
                    return;
                }

                if (cachedDevice.Attributes == null)
                {
                    cachedDevice.Attributes = new Dictionary<string, object?>();
                }

                if (cachedDevice.Attributes.ContainsKey(data.Content.Name))
                {
                    cachedDevice.Attributes[data.Content.Name] = data?.Content?.Value;
                }
                else
                {
                    cachedDevice.Attributes.Add(data.Content.Name, data?.Content?.Value);
                }

                // Update the cache with the modified device
                _cache.Set(cacheKey, cachedDevice);
            }
        }
    }
}
