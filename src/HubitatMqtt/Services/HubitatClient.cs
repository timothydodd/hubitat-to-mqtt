using System.Text.Json;
using System.Text.Json.Serialization;
using HubitatMqtt.Common;
using Microsoft.Extensions.Logging;


public class HubitatClient
{
    private readonly HttpClient _client;
    private readonly HubitatApiSettings _settings;
    private readonly ILogger<HubitatClient> _logger;

    public HubitatClient(HttpClient client, HubitatApiSettings settings, ILogger<HubitatClient> logger)
    {
        _client = client;
        _settings = settings;
        _logger = logger;
    }

    public async Task<List<Device>?> GetAll()
    {
        var url = $"{_settings.BaseUrl}/apps/api/{_settings.DeviceId}/devices/all?access_token={_settings.AccessToken}";
        
        try
        {
            _logger.LogDebug("Fetching all devices from Hubitat API: {Url}", url);
            using var response = await _client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            
            var content = await response.Content.ReadAsStringAsync();
            var devices = JsonSerializer.Deserialize<List<Device>>(content, Constants.JsonOptions);
            
            _logger.LogInformation("Successfully retrieved {DeviceCount} devices from Hubitat", devices?.Count ?? 0);
            return devices;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request failed when fetching all devices from Hubitat. URL: {Url}", url);
            throw new HubitatApiException("Failed to fetch devices from Hubitat API", ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Request timeout when fetching all devices from Hubitat. URL: {Url}", url);
            throw new HubitatApiException("Timeout while fetching devices from Hubitat API", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize device data from Hubitat. URL: {Url}", url);
            throw new HubitatApiException("Invalid JSON response from Hubitat API", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error when fetching all devices from Hubitat. URL: {Url}", url);
            throw new HubitatApiException("Unexpected error while fetching devices from Hubitat API", ex);
        }
    }
    public async Task<Device?> Get(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            _logger.LogWarning("Device ID is null or empty");
            return null;
        }
        
        var url = $"{_settings.BaseUrl}/apps/api/{_settings.DeviceId}/devices/{deviceId}?access_token={_settings.AccessToken}";
        
        try
        {
            _logger.LogDebug("Fetching device {DeviceId} from Hubitat API: {Url}", deviceId, url);
            using var response = await _client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            
            var content = await response.Content.ReadAsStringAsync();
            var device = JsonSerializer.Deserialize<Device>(content, Constants.JsonOptions);
            
            _logger.LogDebug("Successfully retrieved device {DeviceId} from Hubitat", deviceId);
            return device;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request failed when fetching device {DeviceId} from Hubitat. URL: {Url}", deviceId, url);
            throw new HubitatApiException($"Failed to fetch device {deviceId} from Hubitat API", ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Request timeout when fetching device {DeviceId} from Hubitat. URL: {Url}", deviceId, url);
            throw new HubitatApiException($"Timeout while fetching device {deviceId} from Hubitat API", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize device {DeviceId} data from Hubitat. URL: {Url}", deviceId, url);
            throw new HubitatApiException($"Invalid JSON response for device {deviceId} from Hubitat API", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error when fetching device {DeviceId} from Hubitat. URL: {Url}", deviceId, url);
            throw new HubitatApiException($"Unexpected error while fetching device {deviceId} from Hubitat API", ex);
        }
    }
    public async Task SendCommand(string deviceId, string command, string? value)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            _logger.LogWarning("Device ID is null or empty for command {Command}", command);
            throw new ArgumentException("Device ID cannot be null or empty", nameof(deviceId));
        }
        
        if (string.IsNullOrWhiteSpace(command))
        {
            _logger.LogWarning("Command is null or empty for device {DeviceId}", deviceId);
            throw new ArgumentException("Command cannot be null or empty", nameof(command));
        }
        
        var v = string.IsNullOrWhiteSpace(value) ? "" : $"/{value}";
        var url = $"{_settings.BaseUrl}/apps/api/{_settings.DeviceId}/devices/{deviceId}/{command}{v}?access_token={_settings.AccessToken}";
        
        try
        {
            _logger.LogDebug("Sending command {Command} to device {DeviceId} with value {Value}. URL: {Url}", command, deviceId, value, url);
            using var response = await _client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            
            _logger.LogInformation("Successfully sent command {Command} to device {DeviceId}", command, deviceId);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request failed when sending command {Command} to device {DeviceId}. URL: {Url}", command, deviceId, url);
            throw new HubitatApiException($"Failed to send command {command} to device {deviceId}", ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Request timeout when sending command {Command} to device {DeviceId}. URL: {Url}", command, deviceId, url);
            throw new HubitatApiException($"Timeout while sending command {command} to device {deviceId}", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error when sending command {Command} to device {DeviceId}. URL: {Url}", command, deviceId, url);
            throw new HubitatApiException($"Unexpected error while sending command {command} to device {deviceId}", ex);
        }
    }

}


public class HubitatApiSettings
{
    public required string AccessToken { get; set; }
    public required string DeviceId { get; set; }
    public required string BaseUrl { get; set; }
}
public class HubitatApiException : Exception
{
    public HubitatApiException(string message) : base(message) { }
    public HubitatApiException(string message, Exception innerException) : base(message, innerException) { }
}

public class Device
{
    public string? Name { get; set; }
    public string? Label { get; set; }
    public string? Type { get; set; }
    public string? Id { get; set; }
    public string? Date { get; set; }
    public string? Model { get; set; }
    public string? Manufacturer { get; set; }
    public string? Room { get; set; }
    [JsonConverter(typeof(CapabilitiesConverter))]
    public List<object>? Capabilities { get; set; }
    [JsonConverter(typeof(AttributesConverter))]
    public Dictionary<string, object?>? Attributes { get; set; }
    [JsonConverter(typeof(CommandsConverter))]
    public List<object>? Commands { get; set; }

}



