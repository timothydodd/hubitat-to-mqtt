using System.Text.Json;
using System.Text.Json.Serialization;

namespace HubitatMqtt.Common;


public class AttributesConverter : JsonConverter<Dictionary<string, object?>>
{
    public override Dictionary<string, object?>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new Dictionary<string, object?>();

        // Check if we're dealing with an array or an object
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            // Process as array of attribute objects
            reader.Read(); // Move past start array

            while (reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new JsonException("Expected start of object within attributes array");
                }

                ProcessAttributeObject(ref reader, result);
                reader.Read(); // Move to next item or end array
            }
        }
        else if (reader.TokenType == JsonTokenType.StartObject)
        {
            // Process as direct dictionary
            reader.Read(); // Move past start object

            while (reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("Expected property name");
                }

                string key = reader.GetString()!;
                reader.Read(); // Move to value

                result[key] = ReadValue(ref reader);
                reader.Read(); // Move to next property or end object
            }
        }
        else
        {
            throw new JsonException($"Unexpected token type for attributes: {reader.TokenType}");
        }

        return result;
    }

    private void ProcessAttributeObject(ref Utf8JsonReader reader, Dictionary<string, object?> result)
    {
        reader.Read(); // Move to first property

        string? attributeName = null;
        object? currentValue = null;
        bool hasCurrentValue = false;

        while (reader.TokenType != JsonTokenType.EndObject)
        {
            // Read property name
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name");
            }

            string propertyName = reader.GetString()!;
            reader.Read(); // Move to property value

            if (propertyName == "name")
            {
                attributeName = reader.GetString();
            }
            else if (propertyName == "currentValue")
            {
                currentValue = ReadValue(ref reader);
                hasCurrentValue = true;
            }
            else
            {
                // Skip other metadata properties (dataType, values, etc.)
                ReadValue(ref reader);
            }

            reader.Read(); // Move to next property name or end object
        }

        // Add to dictionary with name as key, using currentValue if available
        if (attributeName != null)
        {
            object? valueToStore = hasCurrentValue ? currentValue : null;

            if (!result.ContainsKey(attributeName))
            {
                result[attributeName] = valueToStore;
            }
            else
            {
                // Handle duplicate keys - if the same attribute appears multiple times,
                // just use the first value (or last value by replacing)
                // This handles cases where Hubitat returns duplicate attributes
                result[attributeName] = valueToStore;
            }
        }
    }

    private object? ReadValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();

            case JsonTokenType.Number:
                if (reader.TryGetInt64(out long longValue))
                {
                    return longValue;
                }
                return reader.GetDouble();

            case JsonTokenType.True:
                return true;

            case JsonTokenType.False:
                return false;

            case JsonTokenType.Null:
                return null;

            case JsonTokenType.StartArray:
                var list = new List<object?>();
                reader.Read(); // Move past start array

                while (reader.TokenType != JsonTokenType.EndArray)
                {
                    list.Add(ReadValue(ref reader));
                    reader.Read(); // Move to next item or end array
                }

                return list;

            case JsonTokenType.StartObject:
                var obj = new Dictionary<string, object?>();
                reader.Read(); // Move past start object

                while (reader.TokenType != JsonTokenType.EndObject)
                {
                    string propertyName = reader.GetString()!;
                    reader.Read(); // Move to property value
                    obj[propertyName] = ReadValue(ref reader);
                    reader.Read(); // Move to next property or end object
                }

                return obj;

            default:
                throw new JsonException($"Unexpected token type: {reader.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, object?> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach (var kvp in value)
        {
            writer.WritePropertyName(kvp.Key);
            WriteValue(writer, kvp.Value);
        }

        writer.WriteEndObject();
    }

    private void WriteValue(Utf8JsonWriter writer, object? value)
    {
        if (value == null)
        {
            writer.WriteNullValue();
        }
        else if (value is string stringValue)
        {
            writer.WriteStringValue(stringValue);
        }
        else if (value is long longValue)
        {
            writer.WriteNumberValue(longValue);
        }
        else if (value is int intValue)
        {
            writer.WriteNumberValue(intValue);
        }
        else if (value is double doubleValue)
        {
            writer.WriteNumberValue(doubleValue);
        }
        else if (value is bool boolValue)
        {
            writer.WriteBooleanValue(boolValue);
        }
        else if (value is List<object?> listValue)
        {
            writer.WriteStartArray();
            foreach (var item in listValue)
            {
                WriteValue(writer, item);
            }
            writer.WriteEndArray();
        }
        else if (value is Dictionary<string, object?> dictValue)
        {
            writer.WriteStartObject();
            foreach (var kvp in dictValue)
            {
                writer.WritePropertyName(kvp.Key);
                WriteValue(writer, kvp.Value);
            }
            writer.WriteEndObject();
        }
        else
        {
            // Default to string representation
            writer.WriteStringValue(value.ToString());
        }
    }
}

/// <summary>
/// Normalizes Capabilities array by filtering out metadata objects
/// </summary>
public class CapabilitiesConverter : JsonConverter<List<object>?>
{
    public override List<object>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected start of array for Capabilities");
        }

        var result = new List<object>();
        reader.Read(); // Move past start array

        while (reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                // Keep string capability names
                var capability = reader.GetString();
                if (capability != null)
                {
                    result.Add(capability);
                }
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                // Skip metadata objects with "attributes" property
                SkipObject(ref reader);
            }
            else
            {
                throw new JsonException($"Unexpected token in Capabilities array: {reader.TokenType}");
            }

            reader.Read(); // Move to next item or end array
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<object>? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
        {
            if (item is string stringValue)
            {
                writer.WriteStringValue(stringValue);
            }
            else
            {
                JsonSerializer.Serialize(writer, item, options);
            }
        }
        writer.WriteEndArray();
    }

    private void SkipObject(ref Utf8JsonReader reader)
    {
        reader.Read(); // Move past start object
        int depth = 1;

        while (depth > 0)
        {
            if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
            {
                depth++;
            }
            else if (reader.TokenType == JsonTokenType.EndObject || reader.TokenType == JsonTokenType.EndArray)
            {
                depth--;
            }

            if (depth > 0)
            {
                reader.Read();
            }
        }
    }
}

/// <summary>
/// Normalizes Commands array by extracting command names from objects
/// </summary>
public class CommandsConverter : JsonConverter<List<object>?>
{
    public override List<object>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected start of array for Commands");
        }

        var result = new List<object>();
        reader.Read(); // Move past start array

        while (reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                // Keep string command names
                var command = reader.GetString();
                if (command != null)
                {
                    result.Add(command);
                }
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                // Extract command name from object like {"command": "IdleBrightness"}
                var commandObj = ExtractCommandFromObject(ref reader);
                if (commandObj != null)
                {
                    result.Add(commandObj);
                }
            }
            else
            {
                throw new JsonException($"Unexpected token in Commands array: {reader.TokenType}");
            }

            reader.Read(); // Move to next item or end array
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<object>? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
        {
            if (item is string stringValue)
            {
                writer.WriteStringValue(stringValue);
            }
            else
            {
                JsonSerializer.Serialize(writer, item, options);
            }
        }
        writer.WriteEndArray();
    }

    private string? ExtractCommandFromObject(ref Utf8JsonReader reader)
    {
        reader.Read(); // Move past start object
        string? commandName = null;

        while (reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var propertyName = reader.GetString();
                reader.Read(); // Move to value

                if (propertyName == "command" && reader.TokenType == JsonTokenType.String)
                {
                    commandName = reader.GetString();
                }
                else
                {
                    // Skip other properties
                    SkipValue(ref reader);
                }
            }

            reader.Read(); // Move to next property or end object
        }

        return commandName;
    }

    private void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
        {
            int depth = 1;
            reader.Read();

            while (depth > 0)
            {
                if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
                {
                    depth++;
                }
                else if (reader.TokenType == JsonTokenType.EndObject || reader.TokenType == JsonTokenType.EndArray)
                {
                    depth--;
                }

                if (depth > 0)
                {
                    reader.Read();
                }
            }
        }
    }
}
