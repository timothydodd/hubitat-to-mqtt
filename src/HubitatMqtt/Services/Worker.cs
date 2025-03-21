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

        public Worker(
            ILogger<Worker> logger,
            IConfiguration configuration,
            IMqttClient mqttClient,
            HubitatClient hubitatClient,
            MqttPublishService mqttPublishService,
            DeviceCache deviceCache,
            MqttSyncService mqttSyncService)
        {
            _logger = logger;
            _configuration = configuration;
            _mqttClient = mqttClient;
            _hubitatClient = hubitatClient;
            _mqttPublishService = mqttPublishService;
            _deviceCache = deviceCache;
            _clearTopicOnSync = configuration.GetValue<bool>("ClearTopicOnSync", true);
            _mqttSyncService = mqttSyncService;
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

                    // Sleep for a minute before checking again
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
                _logger.LogInformation("Fetching all devices from Hubitat");

                _deviceCache.Clear();

                // Fetch data from Hubitat API using the HubitatClient
                var devices = await _hubitatClient.GetAll();


                // Publish to MQTT if we have data and are connected
                if (devices != null && devices.Count > 0 && _mqttClient.IsConnected)
                {
                    foreach (var device in devices)
                    {
                        // Update the device cache
                        if (device.Id != null)
                        {
                            _deviceCache.AddDevice(device);
                        }

                        await _mqttPublishService.PublishDeviceToMqttAsync(device);
                    }
                    _lastFullPollTime = DateTime.Now;
                    if (_clearTopicOnSync)
                    {

                        await _mqttSyncService.SyncDevices(devices.Where(x => x.Id != null).Select(x => x.Id!).Distinct().ToHashSet() ?? new HashSet<string>());
                    }



                    _logger.LogInformation("Synchronization completed successfully for {DeviceCount} devices", devices.Count);
                }
                else if (!_mqttClient.IsConnected)
                {
                    _logger.LogWarning("MQTT client not connected. Skipping publish");
                }
                else if (devices == null || devices.Count == 0)
                {
                    _logger.LogWarning("No device data received from Hubitat");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while performing full poll");
            }
        }
    }
}
