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

    readonly string[] _refreshEvents;
    readonly string[] _alwaysRefreshDeviceTypes;

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
        // Get configurable list of events that should trigger a full refresh
        var refreshEventsConfig = _configuration["Hubitat:FullRefreshEvents"] ?? "mode,hsm,alarm";
        _refreshEvents = refreshEventsConfig.Split(',').Select(e => e.Trim().ToLower()).ToArray();

        // Get configurable list of device types that should always be fully refreshed
        var refreshDeviceTypesConfig = _configuration["Hubitat:FullRefreshDeviceTypes"] ?? "thermostat,lock,security";
        _alwaysRefreshDeviceTypes = refreshDeviceTypesConfig.Split(',').Select(t => t.Trim().ToLower()).ToArray();
    }

    [HttpPost("event")]
    public async Task<IActionResult> PostEventAsync([FromBody] DeviceEvent data)
    {
        // Quick validation and early return
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

            _logger.LogDebug("Processing device event: Device {DeviceId}, Event {EventName}, Value {Value}",
                deviceId, eventName, eventValue);

            // Get device from cache
            Device? existingDevice = _deviceCache.GetDevice(deviceId);

            // Determine if we need full refresh
            if (ShouldRefreshFullDevice(existingDevice, deviceId, eventName))
            {
                await HandleFullDeviceRefresh(deviceId, data);
            }
            else
            {
                await HandleAttributeUpdate(deviceId, eventName, eventValue, existingDevice, data);
            }

            _logger.LogDebug("Successfully published event to MQTT: Device {DeviceId}, Event {EventName}",
                deviceId, eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process webhook event");
        }

        // Return a 200 OK response
        return Ok(new { message = "Received successfully" });
    }

    // Extract methods to reduce complexity
    private async Task HandleFullDeviceRefresh(string deviceId, DeviceEvent data)
    {
        _logger.LogDebug("Performing full device refresh for {DeviceId}", deviceId);
        var device = await _hubitatClient.Get(deviceId);

        if (device != null)
        {
            _deviceCache.AddDevice(device);
            await _mqttPublishService.PublishDeviceToMqttAsync(device);
            _logger.LogDebug("Published full device data for {DeviceId}", deviceId);
        }
        else
        {
            _logger.LogWarning("Failed to retrieve full device data for {DeviceId}", deviceId);
            // Fall back to event-only update
            await PublishEventToMqttAsync(data);
        }
    }

    private async Task HandleAttributeUpdate(string deviceId, string eventName, string? eventValue, Device? existingDevice, DeviceEvent data)
    {
        _logger.LogDebug("Performing attribute-only update for {DeviceId}.{EventName}", deviceId, eventName);

        // Update the specific attribute
        await _mqttPublishService.PublishAttributeToMqttAsync(deviceId, eventName, eventValue ?? string.Empty);

        // If we had to fetch a new device, update its attribute and publish full data
        if (existingDevice?.Attributes != null && eventName != null && eventValue != null)
        {
            existingDevice.Attributes[eventName] = eventValue;
            await _mqttPublishService.PublishDeviceToMqttAsync(existingDevice, false);
        }
    }



    private bool ShouldRefreshFullDevice(Device? device, string deviceId, string eventName)
    {
        if (device == null)
        {
            return true;
        }

        // Check if this is a special event type that requires full refresh
        if (_refreshEvents.Contains(eventName?.ToLower()))
        {
            return true;
        }

        // Check if this device type always requires full refresh
        if (_alwaysRefreshDeviceTypes.Contains(device.Type?.ToLower()))
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
