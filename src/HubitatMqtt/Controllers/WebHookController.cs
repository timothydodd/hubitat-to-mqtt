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
    private readonly SyncCoordinator _syncCoordinator;

    readonly string[] _refreshEvents;
    readonly string[] _alwaysRefreshDeviceTypes;

    public WebhookController(
        ILogger<WebhookController> logger,
        IMqttClient mqttClient,
        IConfiguration configuration,
        HubitatClient hubitatClient,
        MqttPublishService mqttPublishService,
        DeviceCache deviceCache,
        SyncCoordinator syncCoordinator)
    {
        _logger = logger;
        _mqttClient = mqttClient;
        _configuration = configuration;
        _hubitatClient = hubitatClient;
        _mqttPublishService = mqttPublishService;
        _deviceCache = deviceCache;
        _syncCoordinator = syncCoordinator;
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

        var deviceId = data.Content.DeviceId;
        var eventName = data.Content.Name;
        var eventValue = data.Content.Value;

        _logger.LogDebug("Processing device event: Device {DeviceId}, Event {EventName}, Value {Value}",
            deviceId, eventName, eventValue);

        try
        {
            // Try to acquire webhook lock to coordinate with full sync
            using var webhookLock = await _syncCoordinator.AcquireWebhookLockAsync(deviceId);

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
        catch (InvalidOperationException ex) when (ex.Message.Contains("Full sync in progress"))
        {
            _logger.LogDebug("Webhook update for device {DeviceId} deferred due to full sync in progress", deviceId);
            // This is expected during full sync - the webhook will be processed after sync completes
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Timeout acquiring webhook lock for device {DeviceId}", deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process webhook event for device {DeviceId}", deviceId);
        }

        // Return a 200 OK response
        return Ok(new { message = "Received successfully" });
    }

    // Extract methods to reduce complexity
    private async Task HandleFullDeviceRefresh(string deviceId, DeviceEvent data)
    {
        _logger.LogDebug("Performing full device refresh for {DeviceId}", deviceId);
        
        try
        {
            // Get the existing device from cache for comparison
            var existingDevice = _deviceCache.GetDevice(deviceId);
            var device = await _hubitatClient.Get(deviceId);

            if (device != null)
            {
                // Check if this is a device type that always requires full refresh and log changes
                if (existingDevice != null && _alwaysRefreshDeviceTypes.Contains(device.Type?.ToLower()))
                {
                    LogDeviceChanges(existingDevice, device, data);
                }

                _deviceCache.AddDevice(device);
                await _mqttPublishService.PublishDeviceToMqttAsync(device);
                _logger.LogDebug("Published full device data for {DeviceId}", deviceId);
            }
            else
            {
                _logger.LogWarning("Device {DeviceId} not found in Hubitat API", deviceId);
                // Fall back to event-only update
                await PublishEventToMqttAsync(data);
            }
        }
        catch (HubitatApiException ex)
        {
            _logger.LogError(ex, "Hubitat API error when refreshing device {DeviceId}. Falling back to event-only update", deviceId);
            await PublishEventToMqttAsync(data);
        }
        catch (MqttPublishException ex)
        {
            _logger.LogError(ex, "MQTT publish error when refreshing device {DeviceId}", deviceId);
            // Don't fall back for MQTT errors - they should be retried
            throw;
        }
    }

    private async Task HandleAttributeUpdate(string deviceId, string eventName, string? eventValue, Device? existingDevice, DeviceEvent data)
    {
        _logger.LogDebug("Performing attribute-only update for {DeviceId}.{EventName}", deviceId, eventName);

        try
        {
            // Update the specific attribute
            await _mqttPublishService.PublishAttributeToMqttAsync(deviceId, eventName, eventValue ?? string.Empty);

            // Update the cached device attribute if it exists
            if (eventName != null && eventValue != null)
            {
                var wasUpdated = _deviceCache.UpdateDeviceAttribute(deviceId, eventName, eventValue);
                
                // If we successfully updated the cache, publish the updated device
                if (wasUpdated)
                {
                    var updatedDevice = _deviceCache.GetDevice(deviceId);
                    if (updatedDevice != null)
                    {
                        await _mqttPublishService.PublishDeviceToMqttAsync(updatedDevice, false);
                    }
                }
            }
        }
        catch (MqttPublishException ex)
        {
            _logger.LogError(ex, "MQTT publish error when updating attribute {EventName} for device {DeviceId}", eventName, deviceId);
            // For attribute updates, we can fall back to the raw event
            await PublishEventToMqttAsync(data);
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

    private void LogDeviceChanges(Device oldDevice, Device newDevice, DeviceEvent triggerEvent)
    {
        var deviceId = newDevice.Id ?? "unknown";
        var deviceName = newDevice.Label ?? newDevice.Name ?? "unknown";
        var deviceType = newDevice.Type ?? "unknown";
        var triggerAttribute = triggerEvent.Content?.Name ?? "unknown";
        var triggerValue = triggerEvent.Content?.Value ?? "unknown";

        _logger.LogDebug("AlwaysRefreshDevice Update - Device: {DeviceId} ({DeviceName}) Type: {DeviceType} | Trigger: {TriggerAttribute}={TriggerValue}",
            deviceId, deviceName, deviceType, triggerAttribute, triggerValue);

        // Compare and log attribute changes
        var changedAttributes = new List<string>();
        
        // Handle case where old device has no attributes
        var oldAttributes = oldDevice.Attributes ?? new Dictionary<string, object?>();
        var newAttributes = newDevice.Attributes ?? new Dictionary<string, object?>();

        // Check for changed or new attributes
        foreach (var newAttr in newAttributes)
        {
            var attributeName = newAttr.Key;
            var newValue = newAttr.Value?.ToString() ?? "null";
            
            if (oldAttributes.TryGetValue(attributeName, out var oldValueObj))
            {
                var oldValue = oldValueObj?.ToString() ?? "null";
                if (oldValue != newValue)
                {
                    _logger.LogDebug("AlwaysRefreshDevice Change - {DeviceId} ({DeviceName}) | {AttributeName}: '{OldValue}' → '{NewValue}'",
                        deviceId, deviceName, attributeName, oldValue, newValue);
                    changedAttributes.Add(attributeName);
                }
            }
            else
            {
                // New attribute
                _logger.LogDebug("AlwaysRefreshDevice New - {DeviceId} ({DeviceName}) | {AttributeName}: '{NewValue}' (new attribute)",
                    deviceId, deviceName, attributeName, newValue);
                changedAttributes.Add(attributeName);
            }
        }

        // Check for removed attributes
        foreach (var oldAttr in oldAttributes)
        {
            var attributeName = oldAttr.Key;
            if (!newAttributes.ContainsKey(attributeName))
            {
                var oldValue = oldAttr.Value?.ToString() ?? "null";
                _logger.LogDebug("AlwaysRefreshDevice Removed - {DeviceId} ({DeviceName}) | {AttributeName}: '{OldValue}' (attribute removed)",
                    deviceId, deviceName, attributeName, oldValue);
                changedAttributes.Add(attributeName);
            }
        }

        if (changedAttributes.Count == 0)
        {
            _logger.LogDebug("AlwaysRefreshDevice NoChanges - {DeviceId} ({DeviceName}) | No attribute changes detected despite refresh trigger",
                deviceId, deviceName);
        }
        else
        {
            _logger.LogDebug("AlwaysRefreshDevice Summary - {DeviceId} ({DeviceName}) | {ChangeCount} attributes changed: {ChangedAttributes}",
                deviceId, deviceName, changedAttributes.Count, string.Join(", ", changedAttributes));
        }
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
