using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NCache.OSS.RateLimiting;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.RateLimiting;

// ---------------------------------------------------------------------------
// Corner Pizzeria - web app.
//
// Only ONE real limit now: 10 pizzas / 5 minutes, combined across every
// customer. It's enforced by a single NCache-backed named policy
// ("global-orders") that every order request passes through.
//
// Customers can still be picked in the UI (just so orders/history are
// grouped per person), but there is no per-customer cap anymore.
// ---------------------------------------------------------------------------

var customers = new[] { "Alice", "Bob", "Carol", "Dave", "Erin" };
const int GlobalLimit = 10;
var globalWindow = TimeSpan.FromMinutes(5);

// In-memory history, kept ONLY for the UI display. It is populated purely
// as a side effect of a request that already passed the real NCache check
// below, so what it shows always matches what NCache enforced.
var customerHistory = new ConcurrentDictionary<string, Queue<OrderRecord>>();
var globalHistory = new Queue<OrderRecord>();
var globalLock = new object();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5170");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddNCacheFixedWindowLimiter("global-orders", opt =>
    {
        opt.PermitLimit = GlobalLimit;
        opt.Window = globalWindow;
        opt.CacheName = "democache";
    });

    options.OnRejected = async (context, token) =>
    {
        var retryAfterSeconds = 0;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            retryAfterSeconds = (int)retryAfter.TotalSeconds;

        context.HttpContext.Response.ContentType = "application/json";
        var payload = JsonSerializer.Serialize(new { success = false, retryAfterSeconds });
        await context.HttpContext.Response.WriteAsync(payload, token);
    };
});

var app = builder.Build();
app.UseRateLimiter();

foreach (var customer in customers)
{
    app.MapPost($"/order/{customer}", () =>
    {
        var record = new OrderRecord(DateTime.UtcNow, Guid.NewGuid().ToString()[..8].ToUpper());

        var queue = customerHistory.GetOrAdd(customer, _ => new Queue<OrderRecord>());
        lock (queue) { queue.Enqueue(record); }
        lock (globalLock) { globalHistory.Enqueue(record); }

        return Results.Ok(new { success = true, message = $"Order confirmed for {customer}", orderId = record.OrderId });
    }).RequireRateLimiting("global-orders");
}

app.MapGet("/state/{customer}", (string customer) =>
{
    var now = DateTime.UtcNow;

    var custQueue = customerHistory.GetOrAdd(customer, _ => new Queue<OrderRecord>());
    List<OrderRecord> custOrders;
    lock (custQueue) { custOrders = custQueue.ToList(); }

    List<OrderRecord> globalActive;
    lock (globalLock)
    {
        Trim(globalHistory, now - globalWindow);
        globalActive = globalHistory.ToList();
    }

    var globalRemaining = Math.Max(0, GlobalLimit - globalActive.Count);
    var globalRetry = globalRemaining > 0 || globalActive.Count == 0
        ? 0
        : (int)Math.Max(0, (globalActive[0].Timestamp + globalWindow - now).TotalSeconds);

    return Results.Ok(new
    {
        customer,
        customerOrders = custOrders.OrderByDescending(o => o.Timestamp)
            .Select(o => new { o.OrderId, ageSeconds = (int)(now - o.Timestamp).TotalSeconds }),
        globalUsed = globalActive.Count,
        globalLimit = GlobalLimit,
        globalRemaining,
        globalRetrySeconds = globalRetry
    });
});

app.MapGet("/", () => Results.Text(Html, "text/html"));

app.Run();

static void Trim(Queue<OrderRecord> queue, DateTime cutoff)
{
    while (queue.Count > 0 && queue.Peek().Timestamp < cutoff)
        queue.Dequeue();
}

record OrderRecord(DateTime Timestamp, string OrderId);

partial class Program
{
    public const string Html = """
    <!DOCTYPE html>
    <html lang="en">
    <head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Corner Pizzeria</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
            background: #1a1a2e;
            color: #f0f0f0;
            min-height: 100vh;
            display: flex;
            justify-content: center;
            padding: 3rem 1rem;
        }
        .card {
            width: 100%;
            max-width: 480px;
            background: #232342;
            border: 1px solid #33335a;
            border-radius: 16px;
            padding: 2rem;
        }
        header { text-align: center; margin-bottom: 2rem; }
        h1 { font-size: 1.6rem; margin-bottom: 0.4rem; }
        .subtitle { color: #a0a0b0; font-size: 0.9rem; }
        .customer-bar { display: flex; gap: 0.5rem; justify-content: center; flex-wrap: wrap; margin-bottom: 2rem; }
        .chip {
            padding: 0.5rem 1rem;
            border-radius: 10px;
            border: 1px solid #33335a;
            background: #1a1a2e;
            color: #a0a0b0;
            cursor: pointer;
            font-size: 0.9rem;
        }
        .chip.active { border-color: #ff6b6b; color: #fff; background: #33203a; }
        .box { background: #1a1a2e; border-radius: 12px; padding: 1.25rem; margin-bottom: 1rem; border: 1px solid #2b2b4a; }
        .row { display: flex; justify-content: space-between; align-items: baseline; margin-bottom: 0.5rem; }
        .row .label { color: #a0a0b0; font-size: 0.85rem; }
        .row .value { font-weight: 700; }
        .bar-track { height: 10px; border-radius: 6px; background: #2b2b4a; overflow: hidden; }
        .bar-fill { height: 100%; background: #ff6b6b; }
        .bar-fill.ok { background: #6bcb77; }
        .order-btn {
            width: 100%;
            padding: 1rem;
            border: none;
            border-radius: 12px;
            background: #ff6b6b;
            color: #fff;
            font-size: 1.05rem;
            font-weight: 700;
            cursor: pointer;
            margin-bottom: 1rem;
        }
        .order-btn:disabled { background: #444; cursor: not-allowed; color: #999; }
        .message { text-align: center; font-size: 0.9rem; margin-bottom: 1rem; min-height: 1.2rem; }
        .message.success { color: #6bcb77; }
        .message.error { color: #ff6b6b; }
        .feed-header { font-size: 0.8rem; color: #a0a0b0; text-transform: uppercase; margin-bottom: 0.5rem; }
        .feed-item {
            display: flex; justify-content: space-between;
            padding: 0.6rem 0.8rem;
            background: #1a1a2e;
            border: 1px solid #2b2b4a;
            border-radius: 8px;
            margin-bottom: 0.4rem;
            font-size: 0.85rem;
        }
        .empty { text-align: center; color: #666; padding: 1rem; font-size: 0.85rem; }
    </style>
    </head>
    <body>
    <div class="card">
        <header>
            <h1>Corner Pizzeria</h1>
            <p class="subtitle">10 pizzas / 5 min combined, across every customer</p>
        </header>

        <div class="customer-bar" id="customerBar"></div>

        <div class="box">
            <div class="row"><span class="label">Combined (all customers)</span><span class="value" id="globalText">0 / 10</span></div>
            <div class="bar-track"><div class="bar-fill" id="globalBar" style="width:0%"></div></div>
        </div>

        <button class="order-btn" id="orderBtn">Order a Pizza</button>
        <div class="message" id="message"></div>

        <div class="box" id="timerBox" style="display:none;">
            <div class="row">
                <span class="label">Resets in</span>
                <span class="value" id="timerText">--:--</span>
            </div>
        </div>

        <div class="feed-header">Your recent orders</div>
        <div id="feed"></div>
    </div>

    <script>
        const CUSTOMERS = ['Alice', 'Bob', 'Carol', 'Dave', 'Erin'];
        let selected = CUSTOMERS[0];
        let localRetry = 0;

        function formatTime(totalSeconds) {
            const m = Math.floor(totalSeconds / 60);
            const s = totalSeconds % 60;
            return `${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
        }

        function renderTimer() {
            const box = document.getElementById('timerBox');
            if (localRetry > 0) {
                box.style.display = 'block';
                document.getElementById('timerText').textContent = formatTime(localRetry);
            } else {
                box.style.display = 'none';
            }
        }

        function renderCustomers() {
            document.getElementById('customerBar').innerHTML = CUSTOMERS.map(c =>
                `<div class="chip ${c === selected ? 'active' : ''}" onclick="selectCustomer('${c}')">${c}</div>`
            ).join('');
        }

        function selectCustomer(name) {
            selected = name;
            renderCustomers();
            document.getElementById('message').textContent = '';
            refreshState();
        }

        async function refreshState() {
            const res = await fetch(`/state/${selected}`);
            const s = await res.json();

            document.getElementById('globalText').textContent = `${s.globalUsed} / ${s.globalLimit}`;
            const globalBar = document.getElementById('globalBar');
            globalBar.style.width = `${(s.globalUsed / s.globalLimit) * 100}%`;
            globalBar.className = 'bar-fill' + (s.globalRemaining > 0 ? ' ok' : '');

            document.getElementById('orderBtn').disabled = s.globalRemaining === 0;

            localRetry = s.globalRetrySeconds;
            renderTimer();

            const feed = document.getElementById('feed');
            if (s.customerOrders.length === 0) {
                feed.innerHTML = '<div class="empty">No orders yet.</div>';
            } else {
                feed.innerHTML = s.customerOrders.map(o =>
                    `<div class="feed-item"><span>Pizza #${o.orderId}</span><span>${o.ageSeconds}s ago</span></div>`
                ).join('');
            }
        }

        async function placeOrder() {
            const btn = document.getElementById('orderBtn');
            const msg = document.getElementById('message');
            btn.disabled = true;
            msg.textContent = '';
            msg.className = 'message';

            try {
                const res = await fetch(`/order/${selected}`, { method: 'POST' });
                const data = await res.json();

                if (res.ok && data.success) {
                    msg.textContent = data.message;
                    msg.classList.add('success');
                } else {
                    msg.textContent = data.retryAfterSeconds
                        ? `Combined limit reached. Try again in ${data.retryAfterSeconds}s.`
                        : 'Combined limit reached. Please wait.';
                    msg.classList.add('error');
                }
            } catch (e) {
                msg.textContent = 'Could not reach the server.';
                msg.classList.add('error');
            } finally {
                await refreshState();
            }
        }

        document.getElementById('orderBtn').addEventListener('click', placeOrder);

        renderCustomers();
        refreshState();
        setInterval(refreshState, 3000);
        setInterval(() => {
            if (localRetry > 0) {
                localRetry -= 1;
                renderTimer();
            }
        }, 1000);
    </script>
    </body>
    </html>
    """;
}