using System.Text.RegularExpressions;
using HubitatMqtt.Common;
using MQTTnet;

namespace HubitatMqtt.Services;

public class MqttSyncService
{
    private readonly ILogger<MqttSyncService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _baseTopic;
    public MqttSyncService(
        ILogger<MqttSyncService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";
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
        var discoveredDeviceIds = new HashSet<string>();

        // Create regex pattern to extract device ID from topics
        var topicPattern = new Regex($"^{_baseTopic}/device/([^/]+)/.*$");

        // Subscribe to all device topics
        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic($"{_baseTopic}/device/#"))
            .Build();

        await client.SubscribeAsync(subscribeOptions);

        Func<MqttApplicationMessageReceivedEventArgs, Task> handler = (e) =>
        {
            var topic = e.ApplicationMessage.Topic;
            var match = topicPattern.Match(topic);

            if (match.Success && match.Groups.Count > 1)
            {
                var deviceId = match.Groups[1].Value;
                if (!string.IsNullOrEmpty(deviceId))
                {
                    discoveredDeviceIds.Add(deviceId);
                }
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
        var devicesToRemove = new HashSet<string>(discoveredDeviceIds);
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

            Console.WriteLine($"Cleared topics for device: {deviceId}");
        }
        await client.DisconnectAsync();
    }
}

