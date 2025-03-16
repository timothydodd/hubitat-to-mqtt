using System.Text.RegularExpressions;
using MQTTnet;

namespace HubitatToMqtt
{
    public class MqttCommandHandler : IHostedService
    {
        private readonly ILogger<MqttCommandHandler> _logger;
        private readonly IConfiguration _configuration;
        private readonly IMqttClient _mqttClient;
        private readonly HubitatClient _hubitatClient;

        public MqttCommandHandler(
            ILogger<MqttCommandHandler> logger,
            IConfiguration configuration,
            IMqttClient mqttClient,
            HubitatClient hubitatClient)
        {
            _logger = logger;
            _configuration = configuration;
            _mqttClient = mqttClient;
            _hubitatClient = hubitatClient;
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
                var baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";

                // Subscribe to direct device ID command topics
                await _mqttClient.SubscribeAsync($"{baseTopic}/device/+/command/+");

                // Subscribe to named device command topics (optional)
                await _mqttClient.SubscribeAsync($"{baseTopic}/+/command/+");

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
                var payload = args.ApplicationMessage.ConvertPayloadToString();

                _logger.LogInformation("Received command on topic {Topic} with payload {Payload}", topic, payload);

                // Extract device ID and command from the topic
                var deviceId = ExtractDeviceId(topic);
                var command = ExtractCommand(topic);

                if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(command))
                {
                    _logger.LogWarning("Invalid command topic format: {Topic}", topic);
                    return;
                }

                // Send the command to Hubitat
                await SendCommandToHubitat(deviceId, command, payload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing command message");
            }
        }

        private string? ExtractDeviceId(string topic)
        {
            var baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";

            // Match topic pattern for direct device ID: hubitat/device/{deviceId}/command/{command}
            var directIdPattern = $"^{baseTopic}/device/([^/]+)/command/[^/]+$";
            var directMatch = Regex.Match(topic, directIdPattern);

            if (directMatch.Success && directMatch.Groups.Count > 1)
            {
                return directMatch.Groups[1].Value;
            }

            // If no direct match, we need to look up the device by name
            var namedPattern = $"^{baseTopic}/([^/]+)/command/[^/]+$";
            var namedMatch = Regex.Match(topic, namedPattern);

            if (namedMatch.Success && namedMatch.Groups.Count > 1)
            {
                var deviceName = namedMatch.Groups[1].Value;
                return ResolveDeviceIdFromName(deviceName);
            }

            return null;
        }

        private string? ExtractCommand(string topic)
        {
            var baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";

            // Match command pattern: .../command/{command}
            var commandPattern = $"^{baseTopic}/.+/command/([^/]+)$";
            var match = Regex.Match(topic, commandPattern);

            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
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

        private async Task SendCommandToHubitat(string deviceId, string command, string value)
        {
            try
            {
                _logger.LogInformation("Sending command to Hubitat - Device: {DeviceId}, Command: {Command}, Value: {Value}",
                    deviceId, command, value);

                await _hubitatClient.SendCommand(deviceId, command, value);

                // After sending the command, we should fetch the updated device state
                // This ensures MQTT topics reflect the new state after the command
                var device = await _hubitatClient.Get(deviceId);
                if (device != null)
                {
                    // Publish updated device state to MQTT
                    // You would implement this by calling your existing publish method
                    // This would require refactoring your PublishDeviceToMqttAsync method to be accessible

                    _logger.LogInformation("Updated device state after command execution");
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
