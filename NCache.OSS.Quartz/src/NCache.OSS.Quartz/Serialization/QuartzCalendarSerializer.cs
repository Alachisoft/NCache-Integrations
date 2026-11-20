using Quartz;
using Quartz.Extensibility;
using Quartz.Impl;
using System.Text;

namespace NCache.OSS.Quartz.Serialization;

internal static class QuartzCalendarSerializer
{
    private static readonly SystemTextJsonObjectSerializer Serializer = new();

    public static string Serialize(ICalendar calendar, IObjectSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        var bytes = (serializer ?? Serializer).Serialize(calendar);

        return Encoding.UTF8.GetString(bytes);
    }

    public static ICalendar Deserialize(string json, IObjectSerializer? serializer = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException(
                "Calendar JSON cannot be empty.",
                nameof(json));
        }

        var bytes = Encoding.UTF8.GetBytes(json);

        return (serializer ?? Serializer).Deserialize<ICalendar>(bytes)
            ?? throw new InvalidOperationException(
                "Quartz calendar deserialization returned null.");
    }
}

