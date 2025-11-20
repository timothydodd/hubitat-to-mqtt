using System.Collections.Concurrent;
using System.Text.Json;
using MQTTnet;

namespace HubitatToMqtt
{
    /// <summary>
    /// Service for reading device state from MQTT retained messages
    /// </summary>
    public class MqttDeviceReader
    {
        private readonly ILogger<MqttDeviceReader> _logger;
        private readonly IConfiguration _configuration;

        public MqttDeviceReader(
            ILogger<MqttDeviceReader> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        /// <summary>
        /// Reads all device states from MQTT retained messages for the given device IDs.
        /// Returns both the devices and all discovered device IDs in MQTT.
        /// </summary>
        public async Task<MqttDeviceReadResult> ReadDevicesFromMqttAsync(IEnumerable<string> deviceIds, CancellationToken cancellationToken = default)
        {
            var devices = new ConcurrentDictionary<string, Device?>();
            var discoveredDeviceIds = new ConcurrentBag<string>();
            var baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";

            // Create a temporary MQTT client for reading
            using var mqttClient = await CreateTemporaryClientAsync();

            if (!mqttClient.IsConnected)
            {
                _logger.LogWarning("Failed to connect MQTT client for reading device states");
                return new MqttDeviceReadResult
                {
                    Devices = devices.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    AllDiscoveredDeviceIds = new HashSet<string>()
                };
            }

            var receivedTopics = new ConcurrentBag<string>();
            var completionSource = new TaskCompletionSource<bool>();
            var expectedTopics = deviceIds.Select(id => $"{baseTopic}/device/{id}").ToHashSet();
            var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Set up message handler to receive retained messages
            mqttClient.ApplicationMessageReceivedAsync += async (e) =>
            {
                try
                {
                    var topic = e.ApplicationMessage.Topic;

                    // Track all device IDs we discover (from any device topic)
                    if (topic.StartsWith($"{baseTopic}/device/"))
                    {
                        var parts = topic.Substring($"{baseTopic}/device/".Length).Split('/');
                        if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0]))
                        {
                            discoveredDeviceIds.Add(parts[0]);
                        }
                    }

                    // Only deserialize devices we explicitly asked for
                    if (expectedTopics.Contains(topic))
                    {
                        receivedTopics.Add(topic);
                        var payload = e.ApplicationMessage.ConvertPayloadToString();

                        if (!string.IsNullOrEmpty(payload))
                        {
                            try
                            {
                                var device = JsonSerializer.Deserialize<Device>(payload, Constants.JsonOptions);
                                if (device?.Id != null)
                                {
                                    devices.TryAdd(device.Id, device);
                                    _logger.LogTrace("Read device {DeviceId} from MQTT topic {Topic}", device.Id, topic);
                                }
                            }
                            catch (JsonException ex)
                            {
                                _logger.LogWarning(ex, "Failed to deserialize device from topic {Topic}", topic);
                            }
                        }
                    }

                    // Check if we've received all expected topics
                    if (receivedTopics.Count >= expectedTopics.Count)
                    {
                        completionSource.TrySetResult(true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing MQTT message for topic {Topic}", e.ApplicationMessage.Topic);
                }

                await Task.CompletedTask;
            };

            try
            {
                // Subscribe to ALL device topics to discover all devices in MQTT
                await mqttClient.SubscribeAsync($"{baseTopic}/device/+");
                // Also subscribe to attribute-specific topics
                await mqttClient.SubscribeAsync($"{baseTopic}/device/+/+");
                _logger.LogDebug("Subscribed to all device topics to read retained messages and discover all devices");

                // Wait for messages or timeout (5 seconds should be enough for retained messages)
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    await completionSource.Task.WaitAsync(timeoutCts.Token);
                    _logger.LogDebug("Received all {Count} expected retained messages", expectedTopics.Count);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Timeout waiting for retained messages. Received {ReceivedCount}/{ExpectedCount} messages",
                        receivedTopics.Count, expectedTopics.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading devices from MQTT");
            }
            finally
            {
                timeoutCts.Dispose();
            }

            return new MqttDeviceReadResult
            {
                Devices = devices.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                AllDiscoveredDeviceIds = discoveredDeviceIds.Distinct().ToHashSet()
            };
        }

        /// <summary>
        /// Cleans up stale device topics from MQTT (devices that exist in MQTT but not in the provided device list)
        /// </summary>
        public async Task CleanupStaleDevicesAsync(HashSet<string> currentDeviceIds, HashSet<string> mqttDeviceIds)
        {
            var devicesToRemove = new HashSet<string>(mqttDeviceIds);
            devicesToRemove.ExceptWith(currentDeviceIds);

            if (devicesToRemove.Count == 0)
            {
                _logger.LogDebug("No stale devices found in MQTT");
                return;
            }

            _logger.LogInformation("Found {Count} stale devices in MQTT to clean up", devicesToRemove.Count);

            var baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";
            using var mqttClient = await CreateTemporaryClientAsync();

            if (!mqttClient.IsConnected)
            {
                _logger.LogWarning("Failed to connect MQTT client for cleanup");
                return;
            }

            // Subscribe to get all topics for devices we're removing
            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter($"{baseTopic}/device/#")
                .Build();

            var topicsToRemove = new ConcurrentBag<string>();

            mqttClient.ApplicationMessageReceivedAsync += async (e) =>
            {
                var topic = e.ApplicationMessage.Topic;
                if (topic.StartsWith($"{baseTopic}/device/"))
                {
                    var parts = topic.Substring($"{baseTopic}/device/".Length).Split('/');
                    if (parts.Length > 0 && devicesToRemove.Contains(parts[0]))
                    {
                        topicsToRemove.Add(topic);
                    }
                }
                await Task.CompletedTask;
            };

            try
            {
                await mqttClient.SubscribeAsync(subscribeOptions);

                // Wait briefly for all retained messages
                await Task.Delay(2000);

                await mqttClient.UnsubscribeAsync($"{baseTopic}/device/#");

                // Publish empty retained messages to remove the topics
                foreach (var topic in topicsToRemove.Distinct())
                {
                    try
                    {
                        await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                            .WithTopic(topic)
                            .WithPayload(Array.Empty<byte>())
                            .WithRetainFlag(true)
                            .Build());

                        _logger.LogDebug("Cleared MQTT topic: {Topic}", topic);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to clear MQTT topic: {Topic}", topic);
                    }
                }

                _logger.LogInformation("Cleaned up {TopicCount} topics for {DeviceCount} stale devices",
                    topicsToRemove.Count, devicesToRemove.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during MQTT cleanup");
            }
        }

        /// <summary>
        /// Creates a temporary MQTT client for reading operations
        /// </summary>
        private async Task<IMqttClient> CreateTemporaryClientAsync()
        {
            var mqttFactory = new MqttClientFactory();
            var mqttClient = mqttFactory.CreateMqttClient();

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithClientId($"HubitatReader_{Guid.NewGuid()}")
                .WithTcpServer(
                    _configuration["MQTT:Server"],
                    _configuration.GetValue<int>("MQTT:Port", 1883))
                .WithCleanSession();

            if (!string.IsNullOrEmpty(_configuration["MQTT:Username"]) &&
                !string.IsNullOrEmpty(_configuration["MQTT:Password"]))
            {
                optionsBuilder = optionsBuilder.WithCredentials(
                    _configuration["MQTT:Username"],
                    _configuration["MQTT:Password"]);
            }

            try
            {
                await mqttClient.ConnectAsync(optionsBuilder.Build());
                _logger.LogDebug("Connected temporary MQTT client for reading");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect temporary MQTT client");
            }

            return mqttClient;
        }
    }

    /// <summary>
    /// Result from reading devices from MQTT
    /// </summary>
    public class MqttDeviceReadResult
    {
        /// <summary>
        /// Dictionary of device ID to Device object for devices that were successfully read
        /// </summary>
        public required Dictionary<string, Device?> Devices { get; init; }

        /// <summary>
        /// All device IDs discovered in MQTT (including those that might not have been fully deserialized)
        /// </summary>
        public required HashSet<string> AllDiscoveredDeviceIds { get; init; }
    }
}
