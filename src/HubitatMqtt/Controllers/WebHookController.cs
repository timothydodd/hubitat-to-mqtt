using System.Text.Json;
using HubitatMqtt.Services;
using HubitatToMqtt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MQTTnet;

[AllowAnonymous]
[ApiController]
[Route("api/hook/device")]
public class WebhookController : ControllerBase
{
    private readonly ILogger<WebhookController> _logger;
    private readonly IMqttClient _mqttClient;
    private readonly IConfiguration _configuration;
    private readonly HubitatClient _hubitatClient;
    private readonly MqttPublishService _mqttPublishService;
    private readonly DeviceCache _deviceCache;

    public WebhookController(
        ILogger<WebhookController> logger,
        IMqttClient mqttClient,
        IConfiguration configuration,
        HubitatClient hubitatClient,
        MqttPublishService mqttPublishService,
        DeviceCache deviceCache)
    {
        _logger = logger;
        _mqttClient = mqttClient;
        _configuration = configuration;
        _hubitatClient = hubitatClient;
        _mqttPublishService = mqttPublishService;
        _deviceCache = deviceCache;
    }

    [HttpPost("event")]
    public async Task<IActionResult> PostEventAsync([FromBody] DeviceEvent data)
    {
        if (data.Content == null || string.IsNullOrEmpty(data.Content.DeviceId) || string.IsNullOrEmpty(data.Content.Name))
        {
            _logger.LogWarning("Received incomplete device event data");
            return Ok(new { message = "Received successfully" });
        }

        try
        {
            var deviceId = data.Content.DeviceId;
            var eventName = data.Content.Name;
            var eventValue = data.Content.Value;

            _logger.LogInformation("Processing device event: Device {DeviceId}, Event {EventName}, Value {Value}",
                deviceId, eventName, eventValue);

            Device? existingDevice = _deviceCache.GetDevice(deviceId);
            // Determine if we should get the full device data
            bool shouldRefreshFull = ShouldRefreshFullDevice(existingDevice, deviceId, eventName);

            if (shouldRefreshFull)
            {
                // Get full device details using the client
                _logger.LogDebug("Performing full device refresh for {DeviceId} due to event {EventName}", deviceId, eventName);
                var device = await _hubitatClient.Get(deviceId);

                if (device != null)
                {
                    // Publish the full device data
                    await _mqttPublishService.PublishDeviceToMqttAsync(device);
                    _logger.LogInformation("Published full device data for {DeviceId}", deviceId);
                }
                else
                {
                    _logger.LogWarning("Failed to retrieve full device data for {DeviceId}", deviceId);
                    // Fall back to event-only update
                    await PublishEventToMqttAsync(data);
                }
            }
            else
            {
                // Just update the specific attribute
                _logger.LogDebug("Performing attribute-only update for {DeviceId}.{EventName}", deviceId, eventName);

                // We still need some device info to construct the topic
                // Try to use cached device data first
                string? deviceName = null;

                // Check if we already have this device in our cache


                // If we don't have the device in our cache, check if we have display name in the event
                if (existingDevice == null)
                {
                    if (!string.IsNullOrEmpty(data.Content.DisplayName))
                    {
                        deviceName = data.Content.DisplayName;
                    }
                    else
                    {
                        // If we don't have a display name or device in cache, we need to fetch device info
                        existingDevice = await _hubitatClient.Get(deviceId);
                        if (existingDevice != null)
                        {
                            // Update the device cache
                            _deviceCache.AddDevice(existingDevice);

                            deviceName = !string.IsNullOrEmpty(existingDevice.Label)
                                ? existingDevice.Label
                                : (!string.IsNullOrEmpty(existingDevice.Name)
                                    ? existingDevice.Name
                                    : $"device_{deviceId}");


                        }
                        else
                        {
                            deviceName = $"device_{deviceId}";
                        }
                    }
                }
                else
                {
                    // Use the device name from cache
                    deviceName = !string.IsNullOrEmpty(existingDevice.Label)
                        ? existingDevice.Label
                        : (!string.IsNullOrEmpty(existingDevice.Name)
                            ? existingDevice.Name
                            : $"device_{deviceId}");
                }

                // Update the specific attribute
                await _mqttPublishService.PublishAttributeToMqttAsync(deviceId, deviceName, eventName, eventValue ?? string.Empty);

                // If we had to fetch the device anyway, might as well publish the full data
                if (existingDevice != null)
                {
                    // Update the attribute in the device object first
                    if (existingDevice.Attributes != null && eventName != null && eventValue != null)
                    {
                        existingDevice.Attributes[eventName] = eventValue;
                    }

                    // Publish the complete device data
                    await _mqttPublishService.PublishDeviceToMqttAsync(existingDevice);
                }
            }

            _logger.LogInformation("Successfully published event to MQTT: Device {DeviceId}, Event {EventName}",
                deviceId, eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process webhook event");
        }

        // Return a 200 OK response
        return Ok(new { message = "Received successfully" });
    }

    private bool ShouldRefreshFullDevice(Device? device, string deviceId, string eventName)
    {
        if (device == null)
        {
            return true;
        }
        // Get configurable list of events that should trigger a full refresh
        var refreshEventsConfig = _configuration["Hubitat:FullRefreshEvents"] ?? "mode,hsm,alarm";
        var refreshEvents = refreshEventsConfig.Split(',').Select(e => e.Trim().ToLower()).ToArray();

        // Get configurable list of device types that should always be fully refreshed
        var refreshDeviceTypesConfig = _configuration["Hubitat:FullRefreshDeviceTypes"] ?? "thermostat,lock,security";
        var alwaysRefreshDeviceTypes = refreshDeviceTypesConfig.Split(',').Select(t => t.Trim().ToLower()).ToArray();

        // Check if this is a special event type that requires full refresh
        if (refreshEvents.Contains(eventName.ToLower()))
        {
            return true;
        }

        // Check if this device type always requires full refresh
        if (alwaysRefreshDeviceTypes.Contains(device.Type?.ToLower()))
        {
            return true;
        }

        return false;
    }



    private async Task PublishEventToMqttAsync(DeviceEvent deviceEvent)
    {
        if (!_mqttClient.IsConnected)
        {
            return;
        }

        var content = deviceEvent.Content;
        if (content == null)
            return;

        var baseTopic = _configuration["MQTT:BaseTopic"] ?? "hubitat";
        var deviceTopic = $"{baseTopic}/device/{content.DeviceId}/events";
        var attributeTopic = $"{baseTopic}/device/{content.DeviceId}/{_mqttPublishService.SanitizeTopicName(content.Name)}";

        // Publish the full event
        var eventMessage = new MqttApplicationMessageBuilder()
            .WithTopic(deviceTopic)
            .WithPayload(JsonSerializer.Serialize(deviceEvent))
            .WithRetainFlag(true)
            .Build();

        await _mqttClient.PublishAsync(eventMessage);

        // Publish just the value
        if (content.Value != null)
        {
            var valueMessage = new MqttApplicationMessageBuilder()
                .WithTopic(attributeTopic)
                .WithPayload(content.Value)
                .WithRetainFlag(true)
                .Build();

            await _mqttClient.PublishAsync(valueMessage);
        }
    }
}

public class DeviceEvent
{
    public EventContent? Content { get; set; }
}

public class EventContent
{
    public required string DeviceId { get; set; }
    public required string Name { get; set; }
    public string? Value { get; set; }
    public string? DisplayName { get; set; }
    public string? DisplayText { get; set; }
    public string? Unit { get; set; }
    public string? Data { get; set; }
}
