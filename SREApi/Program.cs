using Prometheus;

var builder = WebApplication.CreateBuilder(args);
// This is equivalent to: app = FastAPI()
// builder is like configuring your FastAPI app before starting it

var app = builder.Build();

// Equivalent to FastAPI middleware: app.add_middleware(...)
// This auto-instruments all HTTP requests with Prometheus metrics
app.UseHttpMetrics();

// ---- Custom Metrics ----
// In Python prometheus_client: Counter('requests_total', ...)
// prometheus-net uses the same mental model but with C# static fields

var requestCounter = Metrics.CreateCounter(
    "sre_requests_total",
    "Total number of requests",
    new CounterConfiguration { LabelNames = new[] { "endpoint", "status" } }
);

var requestDuration = Metrics.CreateHistogram(
    "sre_request_duration_seconds",
    "Request duration in seconds",
    new HistogramConfiguration
    {
        LabelNames = new[] { "endpoint" },
        Buckets = new[] { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.5 }
    }
);

// SLO target (99.5% availability) — a Gauge is a value that can go up or down
// Python equivalent: Gauge('slo_target', ...)
var sloTarget = Metrics.CreateGauge("sre_slo_target", "SLO target (0-1)");
var currentSli = Metrics.CreateGauge("sre_current_sli", "Current SLI measurement (0-1)");
var errorBudgetRemaining = Metrics.CreateGauge(
    "sre_error_budget_remaining_ratio",
    "Remaining error budget as ratio of total budget"
);

// Initialize static values
sloTarget.Set(0.995); // 99.5% SLO
errorBudgetRemaining.Set(1.0); // Start with full budget

// ---- Rolling window state for SLI calculation ----
// Keep last 1000 requests in memory to compute live SLI
var recentRequests = new Queue<bool>(); // true = success
var lockObj = new object();
const int windowSize = 1000;

// status is now "success", "client_error" (4xx), or "server_error" (5xx)
// Only server_error counts against the SLI — client errors are the caller's fault
void RecordRequest(string status, string endpoint, double durationSeconds)
{
    var isServerError = status == "server_error";

    lock (lockObj)
    {
        recentRequests.Enqueue(!isServerError);
        if (recentRequests.Count > windowSize)
            recentRequests.Dequeue();

        var successCount = recentRequests.Count(x => x);
        var sli = recentRequests.Count > 0 ? (double)successCount / recentRequests.Count : 1.0;
        currentSli.Set(sli);

        var sloValue = 0.995;
        var errorBudgetTotal = 1.0 - sloValue; // 0.005
        var errorRate = 1.0 - sli;
        var budgetUsedRatio = errorBudgetTotal > 0 ? errorRate / errorBudgetTotal : 0;
        errorBudgetRemaining.Set(Math.Max(0, 1.0 - budgetUsedRatio));
    }

    requestCounter.WithLabels(endpoint, status).Inc();
    requestDuration.WithLabels(endpoint).Observe(durationSeconds);
}

// ---- API Routes ---- (equivalent to FastAPI @app.get decorators)

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.MapGet("/api/orders", async () =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await Task.Delay(Random.Shared.Next(10, 80)); // simulate DB latency
    sw.Stop();
    RecordRequest("success", "/api/orders", sw.Elapsed.TotalSeconds);
    return Results.Ok(new { orders = Enumerable.Range(1, 10).Select(i => new { id = i, item = $"Item {i}" }) });
});

app.MapGet("/api/orders/{id}", async (int id) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await Task.Delay(Random.Shared.Next(5, 50));
    sw.Stop();
    
    // Simulate a not-found rate
    if (id > 100)
    {
        // 404 = client_error — they asked for something that doesn't exist
        // this does NOT count against our SLI, it's the caller's fault
        RecordRequest("client_error", "/api/orders/{id}", sw.Elapsed.TotalSeconds);
        return Results.NotFound(new { error = "Order not found", id });
    }

    RecordRequest("success", "/api/orders/{id}", sw.Elapsed.TotalSeconds);
    return Results.Ok(new { id, item = $"Item {id}", status = "shipped" });
});

app.MapPost("/api/orders", async (HttpContext ctx) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await Task.Delay(Random.Shared.Next(20, 150)); // writes are slower
    sw.Stop();
    
    // 5% error rate on creation — simulates downstream failures
    if (Random.Shared.NextDouble() < 0.05)
    {
        // 503 = server_error — our downstream dependency failed, this IS our fault
        RecordRequest("server_error", "/api/orders", sw.Elapsed.TotalSeconds);
        return Results.StatusCode(503);
    }

    RecordRequest("success", "/api/orders", sw.Elapsed.TotalSeconds);
    return Results.Created("/api/orders/99", new { id = 99, status = "created" });
});

// The /chaos endpoint lets Locust deliberately spike error rates
app.MapGet("/api/chaos", async () =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await Task.Delay(Random.Shared.Next(100, 500)); // slow and unreliable
    sw.Stop();
    
    if (Random.Shared.NextDouble() < 0.40) // 40% error rate
    {
        RecordRequest("server_error", "/api/chaos", sw.Elapsed.TotalSeconds);
        return Results.StatusCode(500);
    }

    RecordRequest("success", "/api/chaos", sw.Elapsed.TotalSeconds);
    return Results.Ok(new { status = "survived chaos" });
});

// This is the endpoint Prometheus scrapes — equivalent to expose_metrics() in Python
app.MapMetrics(); // mounts at /metrics by default

app.Run();