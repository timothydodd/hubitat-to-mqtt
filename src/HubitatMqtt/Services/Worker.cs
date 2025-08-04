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
        private readonly bool _clearTopicOnSync = false;
        private readonly MqttSyncService _mqttSyncService;
        private readonly SyncCoordinator _syncCoordinator;

        public Worker(
            ILogger<Worker> logger,
            IConfiguration configuration,
            IMqttClient mqttClient,
            HubitatClient hubitatClient,
            MqttPublishService mqttPublishService,
            DeviceCache deviceCache,
            MqttSyncService mqttSyncService,
            SyncCoordinator syncCoordinator)
        {
            _logger = logger;
            _configuration = configuration;
            _mqttClient = mqttClient;
            _hubitatClient = hubitatClient;
            _mqttPublishService = mqttPublishService;
            _deviceCache = deviceCache;
            _clearTopicOnSync = configuration.GetValue<bool>("ClearTopicOnSync", true);
            _mqttSyncService = mqttSyncService;
            _syncCoordinator = syncCoordinator;
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
                    
                    // Suggest garbage collection after long idle periods to optimize memory
                    if (DateTime.UtcNow - _lastFullPollTime > TimeSpan.FromMinutes(30))
                    {
                        GC.Collect(0, GCCollectionMode.Optimized);
                    }
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
                
                _logger.LogInformation("Full sync lock acquired, fetching all devices from Hubitat");

                _deviceCache.Clear();

                // Fetch data from Hubitat API using the HubitatClient
                var devices = await _hubitatClient.GetAll();

                // Publish to MQTT if we have data
                if (devices != null && devices.Count > 0)
                {
                    int publishedCount = 0;
                    int failedCount = 0;
                    
                    foreach (var device in devices)
                    {
                        try
                        {
                            // Update the device cache
                            if (device.Id != null)
                            {
                                _deviceCache.AddDevice(device);
                            }

                            await _mqttPublishService.PublishDeviceToMqttAsync(device);
                            publishedCount++;
                        }
                        catch (MqttPublishException ex)
                        {
                            _logger.LogError(ex, "Failed to publish device {DeviceId} during full sync", device.Id);
                            failedCount++;
                            // Continue with other devices
                        }
                    }
                    
                    _lastFullPollTime = DateTime.UtcNow;
                    
                    if (_clearTopicOnSync)
                    {
                        try
                        {
                            var deviceIds = _deviceCache.GetAllDeviceIds();
                            await _mqttSyncService.SyncDevices(deviceIds);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to sync MQTT topics during full poll");
                        }
                    }

                    // Clear pending webhook updates for devices that were successfully synced
                    var syncedDeviceIds = devices.Where(d => d.Id != null).Select(d => d.Id!).ToHashSet();
                    _syncCoordinator.ClearPendingWebhookUpdates(syncedDeviceIds);
                    
                    // Cleanup unused device locks to prevent memory leaks
                    _syncCoordinator.CleanupUnusedDeviceLocks(syncedDeviceIds);

                    if (failedCount > 0)
                    {
                        _logger.LogWarning("Synchronization completed with {PublishedCount} devices published and {FailedCount} failures", publishedCount, failedCount);
                    }
                    else
                    {
                        _logger.LogInformation("Full synchronization completed successfully for {DeviceCount} devices", devices.Count);
                    }

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
    }
}
