using System.Text.RegularExpressions;
using HubitatMqtt.Common;
using MQTTnet;

namespace HubitatMqtt.Services;

public partial class MqttSyncService
{
    private readonly ILogger<MqttSyncService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IMqttClient _mqttClient;
    private readonly string _baseTopic;

    // Regex source generators for performance
    [GeneratedRegex(@"^hubitat/device/([^/]+)/([^/]+)$", RegexOptions.Compiled)]
    private static partial Regex WithAttributePattern();

    [GeneratedRegex(@"^hubitat/device/([^/]+)$", RegexOptions.Compiled)]
    private static partial Regex DeviceOnlyPattern();
    public MqttSyncService(
        ILogger<MqttSyncService> logger,
        IConfiguration configuration,
        IMqttClient mqttClient)
    {
        _logger = logger;
        _configuration = configuration;
        _mqttClient = mqttClient;
        _baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";
    }
    public async Task SyncDevices(HashSet<string> localDeviceCache)
    {
        if (!_mqttClient.IsConnected)
        {
            _logger.LogWarning("MQTT client not connected. Skipping topic sync");
            return;
        }
        
        _logger.LogInformation("Starting MQTT topic synchronization for {DeviceCount} devices", localDeviceCache.Count);
        // Set to store discovered device IDs
        var discoveredDeviceIds = new Dictionary<string, List<string>>();
        var lastMessageTime = DateTime.UtcNow;
        var messageLock = new object();

        // Subscribe to all device topics
        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic($"{_baseTopic}/device/#"))
            .Build();

        await _mqttClient.SubscribeAsync(subscribeOptions);

        Func<MqttApplicationMessageReceivedEventArgs, Task> handler = (args) =>
        {
            var topic = args.ApplicationMessage.Topic;

            var deviceMessage = ExtractData(topic);

            if (deviceMessage == null)
            {
                _logger.LogWarning("Invalid command topic format: {Topic}", topic);
                return Task.CompletedTask;
            }

            lock (messageLock)
            {
                if (!discoveredDeviceIds.ContainsKey(deviceMessage.DeviceId))
                {
                    discoveredDeviceIds.Add(deviceMessage.DeviceId, new List<string>());
                }

                if (!string.IsNullOrWhiteSpace(deviceMessage.AttributeName))
                {
                    discoveredDeviceIds[deviceMessage.DeviceId].Add(deviceMessage.AttributeName);
                }

                lastMessageTime = DateTime.UtcNow;
            }

            return Task.CompletedTask;
        };

        // Handle received messages to extract device IDs
        _mqttClient.ApplicationMessageReceivedAsync += handler;

        try
        {
            // Intelligent timeout: wait for message inactivity
            var maxTotalWait = TimeSpan.FromMilliseconds(_configuration.GetValue("MQTT:SyncTimeoutMs", 10000));
            var inactivityThreshold = TimeSpan.FromMilliseconds(_configuration.GetValue("MQTT:SyncInactivityMs", 500));
            var checkInterval = TimeSpan.FromMilliseconds(50);
            var startTime = DateTime.UtcNow;

            while (DateTime.UtcNow - startTime < maxTotalWait)
            {
                await Task.Delay(checkInterval);

                DateTime lastMsg;
                lock (messageLock)
                {
                    lastMsg = lastMessageTime;
                }

                // If no messages for the inactivity threshold, we're done
                if (DateTime.UtcNow - lastMsg > inactivityThreshold)
                {
                    _logger.LogDebug("Topic discovery completed after {Duration}ms of inactivity",
                        (DateTime.UtcNow - startTime).TotalMilliseconds);
                    break;
                }
            }
        }
        finally
        {
            _mqttClient.ApplicationMessageReceivedAsync -= handler;
            await _mqttClient.UnsubscribeAsync($"{_baseTopic}/device/#");
        }
        // Find devices to remove (those not in local cache)
        var devicesToRemove = new HashSet<string>(discoveredDeviceIds.Keys);
        devicesToRemove.ExceptWith(localDeviceCache);

        // Clear topics for devices that don't exist in cache
        foreach (var deviceId in devicesToRemove)
        {
            try
            {
                // Clear the device topic by publishing retained empty message
                await _mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                    .WithTopic($"{_baseTopic}/device/{deviceId}")
                    .WithPayload(Array.Empty<byte>())
                    .WithRetainFlag(true)
                    .Build());

                foreach (var attribute in discoveredDeviceIds[deviceId])
                {
                    // Clear the attribute topic by publishing retained empty message
                    await _mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                        .WithTopic($"{_baseTopic}/device/{deviceId}/{attribute}")
                        .WithPayload(Array.Empty<byte>())
                        .WithRetainFlag(true)
                        .Build());
                }
                
                _logger.LogDebug("Cleared topics for removed device: {DeviceId}", deviceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear topics for device {DeviceId}", deviceId);
            }
        }
        
        _logger.LogInformation("MQTT topic synchronization completed. Cleared {RemovedCount} removed devices", devicesToRemove.Count);
    }
    private DeviceMessage? ExtractData(string topic)
    {
        var withAttributeMatch = WithAttributePattern().Match(topic);
        if (withAttributeMatch.Success)
        {
            var deviceId = withAttributeMatch.Groups[1].Value;
            var attribute = withAttributeMatch.Groups[2].Value;
            return new DeviceMessage
            {
                DeviceId = deviceId,
                AttributeName = attribute
            };
        }

        var deviceOnlyMatch = DeviceOnlyPattern().Match(topic);
        if (deviceOnlyMatch.Success)
        {
            var deviceId = deviceOnlyMatch.Groups[1].Value;
            return new DeviceMessage
            {
                DeviceId = deviceId,
            };
        }
        return null;
    }
}

public class DeviceMessage
{
    public required string DeviceId { get; set; }
    public string? AttributeName { get; set; }
}
