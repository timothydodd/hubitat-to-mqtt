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
        var attributeValues = new Dictionary<string, object?>();

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
            else
            {
                // Handle different value types
                attributeValues[propertyName] = ReadValue(ref reader);
            }

            reader.Read(); // Move to next property name or end object
        }

        // Add to dictionary with name as key
        if (attributeName != null)
        {
            if (!result.ContainsKey(attributeName))
            {
                result[attributeName] = attributeValues;
            }
            else
            {
                // Handle duplicate keys
                var existingValue = result[attributeName];
                if (existingValue is List<object> list)
                {
                    list.Add(attributeValues);
                }
                else
                {
                    result[attributeName] = new List<object> { existingValue!, attributeValues };
                }
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
