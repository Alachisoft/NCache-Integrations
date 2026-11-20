using System.Text.Json;
using System.Text.Json.Serialization;

namespace NCache.OSS.Quartz.Serialization;

internal static class NCacheSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter(),
            new JobDataMapValueConverter()
        },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static byte[] Serialize<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Deserialize<T>(byte[] data)
        => JsonSerializer.Deserialize<T>(data, Options)
           ?? throw new InvalidOperationException($"Deserialization of {typeof(T).Name} returned null.");

    public static T Deserialize<T>(ReadOnlySpan<byte> data)
        => JsonSerializer.Deserialize<T>(data, Options)
           ?? throw new InvalidOperationException($"Deserialization of {typeof(T).Name} returned null.");
}
