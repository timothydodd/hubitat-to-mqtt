using System.Text.RegularExpressions;
using MQTTnet;
using HubitatMqtt.Services;

namespace HubitatToMqtt
{
    public partial class MqttCommandHandler : IHostedService
    {
        private readonly ILogger<MqttCommandHandler> _logger;
        private readonly IConfiguration _configuration;
        private readonly IMqttClient _mqttClient;
        private readonly HubitatClient _hubitatClient;
        private readonly MqttPublishService _mqttPublishService;
        private readonly DeviceCache _deviceCache;
        private readonly string baseTopic;

        // Regex source generators for performance
        [GeneratedRegex(@"^hubitat/device/([^/]+)/command/([^/]+)(/([^/]+))?$", RegexOptions.Compiled)]
        private static partial Regex DeviceCommandRegex();

        [GeneratedRegex(@"^hubitat/([^/]+)/command/([^/]+)$", RegexOptions.Compiled)]
        private static partial Regex NamedDeviceCommandRegex();
        public MqttCommandHandler(
            ILogger<MqttCommandHandler> logger,
            IConfiguration configuration,
            IMqttClient mqttClient,
            HubitatClient hubitatClient,
            MqttPublishService mqttPublishService,
            DeviceCache deviceCache)
        {
            _logger = logger;
            _configuration = configuration;
            _mqttClient = mqttClient;
            _hubitatClient = hubitatClient;
            _mqttPublishService = mqttPublishService;
            _deviceCache = deviceCache;
            baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("MQTT Command Handler starting");

            if (_mqttClient.IsConnected)
            {
                await SubscribeToCommandTopics();
            }

            // Handle reconnection events to resubscribe
            _mqttClient.ConnectedAsync += async (e) =>
            {
                await SubscribeToCommandTopics();

            };

            // Setup message handler
            _mqttClient.ApplicationMessageReceivedAsync += HandleCommandMessage;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("MQTT Command Handler stopping");
            return Task.CompletedTask;
        }

        private async Task SubscribeToCommandTopics()
        {
            try
            {

                // Subscribe to direct device ID command topics
                await _mqttClient.SubscribeAsync($"{baseTopic}/device/+/command/+");

                // Subscribe to named device command topics (optional)
                await _mqttClient.SubscribeAsync($"{baseTopic}/device/+/command/+/+");

                _logger.LogInformation("Subscribed to command topics");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to subscribe to command topics");
            }
        }

        private async Task HandleCommandMessage(MqttApplicationMessageReceivedEventArgs args)
        {
            try
            {
                var topic = args.ApplicationMessage.Topic;

                // Only process command topics - ignore other device topics
                if (!topic.Contains("/command/"))
                {
                    return; // Silently ignore non-command topics
                }

                _logger.LogDebug("Received command on topic {Topic}", topic);

                // Extract device ID and command from the topic
                var deviceId = ExtractDeviceId(topic);
                var command = ExtractCommand(topic);
                var value = ExtractValue(topic);
                if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(command))
                {
                    _logger.LogWarning("Invalid command topic format: {Topic}", topic);
                    return;
                }

                // Send the command to Hubitat
                await SendCommandToHubitat(deviceId, command, value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing command message");
            }
        }

        private string? ExtractDeviceId(string topic)
        {
            // Match topic pattern for direct device ID: hubitat/device/{deviceId}/command/{command}[/{value}]
            var directMatch = DeviceCommandRegex().Match(topic);
            if (directMatch.Success && directMatch.Groups.Count > 1)
            {
                return directMatch.Groups[1].Value;
            }

            // If no direct match, we need to look up the device by name
            var namedMatch = NamedDeviceCommandRegex().Match(topic);
            if (namedMatch.Success && namedMatch.Groups.Count > 1)
            {
                var deviceName = namedMatch.Groups[1].Value;
                return ResolveDeviceIdFromName(deviceName);
            }

            return null;
        }

        private string? ExtractCommand(string topic)
        {
            // Try device command pattern first
            var directMatch = DeviceCommandRegex().Match(topic);
            if (directMatch.Success && directMatch.Groups.Count > 2)
            {
                return directMatch.Groups[2].Value;
            }

            // Try named device pattern
            var namedMatch = NamedDeviceCommandRegex().Match(topic);
            if (namedMatch.Success && namedMatch.Groups.Count > 2)
            {
                return namedMatch.Groups[2].Value;
            }

            return null;
        }
        private string? ExtractValue(string topic)
        {
            // Extract optional value from hubitat/device/{id}/command/{cmd}/{value}
            var directMatch = DeviceCommandRegex().Match(topic);
            if (directMatch.Success && directMatch.Groups.Count > 4 && directMatch.Groups[4].Success)
            {
                return directMatch.Groups[4].Value;
            }
            return null;
        }
        private string? ResolveDeviceIdFromName(string deviceName)
        {
            // In a production app, you would have a proper device name to ID mapping
            // This is a simplified placeholder that would need to be implemented
            // based on your device tracking approach

            // For now, we'll just handle the case where the name format is "device_ID"
            if (deviceName.StartsWith("device_"))
            {
                var potentialId = deviceName.Substring(7); // Remove "device_" prefix
                if (int.TryParse(potentialId, out _))
                {
                    return potentialId;
                }
            }

            _logger.LogWarning("Could not resolve device ID for name: {DeviceName}", deviceName);
            return null;
        }

        private async Task SendCommandToHubitat(string deviceId, string command, string? value)
        {
            try
            {
                _logger.LogDebug("Sending command to Hubitat - Device: {DeviceId}, Command: {Command}, Value: {Value}",
                    deviceId, command, value);

                await _hubitatClient.SendCommand(deviceId, command, value);

                // After sending the command, fetch the updated device state
                // This ensures MQTT topics reflect the new state after the command
                var device = await _hubitatClient.Get(deviceId);
                if (device != null)
                {
                    _deviceCache.AddDevice(device);
                    await _mqttPublishService.PublishDeviceToMqttAsync(device);
                    _logger.LogDebug("Updated device state after command execution");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send command to Hubitat - Device: {DeviceId}, Command: {Command}",
                    deviceId, command);
            }
        }
    }
}
