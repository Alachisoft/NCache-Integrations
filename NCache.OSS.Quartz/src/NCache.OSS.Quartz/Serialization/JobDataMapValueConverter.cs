using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace NCache.OSS.Quartz.Serialization;
internal sealed class JobDataMapValueConverter : JsonConverter<Dictionary<string, object?>>
{
    private const string TypeField = "$t";
    private const string ValueField = "$v";
    private const string ClassField = "$c";

    private const string EnumTag = "Enum";
    private const string StringMapTag = "StringMap";
    private const string JsonTag = "Json";

    internal static bool IsBuiltIn(object value) => value switch
    {
        float f => float.IsFinite(f),
        double d => double.IsFinite(d),
        Enum => true,
        Dictionary<string, string> => true,
        _ => value is string or char or short or int or long or byte or decimal or bool or Guid
            or DateTime or DateTimeOffset or TimeSpan or DateOnly or TimeOnly
    };

    internal static bool IsNonFiniteNumber(object value) =>
        value is float f && !float.IsFinite(f) || value is double d && !double.IsFinite(d);

    public override Dictionary<string, object?> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new Dictionary<string, object?>();
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected start of JobDataMap object.");
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected property name.");
            var key = reader.GetString()!;
            reader.Read();
            using var entry = JsonDocument.ParseValue(ref reader);
            var root = entry.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                result[key] = null;
                continue;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"JobDataMap entry for key '{key}' must be a '{{ \"{TypeField}\": ..., \"{ValueField}\": ... }}' object, " +
                    $"but found a JSON {root.ValueKind} value instead.");
            }

            var typeName = root.GetProperty(TypeField).GetString()!;
            var valueEl = root.GetProperty(ValueField);

            result[key] = typeName switch
            {
                nameof(String) => valueEl.GetString(),
                nameof(Char) => valueEl.GetString()![0],
                nameof(Int16) => valueEl.GetInt16(),
                nameof(Int32) => valueEl.GetInt32(),
                nameof(Int64) => valueEl.GetInt64(),
                nameof(Byte) => valueEl.GetByte(),
                nameof(Single) => valueEl.GetSingle(),
                nameof(Double) => valueEl.GetDouble(),
                nameof(Decimal) => valueEl.GetDecimal(),
                nameof(Boolean) => valueEl.GetBoolean(),
                nameof(Guid) => valueEl.GetGuid(),
                nameof(DateTime) => valueEl.GetDateTime(),
                nameof(DateTimeOffset) => valueEl.GetDateTimeOffset(),
                nameof(TimeSpan) => TimeSpan.ParseExact(valueEl.GetString()!, "c", CultureInfo.InvariantCulture),
                nameof(DateOnly) => DateOnly.ParseExact(valueEl.GetString()!, "O", CultureInfo.InvariantCulture),
                nameof(TimeOnly) => TimeOnly.ParseExact(valueEl.GetString()!, "O", CultureInfo.InvariantCulture),
                StringMapTag => valueEl.Deserialize<Dictionary<string, string>>(),
                EnumTag => ReadEnum(root, valueEl),
                JsonTag => ReadDeclared(root, valueEl),
                _ => throw new JsonException($"Unsupported JobDataMap value type: '{typeName}'.")
            };
        }

        return result;
    }

    private static object ReadEnum(JsonElement root, JsonElement valueEl)
    {
        var number = valueEl.GetInt64();
        var type = TypeNames.Resolve(root.GetProperty(ClassField).GetString()!);

        return type is { IsEnum: true } ? Enum.ToObject(type, number) : number;
    }

    private static object? ReadDeclared(JsonElement root, JsonElement valueEl)
    {
        var type = TypeNames.Resolve(root.GetProperty(ClassField).GetString()!);

        if (type is not null)
        {
            try
            {
                return valueEl.Deserialize(type);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
            }
        }

        return valueEl.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, object?> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, val) in value)
        {
            writer.WritePropertyName(key);

            if (val is null)
            {
                writer.WriteNullValue();
                continue;
            }

            writer.WriteStartObject();

            switch (val)
            {
                case Enum e:
                    writer.WriteString(TypeField, EnumTag);
                    writer.WriteString(ClassField, TypeNames.Of(e.GetType()));
                    writer.WritePropertyName(ValueField);
                    writer.WriteNumberValue(Convert.ToInt64(e, CultureInfo.InvariantCulture));
                    break;

                case Dictionary<string, string> map:
                    writer.WriteString(TypeField, StringMapTag);
                    writer.WritePropertyName(ValueField);
                    JsonSerializer.Serialize(writer, map);
                    break;

                default:
                    if (!WritePrimitive(writer, val))
                    {
                        if (IsNonFiniteNumber(val))
                        {
                            throw new JsonException($"JobDataMap value for key '{key}' is not a finite number.");
                        }

                        writer.WriteString(TypeField, JsonTag);
                        writer.WriteString(ClassField, TypeNames.Of(val.GetType()));
                        writer.WritePropertyName(ValueField);
                        JsonSerializer.Serialize(writer, val, val.GetType());
                    }

                    break;
            }

            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static bool WritePrimitive(Utf8JsonWriter writer, object val)
    {
        switch (val)
        {
            case string s: Tag(writer, nameof(String)); writer.WriteStringValue(s); return true;
            case char c: Tag(writer, nameof(Char)); writer.WriteStringValue(c.ToString()); return true;
            case short sh: Tag(writer, nameof(Int16)); writer.WriteNumberValue(sh); return true;
            case int i: Tag(writer, nameof(Int32)); writer.WriteNumberValue(i); return true;
            case long l: Tag(writer, nameof(Int64)); writer.WriteNumberValue(l); return true;
            case byte by: Tag(writer, nameof(Byte)); writer.WriteNumberValue(by); return true;
            case float f when float.IsFinite(f): Tag(writer, nameof(Single)); writer.WriteNumberValue(f); return true;
            case double d when double.IsFinite(d): Tag(writer, nameof(Double)); writer.WriteNumberValue(d); return true;
            case decimal m: Tag(writer, nameof(Decimal)); writer.WriteNumberValue(m); return true;
            case bool b: Tag(writer, nameof(Boolean)); writer.WriteBooleanValue(b); return true;
            case Guid g: Tag(writer, nameof(Guid)); writer.WriteStringValue(g); return true;
            case DateTime dt: Tag(writer, nameof(DateTime)); writer.WriteStringValue(dt); return true;
            case DateTimeOffset dto: Tag(writer, nameof(DateTimeOffset)); writer.WriteStringValue(dto); return true;
            case TimeSpan ts: Tag(writer, nameof(TimeSpan)); writer.WriteStringValue(ts.ToString("c", CultureInfo.InvariantCulture)); return true;
            case DateOnly d: Tag(writer, nameof(DateOnly)); writer.WriteStringValue(d.ToString("O", CultureInfo.InvariantCulture)); return true;
            case TimeOnly t: Tag(writer, nameof(TimeOnly)); writer.WriteStringValue(t.ToString("O", CultureInfo.InvariantCulture)); return true;
            default: return false;
        }
    }

    private static void Tag(Utf8JsonWriter writer, string tag)
    {
        writer.WriteString(TypeField, tag);
        writer.WritePropertyName(ValueField);
    }
}
