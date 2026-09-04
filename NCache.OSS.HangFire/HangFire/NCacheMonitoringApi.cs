using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;

namespace NCache.OSS.Hangfire
{
    internal class NCacheMonitoringApi : IMonitoringApi
    {
        private readonly NCacheStorage _storage;

        public NCacheMonitoringApi(NCacheStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        public IList<QueueWithTopEnqueuedJobsDto> Queues()
        {
            var cache = _storage.Cache;
            var registryKey = NCacheKeys.QueueRegistry(_storage.Prefix);

            var queueNames = cache.Contains(registryKey)
                ? cache.Get<HashSet<string>>(registryKey) ?? new HashSet<string>()
                : new HashSet<string>();

            var result = new List<QueueWithTopEnqueuedJobsDto>();

            foreach (var name in queueNames.OrderBy(x => x))
            {
                result.Add(new QueueWithTopEnqueuedJobsDto
                {
                    Name = name,
                    Length = GetQueueLength(name),
                    Fetched = FetchedCount(name),
                    FirstJobs = EnqueuedJobs(name, 0, 5)
                });
            }

            return result;
        }

        public IList<ServerDto> Servers()
        {
            var cache = _storage.Cache;
            var serversKey = NCacheKeys.Servers(_storage.Prefix);

            if (!cache.Contains(serversKey))
                return new List<ServerDto>();

            var servers = cache.Get<Dictionary<string, NCacheServerData>>(serversKey);

            if (servers == null)
                return new List<ServerDto>();

            return servers
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => new ServerDto
                {
                    Name = kvp.Key,
                    Queues = kvp.Value.Queues?.ToList() ?? new List<string>(),
                    WorkersCount = kvp.Value.WorkerCount,
                    StartedAt = kvp.Value.StartedAt,
                    Heartbeat = kvp.Value.Heartbeat
                })
                .ToList();
        }
        public JobDetailsDto JobDetails(string jobId)
        {
            var cache = _storage.Cache;
            var jobKey = NCacheKeys.Job(_storage.Prefix, jobId);
            if (!cache.Contains(jobKey)) return null;

            var entry = cache.Get<Dictionary<string, string>>(jobKey);
            entry.TryGetValue("CreatedAt", out var createdAtRaw);

            var job = DeserializeJob(entry);

            var paramsKey = NCacheKeys.JobParameters(_storage.Prefix, jobId);
            var properties = cache.Contains(paramsKey)
                ? cache.Get<Dictionary<string, string>>(paramsKey) ?? new Dictionary<string, string>()
                : new Dictionary<string, string>();

            var history = GetJobHistory(jobId);

            return new JobDetailsDto
            {
                Job = job,
                CreatedAt = createdAtRaw != null ? JobHelper.DeserializeDateTime(createdAtRaw) : (DateTime?)null,
                Properties = properties,
                History = history
            };
        }

        public StatisticsDto GetStatistics()
        {
            var serverCount = GetServerCount();
            var queueNames = GetQueueRegistry();

            return new StatisticsDto
            {
                Servers = serverCount,
                Queues = queueNames.Count,
                Scheduled = CountSet("schedule"),
                Enqueued = queueNames.Sum(name => GetQueueLength(name)),
                Processing = ProcessingCount(),
                Succeeded = CountCounter("stats:succeeded"),
                Failed = CountCounter("stats:failed"),
                Deleted = CountCounter("stats:deleted"),
                Recurring = CountSet("recurring-jobs")
            };
        }

        public JobList<EnqueuedJobDto> EnqueuedJobs(string queue, int from, int perPage)
        {
            var ids = GetQueue(queue).Skip(from).Take(perPage);

            return BuildJobList(ids, (jobId, job, stateData) =>
            {
                stateData.TryGetValue("State", out var stateName);
                return new EnqueuedJobDto
                {
                    Job = job,
                    State = stateName,
                    InEnqueuedState = string.Equals(stateName, EnqueuedState.StateName, StringComparison.OrdinalIgnoreCase),
                    EnqueuedAt = stateData.TryGetValue("EnqueuedAt", out var raw) ? JobHelper.DeserializeNullableDateTime(raw) : null
                };
            });
        }

        public JobList<FetchedJobDto> FetchedJobs(string queue, int from, int perPage)
        {
            var ids = GetFetchedJobIds(queue).Skip(from).Take(perPage);

            return BuildJobList(ids, (jobId, job, stateData) =>
            {
                stateData.TryGetValue("State", out var stateName);
                return new FetchedJobDto
                {
                    Job = job,
                    State = stateName,
                    FetchedAt = stateData.TryGetValue("FetchedAt", out var raw) ? JobHelper.DeserializeNullableDateTime(raw) : null
                };
            });
        }

        public JobList<ProcessingJobDto> ProcessingJobs(int from, int count)
        {
            var ids = GetProcessingJobIds().Skip(from).Take(count);

            return BuildJobList(ids, (jobId, job, stateData) =>
            {
                stateData.TryGetValue("State", out var stateName);
                return new ProcessingJobDto
                {
                    Job = job,
                    InProcessingState = string.Equals(stateName, ProcessingState.StateName, StringComparison.OrdinalIgnoreCase),
                    ServerId = stateData.TryGetValue("ServerId", out var serverId) ? serverId : null,
                    StartedAt = stateData.TryGetValue("StartedAt", out var raw) ? JobHelper.DeserializeNullableDateTime(raw) : null
                };
            });
        }
        public JobList<ScheduledJobDto> ScheduledJobs(int from, int count)
        {
            var cache = _storage.Cache;
            var setKey = NCacheKeys.Set(_storage.Prefix, "schedule");
            var scores = cache.Contains(setKey)
                ? cache.Get<Dictionary<string, double>>(setKey) ?? new Dictionary<string, double>()
                : new Dictionary<string, double>();

            var ids = scores.OrderBy(kvp => kvp.Value).Skip(from).Take(count).Select(kvp => kvp.Key);

            return BuildJobList(ids, (jobId, job, stateData) =>
            {
                stateData.TryGetValue("State", out var stateName);
                var enqueueAt = scores.TryGetValue(jobId, out var score) ? JobHelper.FromTimestamp((long)score) : DateTime.MinValue;

                return new ScheduledJobDto
                {
                    Job = job,
                    InScheduledState = string.Equals(stateName, ScheduledState.StateName, StringComparison.OrdinalIgnoreCase),
                    EnqueueAt = enqueueAt,
                    ScheduledAt = stateData.TryGetValue("ScheduledAt", out var raw) ? JobHelper.DeserializeNullableDateTime(raw) : null
                };
            });
        }

        public JobList<SucceededJobDto> SucceededJobs(int from, int count)
        {
            return BuildJobList(GetListPage("succeeded", from, count), (jobId, job, stateData) =>
            {
                stateData.TryGetValue("State", out var stateName);
                return new SucceededJobDto
                {
                    Job = job,
                    Result = stateData.TryGetValue("Result", out var result) ? result : null,
                    SucceededAt = stateData.TryGetValue("SucceededAt", out var raw) ? JobHelper.DeserializeNullableDateTime(raw) : null,
                    InSucceededState = string.Equals(stateName, SucceededState.StateName, StringComparison.OrdinalIgnoreCase)
                };
            });
        }

        public JobList<FailedJobDto> FailedJobs(int from, int count)
        {
            return BuildJobList(GetListPage("failed", from, count), (jobId, job, stateData) =>
            {
                stateData.TryGetValue("State", out var stateName);
                stateData.TryGetValue("Reason", out var reason);
                return new FailedJobDto
                {
                    Job = job,
                    Reason = reason,
                    ExceptionDetails = stateData.TryGetValue("ExceptionDetails", out var d) ? d : null,
                    ExceptionMessage = stateData.TryGetValue("ExceptionMessage", out var m) ? m : null,
                    ExceptionType = stateData.TryGetValue("ExceptionType", out var t) ? t : null,
                    FailedAt = stateData.TryGetValue("FailedAt", out var raw) ? JobHelper.DeserializeNullableDateTime(raw) : null,
                    InFailedState = string.Equals(stateName, FailedState.StateName, StringComparison.OrdinalIgnoreCase)
                };
            });
        }

        public JobList<DeletedJobDto> DeletedJobs(int from, int count)
        {
            return BuildJobList(GetListPage("deleted", from, count), (jobId, job, stateData) =>
            {
                stateData.TryGetValue("State", out var stateName);
                return new DeletedJobDto
                {
                    Job = job,
                    DeletedAt = stateData.TryGetValue("DeletedAt", out var raw) ? JobHelper.DeserializeNullableDateTime(raw) : null,
                    InDeletedState = string.Equals(stateName, DeletedState.StateName, StringComparison.OrdinalIgnoreCase)
                };
            });
        }

        public long ScheduledCount() => CountSet("schedule");
        public long EnqueuedCount(string queue) => GetQueueLength(queue);
        public long ProcessingCount()
        {
            return GetAllProcessingEntries().Count;
        }
        public long FetchedCount(string queue)
        {
            var all = GetAllProcessingEntries();
            return all.Values.Count(x => x.Queue == queue);
        }
        public long SucceededListCount() => GetList("succeeded").Count;
        public long DeletedListCount() => GetList("deleted").Count;
        public long FailedCount() => GetList("failed").Count;
        public IDictionary<DateTime, long> SucceededByDatesCount() => GetTimelineStats("succeeded", "SucceededAt");

        public IDictionary<DateTime, long> FailedByDatesCount() => GetTimelineStats("failed", "FailedAt");

        public IDictionary<DateTime, long> HourlySucceededJobs() => GetHourlyTimelineStats("succeeded", "SucceededAt");

        public IDictionary<DateTime, long> HourlyFailedJobs() => GetHourlyTimelineStats("failed", "FailedAt");


        // Standardized helpers
        private List<string> GetQueue(string queue)
        {
            var cache = _storage.Cache;
            var headKey = NCacheKeys.QueueHead(_storage.Prefix, queue);
            var tailKey = NCacheKeys.QueueTail(_storage.Prefix, queue);

            var head = cache.Contains(headKey) ? cache.Get<int>(headKey) : 0;
            var tail = cache.Contains(tailKey) ? cache.Get<int>(tailKey) : 0;

            var result = new List<string>();
            for (int i = head; i < tail; i++)
            {
                var itemKey = NCacheKeys.QueueItem(_storage.Prefix, queue, i);
                if (cache.Contains(itemKey))
                    result.Add(cache.Get<string>(itemKey));
            }
            return result;
        }

        private List<string> GetList(string name)
        {
            var cache = _storage.Cache;
            var key = NCacheKeys.List(_storage.Prefix, name);

            if (!cache.Contains(key))
                return new List<string>();

            return cache.Get<List<string>>(key) ?? new List<string>();
        }

        private List<string> GetListPage(string listName, int from, int count)
        {
            return GetList(listName).Skip(from).Take(count).ToList();
        }

        private HashSet<string> GetQueueRegistry()
        {
            var cache = _storage.Cache;
            var key = NCacheKeys.QueueRegistry(_storage.Prefix);

            if (!cache.Contains(key))
                return new HashSet<string>();

            return cache.Get<HashSet<string>>(key) ?? new HashSet<string>();
        }

        private int GetServerCount()
        {
            var cache = _storage.Cache;
            var key = NCacheKeys.Servers(_storage.Prefix);

            if (!cache.Contains(key))
                return 0;

            var servers = cache.Get<Dictionary<string, NCacheServerData>>(key);
            return servers?.Count ?? 0;
        }

        private Dictionary<string, string> GetState(string jobId)
        {
            var cache = _storage.Cache;
            var stateKey = NCacheKeys.JobState(_storage.Prefix, jobId);

            if (!cache.Contains(stateKey))
                return new Dictionary<string, string>();

            return cache.Get<Dictionary<string, string>>(stateKey)
                       ?? new Dictionary<string, string>();
             
        }

        private List<StateHistoryDto> GetJobHistory(string jobId)
        {
            var cache = _storage.Cache;
            var historyKey = NCacheKeys.JobHistory(_storage.Prefix, jobId);
            var history = new List<StateHistoryDto>();

            if (!cache.Contains(historyKey))
                return history;

            var rawHistory = cache.Get<List<Dictionary<string, string>>>(historyKey)
                              ?? new List<Dictionary<string, string>>();

            foreach (var stateEntry in rawHistory)
            {
                stateEntry.TryGetValue("State", out var stateName);
                stateEntry.TryGetValue("Reason", out var reason);
                stateEntry.TryGetValue("CreatedAt", out var stateCreatedAtRaw);

                var stateProperties = stateEntry
                    .Where(kvp => kvp.Key != "State" && kvp.Key != "Reason" && kvp.Key != "CreatedAt")
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                history.Add(new StateHistoryDto
                {
                    StateName = stateName,
                    Reason = reason,
                    CreatedAt = stateCreatedAtRaw != null ? JobHelper.DeserializeDateTime(stateCreatedAtRaw) : DateTime.MinValue,
                    Data = stateProperties
                });
            }

            return history;
        }

        private Job DeserializeJob(Dictionary<string, string> entry)
        {
            try
            {
                entry.TryGetValue("Type", out var type);
                entry.TryGetValue("Method", out var method);
                entry.TryGetValue("ParameterTypes", out var parameterTypes);
                entry.TryGetValue("Arguments", out var arguments);

                if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(method))
                {
                    return new InvocationData(type, method, parameterTypes, arguments).DeserializeJob();
                }
            }
            catch (JobLoadException) { }

            return null;
        }

        private JobList<TDto> BuildJobList<TDto>(
            IEnumerable<string> jobIds,
            Func<string, Job, Dictionary<string, string>, TDto> selector)
        {
            var result = new List<KeyValuePair<string, TDto>>();

            foreach (var jobId in jobIds)
            {
                var job = LoadJob(jobId);

                var stateData = GetState(jobId);

                result.Add(new KeyValuePair<string, TDto>(
                    jobId,
                    selector(jobId, job, stateData)));
            }

            return new JobList<TDto>(result);
        }

        private Job LoadJob(string jobId)
        {
            var cache = _storage.Cache;
            var jobKey = NCacheKeys.Job(_storage.Prefix, jobId);

            if (!cache.Contains(jobKey))
                return null;

            var entry = cache.Get<Dictionary<string, string>>(jobKey);

            if (entry == null)
                return null;

            return DeserializeJob(entry);
        }

        private long CountCounter(string name) => NCacheCounters.GetValue(_storage, name);

        private long CountSet(string name)
        {
            var cache = _storage.Cache;
            var key = NCacheKeys.Set(_storage.Prefix, name);
            return cache.Contains(key) ? cache.Get<Dictionary<string, double>>(key)?.Count ?? 0 : 0;
        }

        private IDictionary<DateTime, long> GetTimelineStats(string listName, string timestampField, int days = 7)
        {
            var result = new Dictionary<DateTime, long>();
            var today = DateTime.UtcNow.Date;
            for (int i = 0; i < days; i++) result[today.AddDays(-i)] = 0;

            foreach (var stateData in GetStateEntries(listName))
            {
                if (!stateData.TryGetValue(timestampField, out var raw)) continue;
                var timestamp = JobHelper.DeserializeNullableDateTime(raw);
                if (timestamp == null) continue;

                var date = timestamp.Value.Date;
                if (result.ContainsKey(date)) result[date]++;
            }

            return result;
        }
        private IDictionary<DateTime, long> GetHourlyTimelineStats(string listName, string timestampField)
        {
            var result = new Dictionary<DateTime, long>();
            var now = DateTime.UtcNow;
            for (int i = 0; i < 24; i++)
            {
                var h = now.AddHours(-i);
                result[new DateTime(h.Year, h.Month, h.Day, h.Hour, 0, 0, DateTimeKind.Utc)] = 0;
            }

            foreach (var stateData in GetStateEntries(listName))
            {
                if (!stateData.TryGetValue(timestampField, out var raw)) continue;
                var timestamp = JobHelper.DeserializeNullableDateTime(raw);
                if (timestamp == null) continue;

                var bucket = new DateTime(timestamp.Value.Year, timestamp.Value.Month, timestamp.Value.Day,
                                           timestamp.Value.Hour, 0, 0, DateTimeKind.Utc);
                if (result.ContainsKey(bucket)) result[bucket]++;
            }

            return result;
        }

        private IEnumerable<Dictionary<string, string>> GetStateEntries(string listName)
        {
            foreach (var jobId in GetList(listName))
            {
                var stateData = GetState(jobId);
                if (stateData.Count > 0)
                    yield return stateData;
            }
        }

        
        private List<string> GetProcessingJobIds()
        {
            return new List<string>(GetAllProcessingEntries().Keys);
        }
        private List<string> GetFetchedJobIds(string queue)
        {
            var all = GetAllProcessingEntries();
            return all.Where(kvp => kvp.Value.Queue == queue)
                      .Select(kvp => kvp.Key)
                      .ToList();
        }
       
        private Dictionary<string, NCacheProcessingEntry> GetAllProcessingEntries()
        {
            var cache = _storage.Cache;
            var serversKey = NCacheKeys.Servers(_storage.Prefix);
            var result = new Dictionary<string, NCacheProcessingEntry>();

            // Get all known server IDs
            List<string> serverIds;
            if (!cache.Contains(serversKey))
                return result;

            var servers = cache.Get<Dictionary<string, NCacheServerData>>(serversKey);
            if (servers == null) return result;
            serverIds = servers.Keys.ToList();

            // Merge every server's processing set
            foreach (var serverId in serverIds)
            {
                var procKey = NCacheKeys.Processing(_storage.Prefix, serverId);
                if (!cache.Contains(procKey)) continue;

                var dict = cache.Get<Dictionary<string, NCacheProcessingEntry>>(procKey);
                if (dict == null) continue;

                foreach (var kvp in dict)
                {
                    // In the unlikely case of duplicate job IDs across servers,
                    // the most recent entry wins.
                    result[kvp.Key] = kvp.Value;
                }
            }

            return result;
        }
        private int GetQueueLength(string queue)
        {
            var cache = _storage.Cache;
            var headKey = NCacheKeys.QueueHead(_storage.Prefix, queue);
            var tailKey = NCacheKeys.QueueTail(_storage.Prefix, queue);

            var head = cache.Contains(headKey) ? cache.Get<int>(headKey) : 0;
            var tail = cache.Contains(tailKey) ? cache.Get<int>(tailKey) : 0;

            return Math.Max(0, tail - head);
        }
    }
}
