using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting;

public sealed class ConcurrencyRateLimiter<TKey> : RateLimiter
{
    private readonly ConcurrencyManager _manager;
    private readonly ConcurrencyRateLimiterOptions _options;

    private readonly ConcurrentQueue<Request> _queue = new();

    private readonly Channel<AdmissionTicket> _admissionChannel =
        Channel.CreateUnbounded<AdmissionTicket>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly Task _admissionLoopTask;

    private readonly CancellationTokenSource _admissionLoopCts = new();

    private readonly PeriodicTimer? _periodicTimer;

    private bool _disposed;

    private readonly ConcurrencyLease _failedLease =
        new(false, null, null);

    private int _activeRequestsCount;

    private long _idleSince = Stopwatch.GetTimestamp();

    public override TimeSpan? IdleDuration =>
        Interlocked.CompareExchange(
            ref _activeRequestsCount,
            0,
            0) > 0
            ? null
            : Stopwatch.GetElapsedTime(_idleSince);

    public ConcurrencyRateLimiter(
        TKey partitionKey,
        ConcurrencyRateLimiterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.PermitLimit <= 0)
        {
            throw new ArgumentException(
                $"{nameof(options.PermitLimit)} must be > 0");
        }

        if (options.QueueLimit < 0)
        {
            throw new ArgumentException(
                $"{nameof(options.QueueLimit)} must be >= 0");
        }

        if (!options.isValid(out var err))
            throw new ArgumentException(err);

        _options = new ConcurrencyRateLimiterOptions
        {
            PermitLimit = options.PermitLimit,
            QueueLimit = options.QueueLimit,
            TryDequeuePeriod = options.TryDequeuePeriod,
            ExpectedRequestTimeout = options.ExpectedRequestTimeout,
            LockTimeout = options.LockTimeout,
            CacheName = options.CacheName,
            ServerList = options.ServerList,
            Port = options.Port
        };

        // 4. Pass the safely isolated clone to the manager
        _manager = new ConcurrencyManager(
            partitionKey?.ToString() ?? string.Empty,
            _options);

        _admissionLoopTask = ProcessAdmissionsAsync(_admissionLoopCts.Token);

        if (_options.QueueLimit > 0)
        {
            _periodicTimer =
                new PeriodicTimer(_options.TryDequeuePeriod);

            _ = StartDequeueTimerAsync(_periodicTimer);
        }
    }

    public override RateLimiterStatistics? GetStatistics()
    {
        return _manager.GetStatistics();
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        _idleSince = Stopwatch.GetTimestamp();

        if (permitCount > _options.PermitLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(permitCount));
        }

        Interlocked.Increment(ref _activeRequestsCount);

        try
        {
            return await AcquireAsyncCoreInternal(
                cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequestsCount);

            _idleSince = Stopwatch.GetTimestamp();
        }
    }

    protected override RateLimitLease AttemptAcquireCore(
        int permitCount)
    {
        return _failedLease;
    }

    private async ValueTask<RateLimitLease>
        AcquireAsyncCoreInternal(
        CancellationToken cancellationToken)
    {
        var leaseContext = new ConcurrencyLeaseContext
        {
            Limit = _options.PermitLimit,
            RequestId = Guid.NewGuid().ToString(),
        };

        var ticket = new AdmissionTicket(leaseContext.RequestId);
        _admissionChannel.Writer.TryWrite(ticket);

        var response = await ticket.Completion.Task;

        leaseContext.Count = response.Count;

        if (response.Allowed)
        {
            return new ConcurrencyLease(
                true,
                this,
                leaseContext);
        }

        if (response.Queued)
        {
            Request request = new()
            {
                CancellationToken = cancellationToken,
                LeaseContext = leaseContext,
                TaskCompletionSource =
                    new TaskCompletionSource<RateLimitLease>()
            };

            if (cancellationToken.CanBeCanceled)
            {
                request.CancellationTokenRegistration =
                    cancellationToken.Register(static state =>
                    {
                        // Explicitly unbox the object back into the expected ValueTuple structure
                        var (req, mgr) = ((Request, ConcurrencyManager))state!;

                        if (req.TaskCompletionSource!.TrySetCanceled(req.CancellationToken))
                        {
                            if (req.LeaseContext?.RequestId is not null)
                            {
                                _ = mgr.ReleaseQueueLeaseAsync(req.LeaseContext.RequestId);
                            }
                        }
                    }, (request, _manager));
            }

            _queue.Enqueue(request);

            return await request.TaskCompletionSource.Task;
        }

        return new ConcurrencyLease(
            false,
            this,
            leaseContext);
    }

    private async Task ProcessAdmissionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var ticket in _admissionChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    var response = await _manager.TryAcquireLeaseAsync(
                        ticket.RequestId,
                        tryEnqueue: true);

                    ticket.Completion.TrySetResult(response);
                }
                catch (Exception ex)
                {
                    ticket.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during Dispose.
        }
    }

    private void Release(
        ConcurrencyLeaseContext leaseContext)
    {
        if (leaseContext.RequestId is null)
        {
            return;
        }

        _ = ReleaseAndPumpQueueAsync(leaseContext.RequestId);
    }

    private async Task ReleaseAndPumpQueueAsync(string requestId)
    {
        await _manager.ReleaseLeaseAsync(requestId);

        if (_options.QueueLimit > 0)
        {
            await TryDequeueRequestsAsync();
        }
    }

    private async Task StartDequeueTimerAsync(
        PeriodicTimer periodicTimer)
    {
        while (await periodicTimer.WaitForNextTickAsync())
        {
            await TryDequeueRequestsAsync();
        }
    }

    private int _dequeueRunning;

    private async Task TryDequeueRequestsAsync()
    {
        if (Interlocked.CompareExchange(ref _dequeueRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            while (_queue.TryPeek(out var request))
            {
                if (request.TaskCompletionSource!
                    .Task
                    .IsCompleted)
                {
                    try
                    {
                        await _manager.ReleaseQueueLeaseAsync(
                            request.LeaseContext!
                                .RequestId!);
                    }
                    finally
                    {
                        request
                            .CancellationTokenRegistration
                            .Dispose();

                        _queue.TryDequeue(out _);
                    }

                    continue;
                }

                var response =
                    await _manager.TryPromoteQueuedAsync(
                        request.LeaseContext!
                            .RequestId!);

                request.LeaseContext.Count =
                    response.Count;

                if (response.Allowed)
                {
                    var pendingLease = new ConcurrencyLease(true, this, request.LeaseContext);
                    try
                    {
                        if (request.TaskCompletionSource?.TrySetResult(pendingLease) == false)
                        {
                            await _manager.ReleaseLeaseAsync(request.LeaseContext.RequestId!);
                        }
                    }
                    finally
                    {
                        request.CancellationTokenRegistration.Dispose();
                        _queue.TryDequeue(out _);
                    }
                }
                else
                {
                    break;
                }
            }
        }
        catch
        {
        }
        finally
        {
            Volatile.Write(ref _dequeueRunning, 0);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            return;
        }

        _disposed = true;

        _periodicTimer?.Dispose();

        _admissionChannel.Writer.TryComplete();
        _admissionLoopCts.Cancel();

        while (_queue.TryDequeue(out var request))
        {
            request?.CancellationTokenRegistration.Dispose();

            // Crucial: Remove from NCache so it doesn't block surviving nodes!
            if (request?.LeaseContext?.RequestId is not null)
            {
                _ = _manager.ReleaseQueueLeaseAsync(request.LeaseContext.RequestId);
            }

            request?.TaskCompletionSource?.TrySetResult(_failedLease);
        }

        _admissionLoopCts.Dispose();

        base.Dispose(disposing);
    }

    protected override ValueTask DisposeAsyncCore()
    {
        Dispose(true);

        return default;
    }

    private sealed class ConcurrencyLeaseContext
    {
        public string? RequestId { get; set; }

        public long Count { get; set; }

        public long Limit { get; set; }
    }

    private sealed class ConcurrencyLease : RateLimitLease
    {
        private static readonly string[] s_allMetadataNames =
        {
            "Limit",
            "Remaining"
        };

        private bool _disposed;

        private readonly
            ConcurrencyRateLimiter<TKey>? _limiter;

        private readonly
            ConcurrencyLeaseContext? _context;

        public ConcurrencyLease(
            bool isAcquired,
            ConcurrencyRateLimiter<TKey>? limiter,
            ConcurrencyLeaseContext? context)
        {
            IsAcquired = isAcquired;
            _limiter = limiter;
            _context = context;
        }

        public override bool IsAcquired { get; }

        public override IEnumerable<string> MetadataNames =>
            s_allMetadataNames;

        public override bool TryGetMetadata(
            string metadataName,
            out object? metadata)
        {
            if (metadataName == "Limit" && _context is not null)
            {
                metadata = _context.Limit;
                return true;
            }

            if (metadataName == "Remaining" && _context is not null)
            {
                metadata =
                    _context.Limit - _context.Count;

                return true;
            }

            metadata = null;

            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_context != null)
            {
                _limiter?.Release(_context);
            }
        }
    }

    private sealed class AdmissionTicket
    {
        public AdmissionTicket(string requestId)
        {
            RequestId = requestId;
            Completion = new TaskCompletionSource<ConcurrencyResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public string RequestId { get; }

        public TaskCompletionSource<ConcurrencyResponse> Completion { get; }
    }

    private sealed class Request
    {
        public CancellationToken CancellationToken
        {
            get;
            set;
        }

        public ConcurrencyLeaseContext? LeaseContext
        {
            get;
            set;
        }

        public TaskCompletionSource<RateLimitLease>?
            TaskCompletionSource
        {
            get;
            set;
        }

        public CancellationTokenRegistration
            CancellationTokenRegistration
        {
            get;
            set;
        }
    }
}