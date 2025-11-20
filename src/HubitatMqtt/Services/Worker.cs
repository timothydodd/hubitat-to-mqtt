using HubitatMqtt.Services;
using MQTTnet;

namespace HubitatToMqtt
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _configuration;
        private readonly IMqttClient _mqttClient;
        private readonly HubitatClient _hubitatClient;
        private readonly MqttPublishService _mqttPublishService;
        private readonly DeviceCache _deviceCache;
        private DateTime _lastFullPollTime = DateTime.MinValue;
        private readonly MqttSyncService _mqttSyncService;
        private readonly SyncCoordinator _syncCoordinator;
        private readonly MqttDeviceReader _mqttDeviceReader;

        public Worker(
            ILogger<Worker> logger,
            IConfiguration configuration,
            IMqttClient mqttClient,
            HubitatClient hubitatClient,
            MqttPublishService mqttPublishService,
            DeviceCache deviceCache,
            MqttSyncService mqttSyncService,
            SyncCoordinator syncCoordinator,
            MqttDeviceReader mqttDeviceReader)
        {
            _logger = logger;
            _configuration = configuration;
            _mqttClient = mqttClient;
            _hubitatClient = hubitatClient;
            _mqttPublishService = mqttPublishService;
            _deviceCache = deviceCache;
            _mqttSyncService = mqttSyncService;
            _syncCoordinator = syncCoordinator;
            _mqttDeviceReader = mqttDeviceReader;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Check if we need to do a sync poll
                    bool shouldPoll = ShouldPerformSyncPoll();

                    if (shouldPoll)
                    {
                        _logger.LogInformation("Performing scheduled synchronization poll at: {time}", DateTimeOffset.Now);
                        await PerformFullPollAsync();
                    }

                    // Sleep for 2 minutes before checking again
                    await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while checking for poll schedule");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // Wait before retrying
                }
            }
        }

        private bool ShouldPerformSyncPoll()
        {
            // Get the sync interval from config (default 4 hours)
            int syncIntervalHours = _configuration.GetValue<int>("SyncPollIntervalHours", 4);

            // Check if polling is disabled (set to 0 or negative)
            if (syncIntervalHours <= 0)
            {
                return false;
            }

            // Check if it's time for a sync poll
            return DateTime.Now > _lastFullPollTime.AddHours(syncIntervalHours);
        }

        private async Task PerformFullPollAsync()
        {
            try
            {
                _logger.LogInformation("Starting full device synchronization");

                // Acquire full sync lock to prevent webhook interference
                using var syncLock = await _syncCoordinator.AcquireFullSyncLockAsync();

                _logger.LogDebug("Full sync lock acquired, fetching all devices from Hubitat");

                // Fetch data from Hubitat API using the HubitatClient
                var devices = await _hubitatClient.GetAll();

                // Publish to MQTT if we have data
                if (devices != null && devices.Count > 0)
                {
                    // Read current device states from MQTT
                    _logger.LogDebug("Reading current device states from MQTT");
                    var deviceIds = devices.Where(d => d.Id != null).Select(d => d.Id!).ToList();
                    var mqttReadResult = await _mqttDeviceReader.ReadDevicesFromMqttAsync(deviceIds);
                    _logger.LogDebug("Read {Count} devices from MQTT retained messages, discovered {TotalCount} total device IDs in MQTT",
                        mqttReadResult.Devices.Count, mqttReadResult.AllDiscoveredDeviceIds.Count);

                    // Detect which devices have changed compared to MQTT state
                    var changedDevices = new List<Device>();
                    var unchangedCount = 0;

                    foreach (var device in devices)
                    {
                        if (device.Id != null)
                        {
                            // Compare against MQTT state, not cache
                            if (!mqttReadResult.Devices.TryGetValue(device.Id, out var mqttDevice) ||
                                HasDeviceChanged(mqttDevice, device))
                            {
                                changedDevices.Add(device);
                                _logger.LogDebug("Device {DeviceId} ({DeviceName}) has changes, will publish to MQTT",
                                    device.Id, device.Label ?? device.Name);
                            }
                            else
                            {
                                unchangedCount++;
                            }

                            // Update cache with latest data
                            _deviceCache.AddDevice(device);
                        }
                    }

                    _logger.LogInformation("Full sync detected {ChangedCount} changed devices and {UnchangedCount} unchanged devices",
                        changedDevices.Count, unchangedCount);

                    // Use batch publishing for better performance - only publish changed devices
                    if (changedDevices.Count > 0)
                    {
                        try
                        {
                            await _mqttPublishService.PublishDevicesBatchAsync(changedDevices);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to batch publish devices during full sync");
                        }
                    }
                    else
                    {
                        _logger.LogInformation("No device changes detected during full sync, skipping MQTT publish");
                    }

                    _lastFullPollTime = DateTime.UtcNow;

                    // Clean up stale MQTT topics (devices that no longer exist in Hubitat)
                    try
                    {
                        var currentDeviceIds = _deviceCache.GetAllDeviceIds();
                        await _mqttDeviceReader.CleanupStaleDevicesAsync(currentDeviceIds, mqttReadResult.AllDiscoveredDeviceIds);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to cleanup stale MQTT topics during full poll");
                    }

                    // Clear pending webhook updates for devices that were successfully synced
                    var syncedDeviceIds = devices.Where(d => d.Id != null).Select(d => d.Id!).ToHashSet();
                    _syncCoordinator.ClearPendingWebhookUpdates(syncedDeviceIds);

                    // Cleanup unused device locks to prevent memory leaks
                    _syncCoordinator.CleanupUnusedDeviceLocks(syncedDeviceIds);

                    _logger.LogInformation("Full synchronization completed successfully for {DeviceCount} devices", devices.Count);

                    // Process any devices that had webhook updates during sync
                    await ProcessPendingWebhookUpdates();
                }
                else
                {
                    _logger.LogWarning("No device data received from Hubitat API");
                }
            }
            catch (TimeoutException ex)
            {
                _logger.LogError(ex, "Timeout acquiring full sync lock");
            }
            catch (HubitatApiException ex)
            {
                _logger.LogError(ex, "Hubitat API error occurred while performing full poll");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error occurred while performing full poll");
            }
        }

        private async Task ProcessPendingWebhookUpdates()
        {
            var pendingDevices = _syncCoordinator.GetDevicesWithPendingWebhookUpdates();

            if (pendingDevices.Count > 0)
            {
                _logger.LogInformation("Processing {Count} devices with pending webhook updates", pendingDevices.Count);

                foreach (var deviceId in pendingDevices)
                {
                    try
                    {
                        // Re-fetch the device to get the latest state
                        var device = await _hubitatClient.Get(deviceId);
                        if (device != null)
                        {
                            _deviceCache.AddDevice(device);
                            await _mqttPublishService.PublishDeviceToMqttAsync(device);
                            _logger.LogDebug("Processed pending webhook update for device {DeviceId}", deviceId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process pending webhook update for device {DeviceId}", deviceId);
                    }
                }

                // Clear the processed updates
                _syncCoordinator.ClearPendingWebhookUpdates(pendingDevices);
            }
        }

        /// <summary>
        /// Compares two device states to determine if there are any changes
        /// </summary>
        private bool HasDeviceChanged(Device? mqttDevice, Device newDevice)
        {
            if (mqttDevice == null)
            {
                return true; // Device doesn't exist in MQTT, consider it changed
            }

            // Compare basic properties
            if (mqttDevice.Label != newDevice.Label ||
                mqttDevice.Name != newDevice.Name ||
                mqttDevice.Type != newDevice.Type)
            {
                return true;
            }

            // Compare attributes
            if (mqttDevice.Attributes == null && newDevice.Attributes == null)
            {
                return false; // Both have no attributes, no change
            }

            if (mqttDevice.Attributes == null || newDevice.Attributes == null)
            {
                return true; // One has attributes, the other doesn't
            }

            if (mqttDevice.Attributes.Count != newDevice.Attributes.Count)
            {
                return true; // Different number of attributes
            }

            // Compare each attribute
            foreach (var attr in newDevice.Attributes)
            {
                if (!mqttDevice.Attributes.TryGetValue(attr.Key, out var mqttValue))
                {
                    return true; // New attribute added
                }

                // Compare attribute values
                if (!AreAttributeValuesEqual(mqttValue, attr.Value))
                {
                    return true;
                }
            }

            return false; // No changes detected
        }

        /// <summary>
        /// Compares two attribute values for equality
        /// </summary>
        private bool AreAttributeValuesEqual(object? value1, object? value2)
        {
            if (value1 == null && value2 == null)
                return true;
            if (value1 == null || value2 == null)
                return false;

            // For value types and strings, use direct comparison
            if (value1 is string || value1.GetType().IsValueType)
            {
                return value1.Equals(value2);
            }

            // For complex types, serialize and compare JSON
            try
            {
                var json1 = System.Text.Json.JsonSerializer.Serialize(value1, Constants.JsonOptions);
                var json2 = System.Text.Json.JsonSerializer.Serialize(value2, Constants.JsonOptions);
                return json1 == json2;
            }
            catch
            {
                // If serialization fails, assume they're different
                return false;
            }
        }
    }
}
