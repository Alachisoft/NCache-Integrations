using Quartz;

namespace NCache.OSS.Quartz.Serialization;

internal static class JobDataMapMapper
{
    public static Dictionary<string, object?> ToStorageMap(JobDataMap? map, QuartzObjectSerialization? serialization = null)
    {
        if (map is null) return new Dictionary<string, object?>();
        var result = new Dictionary<string, object?>(map.Count);
        foreach (var key in map.Keys)
        {
            var value = map[key];

            if (value is null)
            {
                result[key] = null;
                continue;
            }

            if (JobDataMapValueConverter.IsNonFiniteNumber(value))
            {
                throw Refused(key, value, "it is not a finite number.", cause: null);
            }

            if (!JobDataMapValueConverter.IsBuiltIn(value) &&
                !(serialization ?? QuartzObjectSerialization.Default).AcceptsJobDataValue(key, value, out var reason))
            {
                throw Refused(key, value, reason!.Message, reason);
            }

            result[key] = value is Dictionary<string, string> stringMap ? new Dictionary<string, string>(stringMap) : value;
        }
        return result;
    }

    // Builds the refusal for a value the store cannot keep, carrying the reason it was refused.
    private static JobPersistenceException Refused(string key, object value, string why, Exception? cause)
    {
        var message = $"JobDataMap value '{key}' ({value.GetType().Name}) cannot be persisted by NCache.OSS.Quartz: {why}";

        return cause is null ? new JobPersistenceException(message) : new JobPersistenceException(message, cause);
    }

    public static JobDataMap ToJobDataMap(Dictionary<string, object?> storageMap)
    {
        var map = new JobDataMap();
        foreach (var (key, value) in storageMap)
        {
            map[key] = value is Dictionary<string, string> stringMap ? new Dictionary<string, string>(stringMap) : value;
        }
        return map;
    }
}
