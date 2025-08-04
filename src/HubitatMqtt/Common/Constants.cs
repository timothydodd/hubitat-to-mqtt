using System.Text.Json;


public class Constants
{
    public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions() 
    { 
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false // Optimize for size
    };
}
