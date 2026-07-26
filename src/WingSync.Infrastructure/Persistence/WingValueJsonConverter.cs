using System.Text.Json;
using System.Text.Json.Serialization;
using WingSync.Core.Domain;

namespace WingSync.Infrastructure.Persistence;

internal sealed class WingValueJsonConverter : JsonConverter<WingValue>
{
    public override WingValue Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var typeElement)
            || !root.TryGetProperty("value", out var valueElement)
            || typeElement.ValueKind != JsonValueKind.String
            || !Enum.TryParse<WingValueType>(
                typeElement.GetString(),
                ignoreCase: false,
                out var valueType)
            || !Enum.IsDefined(valueType))
        {
            throw new JsonException("A cached WING value must contain a valid type and value.");
        }

        try
        {
            return valueType switch
            {
                WingValueType.I => WingValue.FromInt32(valueElement.GetInt32()),
                WingValueType.F => WingValue.FromFloat(valueElement.GetSingle()),
                WingValueType.S => WingValue.FromString(
                    valueElement.GetString()
                    ?? throw new JsonException("A cached WING string cannot be null.")),
                _ => throw new JsonException("The cached WING value type is not supported."),
            };
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonException("The cached WING value does not match its declared type.", exception);
        }
        catch (FormatException exception)
        {
            throw new JsonException("The cached WING value is malformed.", exception);
        }
        catch (OverflowException exception)
        {
            throw new JsonException("The cached WING value is outside its supported range.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        WingValue value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type.ToString());
        writer.WritePropertyName("value");
        switch (value.Type)
        {
            case WingValueType.I:
                writer.WriteNumberValue(value.AsInt32());
                break;

            case WingValueType.F:
                writer.WriteNumberValue(value.AsFloat());
                break;

            case WingValueType.S:
                writer.WriteStringValue(value.AsString());
                break;

            default:
                throw new JsonException($"Unsupported WING value type '{value.Type}'.");
        }

        writer.WriteEndObject();
    }
}
