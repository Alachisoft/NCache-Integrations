//NCacheKeys.cs
using System;

namespace NCache.OSS.Hangfire
{

    [Serializable]
    internal class NCacheProcessingEntry
    {
        public string Queue { get; set; }
        public DateTime FetchedAt { get; set; }
    }
     public static class NCacheKeys
    {
        public static string Job(string prefix, string jobId) => $"{prefix}job:{jobId}";

        public static string JobState(string prefix, string jobId) => $"{prefix}job:{jobId}:state";

        public static string JobParameters(string prefix, string jobId) => $"{prefix}job:{jobId}:parameters";

        public static string JobHistory(string prefix, string jobId) => $"{prefix}job:{jobId}:history";

        public static string Queue(string prefix, string queue) => $"{prefix}queue:{queue}";
        public static string Processing(string prefix) => $"{prefix}processing";
        public static string Processing(string prefix, string serverId) => $"{prefix}processing:{serverId}";
        public static string Servers(string prefix) => $"{prefix}servers";

        public static string Set(string prefix, string name) => $"{prefix}set:{name}";

        public static string Hash(string prefix, string name) => $"{prefix}hash:{name}";

        public static string List(string prefix, string name) => $"{prefix}list:{name}";

        public static string QueueTopic(string prefix, string queue) => $"{prefix}topic:queue:{queue}";

        public static string Counter(string prefix, string name) => $"{prefix}counter:{name}";
        public static string CounterRaw(string prefix, string name, string entryId) => $"{prefix}counter:{name}:raw:{entryId}";
        public static string QueueHead(string prefix, string queue) => $"{prefix}queue:{queue}:head";
        public static string QueueTail(string prefix, string queue) => $"{prefix}queue:{queue}:tail";
        public static string QueueItem(string prefix, string queue, int index) => $"{prefix}queue:{queue}:item:{index}";
        public static string CounterPending(string prefix, string name) => $"{prefix}counter:{name}:pending";
        public static string CounterRegistry(string prefix) => $"{prefix}counters";


        public static string QueueRegistry(string prefix) => $"{prefix}queueregistry";
        internal static string WrapperLock(string key) => $"NCacheWrapperLock:{key}";
    }
}