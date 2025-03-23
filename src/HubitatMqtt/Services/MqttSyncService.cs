using System.Text.RegularExpressions;
using HubitatMqtt.Common;
using MQTTnet;

namespace HubitatMqtt.Services;

public class MqttSyncService
{
    private readonly ILogger<MqttSyncService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _baseTopic;
    public Regex WithAttributeRegex { get; }
    public Regex DeviceOnlyRegex { get; }
    public MqttSyncService(
        ILogger<MqttSyncService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";
        WithAttributeRegex = new Regex($"^{_baseTopic}/device/([^/]+)/([^/]+)$", RegexOptions.Compiled);
        DeviceOnlyRegex = new Regex($"^{_baseTopic}/device/([^/]+)$", RegexOptions.Compiled);
    }
    public async Task SyncDevices(HashSet<string> localDeviceCache)
    {
        using var client = await MqttBuilder.CreateClient(_logger, _configuration);
        if (!client.IsConnected)
        {
            _logger.LogWarning("MQTT client not connected. Skipping publish");
            return;
        }
        // Set to store discovered device IDs
        var discoveredDeviceIds = new Dictionary<string, List<string>>();

        // Create regex pattern to extract device ID from topics
        var topicPattern = new Regex($"^{_baseTopic}/device/([^/]+)/.*$");

        // Subscribe to all device topics
        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic($"{_baseTopic}/device/#"))
            .Build();

        await client.SubscribeAsync(subscribeOptions);

        Func<MqttApplicationMessageReceivedEventArgs, Task> handler = (args) =>
        {
            var topic = args.ApplicationMessage.Topic;

            var deviceMessage = ExtractData(topic);

            if (deviceMessage == null)
            {
                _logger.LogWarning("Invalid command topic format: {Topic}", topic);
                return Task.CompletedTask;
            }

            if (!discoveredDeviceIds.ContainsKey(deviceMessage.DeviceId))
            {
                discoveredDeviceIds.Add(deviceMessage.DeviceId, new List<string>());
            }

            if (!string.IsNullOrWhiteSpace(deviceMessage.AttributeName))
            {
                discoveredDeviceIds[deviceMessage.DeviceId].Add(deviceMessage.AttributeName);
            }

            return Task.CompletedTask;
        };
        // Handle received messages to extract device IDs
        client.ApplicationMessageReceivedAsync += handler;

        // Give some time to collect device IDs (you might need to adjust this)
        await Task.Delay(10000);
        client.ApplicationMessageReceivedAsync -= handler;
        await client.UnsubscribeAsync($"{_baseTopic}/device/#");
        // Find devices to remove (those not in local cache)
        var devicesToRemove = new HashSet<string>(discoveredDeviceIds.Keys);
        devicesToRemove.ExceptWith(localDeviceCache);

        // Clear topics for devices that don't exist in cache
        foreach (var deviceId in devicesToRemove)
        {
            // Clear the device topic by publishing retained empty message
            await client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic($"{_baseTopic}/device/{deviceId}")
                .WithPayload(new byte[0])
                .WithRetainFlag(true)
                .Build());

            foreach (var attribute in discoveredDeviceIds[deviceId])
            {
                // Clear the attribute topic by publishing retained empty message
                await client.PublishAsync(new MqttApplicationMessageBuilder()
                    .WithTopic($"{_baseTopic}/device/{deviceId}/{attribute}")
                    .WithPayload(new byte[0])
                    .WithRetainFlag(true)
                    .Build());
            }
            Console.WriteLine($"Cleared topics for device: {deviceId}");
        }
        await client.DisconnectAsync();
    }
    private DeviceMessage? ExtractData(string topic)
    {
        var withAttributeMatch = WithAttributeRegex.Match(topic);
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

        var deviceOnlyMatch = DeviceOnlyRegex.Match(topic);
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
