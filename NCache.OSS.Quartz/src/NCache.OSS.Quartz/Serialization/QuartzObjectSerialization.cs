using System.Text;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl;

namespace NCache.OSS.Quartz.Serialization;

internal sealed class QuartzObjectSerialization
{
    public static readonly QuartzObjectSerialization Default = new(null);

    public QuartzObjectSerialization(IObjectSerializer? serializer)
    {
        Serializer = serializer ?? new SystemTextJsonObjectSerializer();
    }

    public IObjectSerializer Serializer { get; }

    // Asks Quartz's serializer whether it can carry this JobDataMap value, handing back its reason when it cannot.
    public bool AcceptsJobDataValue(string key, object value, out Exception? reason)
    {
        try
        {
            Serializer.Serialize(new JobDataMap { [key] = value });
            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex;
            return false;
        }
    }

    public string SerializeTrigger(IOperableTrigger trigger)
    {
        var copy = (IOperableTrigger) trigger.Clone();
        copy.JobDataMap = new JobDataMap();

        try
        {
            return Encoding.UTF8.GetString(Serializer.Serialize<ITrigger>(copy));
        }
        catch (Exception ex)
        {
            throw new NotSupportedException(
                $"Trigger '{trigger.Key}' is of type '{trigger.GetType().FullName}', which has no serializer registered. " +
                "Register a TriggerSerializer for it with the scheduler's SystemTextJsonSerializerRegistry " +
                "(AddTriggerSerializer), or with NCacheJobStoreOptions.ConfigureSerializer.", ex);
        }
    }

    public IOperableTrigger DeserializeTrigger(string json, string triggerDescription)
    {
        ITrigger? trigger;

        try
        {
            trigger = Serializer.Deserialize<ITrigger>(Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex)
        {
            throw new NotSupportedException(
                $"Custom trigger '{triggerDescription}' cannot be read back: {ex.Message} " +
                "Its TriggerSerializer must be registered on every node.", ex);
        }

        return trigger as IOperableTrigger
               ?? throw new NotSupportedException($"Custom trigger '{triggerDescription}' was read back as '{trigger?.GetType().FullName ?? "null"}', which is not an IOperableTrigger.");
    }
}
