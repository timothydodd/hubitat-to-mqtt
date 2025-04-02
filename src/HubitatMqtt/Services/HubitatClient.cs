using System.Text.Json;
using System.Text.Json.Serialization;
using HubitatMqtt.Common;


public class HubitatClient
{
    private readonly HttpClient _client;
    private readonly HubitatApiSettings _settings;

    public HubitatClient(HttpClient client, HubitatApiSettings omdbSettings)
    {
        _client = client;
        _settings = omdbSettings;
    }

    public async Task<List<Device>?> GetAll()
    {
        //encode title
        var url = $"{_settings.BaseUrl}/apps/api/{_settings.DeviceId}/devices/all?access_token={_settings.AccessToken}";

        var response = await _client.GetAsync(url);
        try
        {
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var d = JsonSerializer.Deserialize<List<Device>>(content, Constants.JsonOptions);
            return d;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);

        }
        return null;
    }
    public async Task<Device?> Get(string deviceId)
    {
        //encode title
        var url = $"{_settings.BaseUrl}/apps/api/{_settings.DeviceId}/devices/{deviceId}?access_token={_settings.AccessToken}";

        var response = await _client.GetAsync(url);
        try
        {
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var device = JsonSerializer.Deserialize<Device>(content, Constants.JsonOptions);
            return device;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);

        }
        return null;
    }
    public async Task SendCommand(string deviceId, string command, string? value)
    {

        var v = string.IsNullOrWhiteSpace(value) ? "" : $"/{value}";
        //encode title
        var url = $"{_settings.BaseUrl}/apps/api/{_settings.DeviceId}/devices/{deviceId}/{command}{v}?access_token={_settings.AccessToken}";

        var response = await _client.GetAsync(url);
        try
        {
            response.EnsureSuccessStatusCode();
            return;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);

        }
        return;
    }

}


public class HubitatApiSettings
{
    public required string AccessToken { get; set; }
    public required string DeviceId { get; set; }
    public required string BaseUrl { get; set; }
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
    public List<object>? Capabilities { get; set; }
    [JsonConverter(typeof(AttributesConverter))]
    public Dictionary<string, object?>? Attributes { get; set; }
    public List<object>? Commands { get; set; }

}



