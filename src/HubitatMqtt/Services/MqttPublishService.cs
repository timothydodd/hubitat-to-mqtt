﻿using System.Text.Json;
using MQTTnet;

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

        public MqttPublishService(
            ILogger<MqttPublishService> logger,
            IMqttClient mqttClient,
            IConfiguration configuration)
        {
            _logger = logger;
            _mqttClient = mqttClient;
            _configuration = configuration;
        }
        /// <summary>
        /// Publishes a full device to MQTT topics
        /// </summary>
        public async Task PublishDeviceToMqttAsync(Device device, bool publishAttributes = true)
        {
            if (!_mqttClient.IsConnected)
            {
                _logger.LogWarning("MQTT client not connected. Skipping publish");
                return;
            }

            if (device == null || string.IsNullOrEmpty(device.Id))
            {
                _logger.LogWarning("Invalid device data. Skipping publish");
                return;
            }

            try
            {


                var baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";



                // Also publish device by ID for direct control
                var idTopic = $"{baseTopic}/device/{device.Id}";
                var idMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(idTopic)
                    .WithPayload(JsonSerializer.Serialize(device, Constants.JsonOptions))
                    .WithRetainFlag(true)
                    .Build();

                await _mqttClient.PublishAsync(idMessage);

                // Also publish individual attributes for easy access
                if (device.Attributes != null && publishAttributes)
                {
                    foreach (var attr in device.Attributes)
                    {
                        string attrName = attr.Key;

                        var attrValue = attr.Value;

                        if (attrValue == null)
                        {
                            continue;
                        }
                        string valueString = "";
                        var attrType = attrValue.GetType();
                        if (attrType == typeof(string) || attrType.IsValueType)
                        {
                            valueString = attrValue?.ToString() ?? "";
                        }
                        else
                        {
                            valueString = JsonSerializer.Serialize(attrValue, Constants.JsonOptions);
                        }


                        await PublishAttributeToMqttAsync(device.Id, attrName, valueString);
                    }
                }

                _logger.LogDebug("Published device {DeviceId} ({DeviceName}) to MQTT", device.Id, device.Label);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing device {DeviceId} to MQTT", device.Id);
            }
        }

        /// <summary>
        /// Publishes a single device attribute to MQTT
        /// </summary>
        public async Task PublishAttributeToMqttAsync(string deviceId, string attributeName, string attributeValue)
        {
            if (!_mqttClient.IsConnected)
            {
                return;
            }

            try
            {
                var baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";


                // Also publish to ID-based topic
                var idAttributeTopic = $"{baseTopic}/device/{deviceId}/{SanitizeTopicName(attributeName)}";
                var idMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(idAttributeTopic)
                    .WithPayload(attributeValue)
                    .WithRetainFlag(true)
                    .Build();

                await _mqttClient.PublishAsync(idMessage);

                _logger.LogDebug("Published attribute {Attribute}={Value} for device {DeviceId}",
                    attributeName, attributeValue, deviceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing attribute {Attribute} for device {DeviceId}",
                    attributeName, deviceId);
            }
        }

        /// <summary>
        /// Sanitizes a name for use in MQTT topics
        /// </summary>
        public string SanitizeTopicName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "unknown";
            }

            // Replace spaces, special chars, etc. to make a valid MQTT topic
            return name.Replace(" ", "_")
                .Replace("/", "_")
                .Replace("+", "_")
                .Replace("#", "_");
        }
    }
}
