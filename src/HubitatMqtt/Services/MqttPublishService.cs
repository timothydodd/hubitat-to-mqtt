using System.Text.Json;
using MQTTnet;
using MQTTnet.Exceptions;

namespace HubitatToMqtt
{
    /// <summary>
    /// Shared service for publishing device data to MQTT
    /// </summary>
    public class MqttPublishService
    {
        private readonly ILogger<MqttPublishService> _logger;
        private readonly IMqttClient _mqttClient;
        private readonly IConfiguration _configuration;
        private readonly int _maxRetryAttempts;
        private readonly TimeSpan _retryDelay;
        private static readonly char[] InvalidTopicChars = [' ', '/', '+', '#', '\t', '\n', '\r'];

        public MqttPublishService(
            ILogger<MqttPublishService> logger,
            IMqttClient mqttClient,
            IConfiguration configuration)
        {
            _logger = logger;
            _mqttClient = mqttClient;
            _configuration = configuration;
            _maxRetryAttempts = configuration.GetValue("MQTT:MaxRetryAttempts", 3);
            _retryDelay = TimeSpan.FromMilliseconds(configuration.GetValue("MQTT:RetryDelayMs", 1000));
        }
        /// <summary>
        /// Publishes multiple devices to MQTT in batches for improved performance
        /// </summary>
        public async Task PublishDevicesBatchAsync(IEnumerable<Device> devices, int batchSize = 50)
        {
            if (!await EnsureConnectedAsync())
            {
                _logger.LogWarning("MQTT client not connected. Skipping batch publish");
                return;
            }

            var deviceList = devices.ToList();
            var totalDevices = deviceList.Count;
            var publishedCount = 0;
            var failedCount = 0;

            // Process devices in batches
            for (int i = 0; i < totalDevices; i += batchSize)
            {
                var batch = deviceList.Skip(i).Take(batchSize);
                var messages = new List<MqttApplicationMessage>();

                foreach (var device in batch)
                {
                    if (device == null || string.IsNullOrEmpty(device.Id))
                    {
                        failedCount++;
                        continue;
                    }

                    try
                    {
                        // Build device message
                        var baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";
                        var idTopic = $"{baseTopic}/device/{device.Id}";
                        var deviceJson = JsonSerializer.Serialize(device, Constants.JsonOptions);

                        messages.Add(new MqttApplicationMessageBuilder()
                            .WithTopic(idTopic)
                            .WithPayload(deviceJson)
                            .WithRetainFlag(true)
                            .Build());

                        // Build attribute messages
                        if (device.Attributes != null)
                        {
                            foreach (var attr in device.Attributes)
                            {
                                if (attr.Value == null)
                                    continue;

                                string valueString;
                                var attrType = attr.Value.GetType();
                                if (attrType == typeof(string) || attrType.IsValueType)
                                {
                                    valueString = attr.Value.ToString() ?? "";
                                }
                                else
                                {
                                    valueString = JsonSerializer.Serialize(attr.Value, Constants.JsonOptions);
                                }

                                var idAttributeTopic = $"{baseTopic}/device/{device.Id}/{SanitizeTopicName(attr.Key)}";
                                messages.Add(new MqttApplicationMessageBuilder()
                                    .WithTopic(idAttributeTopic)
                                    .WithPayload(valueString)
                                    .WithRetainFlag(true)
                                    .Build());
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to build messages for device {DeviceId}", device.Id);
                        failedCount++;
                    }
                }

                // Publish batch
                if (messages.Count > 0)
                {
                    try
                    {
                        foreach (var message in messages)
                        {
                            await PublishWithRetryAsync(message, $"batch message to {message.Topic}");
                        }
                        publishedCount += messages.Count;
                        _logger.LogTrace("Published batch of {MessageCount} messages", messages.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to publish batch");
                        failedCount += messages.Count;
                    }
                }
            }

            _logger.LogDebug("Batch publish completed: {PublishedCount} messages published, {FailedCount} failed",
                publishedCount, failedCount);
        }

        /// <summary>
        /// Publishes a full device to MQTT topics
        /// </summary>
        public async Task PublishDeviceToMqttAsync(Device device, bool publishAttributes = true)
        {
            if (device == null || string.IsNullOrEmpty(device.Id))
            {
                _logger.LogWarning("Invalid device data. Skipping publish");
                return;
            }

            if (!await EnsureConnectedAsync())
            {
                _logger.LogWarning("MQTT client not connected and unable to reconnect. Skipping device {DeviceId} publish", device.Id);
                return;
            }

            try
            {
                var baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";

                // Publish device by ID for direct control
                var idTopic = $"{baseTopic}/device/{device.Id}";
                var deviceJson = JsonSerializer.Serialize(device, Constants.JsonOptions);
                var idMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(idTopic)
                    .WithPayload(deviceJson)
                    .WithRetainFlag(true)
                    .Build();

                await PublishWithRetryAsync(idMessage, $"device {device.Id} full data");

                // Publish individual attributes for easy access
                if (device.Attributes != null && publishAttributes)
                {
                    foreach (var attr in device.Attributes)
                    {
                        if (attr.Value == null)
                            continue;

                        string valueString;
                        var attrType = attr.Value.GetType();
                        if (attrType == typeof(string) || attrType.IsValueType)
                        {
                            valueString = attr.Value.ToString() ?? "";
                        }
                        else
                        {
                            valueString = JsonSerializer.Serialize(attr.Value, Constants.JsonOptions);
                        }

                        await PublishAttributeToMqttAsync(device.Id, attr.Key, valueString);
                    }
                }

                _logger.LogDebug("Successfully published device {DeviceId} ({DeviceName}) to MQTT", device.Id, device.Label);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish device {DeviceId} to MQTT", device.Id);
                throw new MqttPublishException($"Failed to publish device {device.Id} to MQTT", ex);
            }
        }

        /// <summary>
        /// Publishes a single device attribute to MQTT
        /// </summary>
        public async Task PublishAttributeToMqttAsync(string deviceId, string attributeName, string attributeValue)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(attributeName))
            {
                _logger.LogWarning("Invalid attribute data: DeviceId={DeviceId}, AttributeName={AttributeName}", deviceId, attributeName);
                return;
            }

            if (!await EnsureConnectedAsync())
            {
                _logger.LogWarning("MQTT client not connected and unable to reconnect. Skipping attribute {Attribute} for device {DeviceId}",
                    attributeName, deviceId);
                return;
            }

            try
            {
                var baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";
                var idAttributeTopic = $"{baseTopic}/device/{deviceId}/{SanitizeTopicName(attributeName)}";
                var idMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(idAttributeTopic)
                    .WithPayload(attributeValue ?? "")
                    .WithRetainFlag(true)
                    .Build();

                await PublishWithRetryAsync(idMessage, $"attribute {attributeName} for device {deviceId}");

                _logger.LogDebug("Successfully published attribute {Attribute}={Value} for device {DeviceId}",
                    attributeName, attributeValue, deviceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish attribute {Attribute} for device {DeviceId}",
                    attributeName, deviceId);
                throw new MqttPublishException($"Failed to publish attribute {attributeName} for device {deviceId}", ex);
            }
        }

        /// <summary>
        /// Ensures MQTT client is connected, attempting reconnection if needed
        /// </summary>
        private async Task<bool> EnsureConnectedAsync()
        {
            if (_mqttClient.IsConnected)
            {
                return true;
            }

            _logger.LogWarning("MQTT client disconnected. Connection state: {State}", _mqttClient.IsConnected);

            // Try to reconnect if disconnected
            try
            {
                if (!_mqttClient.IsConnected)
                {
                    _logger.LogInformation("Attempting to reconnect MQTT client...");
                    // Note: The actual reconnection should be handled by the MqttBuilder's reconnection logic
                    // We just wait a short time to see if automatic reconnection works
                    await Task.Delay(1000);
                    return _mqttClient.IsConnected;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check MQTT connection status");
            }

            return _mqttClient.IsConnected;
        }

        /// <summary>
        /// Publishes an MQTT message with retry logic
        /// </summary>
        private async Task PublishWithRetryAsync(MqttApplicationMessage message, string description)
        {
            Exception? lastException = null;

            for (int attempt = 1; attempt <= _maxRetryAttempts; attempt++)
            {
                try
                {
                    if (!_mqttClient.IsConnected)
                    {
                        _logger.LogWarning("MQTT client disconnected during publish attempt {Attempt} for {Description}", attempt, description);
                        if (attempt < _maxRetryAttempts)
                        {
                            await Task.Delay(_retryDelay);
                            continue;
                        }
                        throw new MqttCommunicationException("MQTT client is not connected");
                    }

                    await _mqttClient.PublishAsync(message);

                    if (attempt > 1)
                    {
                        _logger.LogInformation("Successfully published {Description} on attempt {Attempt}", description, attempt);
                    }
                    return;
                }
                catch (MqttCommunicationException ex) when (attempt < _maxRetryAttempts)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "MQTT communication failed for {Description} on attempt {Attempt}. Retrying in {Delay}ms...",
                        description, attempt, _retryDelay.TotalMilliseconds);
                    await Task.Delay(_retryDelay);
                }
                catch (Exception ex) when (attempt < _maxRetryAttempts && (ex is TaskCanceledException || ex is TimeoutException))
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "Timeout/cancellation for {Description} on attempt {Attempt}. Retrying in {Delay}ms...",
                        description, attempt, _retryDelay.TotalMilliseconds);
                    await Task.Delay(_retryDelay);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish {Description} on attempt {Attempt}", description, attempt);
                    throw;
                }
            }

            throw new MqttPublishException($"Failed to publish {description} after {_maxRetryAttempts} attempts", lastException);
        }

        /// <summary>
        /// Sanitizes a name for use in MQTT topics (optimized for performance)
        /// </summary>
        public string SanitizeTopicName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "unknown";
            }

            // Check if sanitization is needed
            if (name.IndexOfAny(InvalidTopicChars) == -1)
            {
                return name;
            }

            // Efficiently replace invalid characters using string.Create
            return string.Create(name.Length, name, static (span, original) =>
            {
                original.AsSpan().CopyTo(span);
                for (int i = 0; i < span.Length; i++)
                {
                    if (Array.IndexOf(InvalidTopicChars, span[i]) >= 0)
                    {
                        span[i] = '_';
                    }
                }
            });
        }
    }

    public class MqttPublishException : Exception
    {
        public MqttPublishException(string message) : base(message) { }
        public MqttPublishException(string message, Exception? innerException) : base(message, innerException) { }
    }

}
