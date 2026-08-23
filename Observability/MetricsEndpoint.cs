using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Observability
{
    /// <summary>HTTP Prometheus endpoint with tracked request draining on shutdown.</summary>
    public sealed class MetricsEndpoint : IDisposable
    {
        private readonly AgentMetrics _metrics;
        private readonly MachineIdentityLabels _identity;
        private readonly ILogger<MetricsEndpoint> _logger;
        private readonly ConcurrentDictionary<int, Task> _requests = new();
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private int _requestId;
        private bool _disposed;
        public string BindUrl { get; private set; } = "127.0.0.1:9090";

        public MetricsEndpoint(AgentMetrics metrics, MachineIdentityLabels identity, ILogger<MetricsEndpoint> logger)
        { _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics)); _identity = identity ?? throw new ArgumentNullException(nameof(identity)); _logger = logger ?? throw new ArgumentNullException(nameof(logger)); }

        public void Start(int port)
        {
            if (_listener != null) return;
            bool bound = TryStart($"http://+:{port}/");
            if (!bound) { _logger.LogWarning("Metrics wildcard bind failed; using loopback. Port={Port}", port); bound = TryStart($"http://127.0.0.1:{port}/"); }
            if (!bound) { _logger.LogError("Metrics endpoint failed to start."); return; }
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => ServeLoopAsync(_listener!, _cts.Token));
            _logger.LogInformation("Prometheus metrics endpoint listening. Url={Url}", BindUrl);
        }
        private bool TryStart(string prefix)
        {
            try { var l = new HttpListener(); l.Prefixes.Add(prefix); l.Start(); _listener = l; BindUrl = prefix; return true; }
            catch (HttpListenerException ex) { _logger.LogDebug(ex, "Metrics bind failed."); return false; }
        }
        private async Task ServeLoopAsync(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync().ConfigureAwait(false);
                    TrackRequest(HandleRequestAsync(context));
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Metrics endpoint accept failed."); }
            }
        }
        private void TrackRequest(Task request)
        {
            int id = Interlocked.Increment(ref _requestId);
            _requests[id] = request;
            request.ContinueWith(completed => { _requests.TryRemove(id, out Task? ignored); }, TaskScheduler.Default);
        }
        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url?.AbsolutePath ?? "/";
                bool metrics = path.Equals("/metrics", StringComparison.OrdinalIgnoreCase) || path.Equals("/stats/prometheus", StringComparison.OrdinalIgnoreCase) || path == "/";
                byte[] body = System.Text.Encoding.UTF8.GetBytes(metrics ? RenderWithIdentity(_metrics.RenderPrometheusText()) : "Not found.\n");
                ctx.Response.StatusCode = metrics ? 200 : 404;
                ctx.Response.ContentType = metrics ? "text/plain; version=0.0.4; charset=utf-8" : "text/plain; charset=utf-8";
                await ctx.Response.OutputStream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogError(ex, "Metrics request handling failed."); }
            finally { try { ctx.Response.Close(); } catch { } }
        }
        private string RenderWithIdentity(string body) => body + $"agent_info{{machine=\"{Escape(_identity.MachineName)}\",environment=\"{Escape(_identity.Environment)}\",agent_id=\"{Escape(_identity.AgentId)}\"}} 1\n";
        private static string Escape(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        public void Stop()
        {
            _cts?.Cancel(); try { _listener?.Stop(); _listener?.Close(); } catch { }
            try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
            Task[] active = _requests.Values.ToArray();
            try { Task.WaitAll(active, TimeSpan.FromSeconds(3)); } catch { }
        }
        public void Dispose() { if (_disposed) return; _disposed = true; Stop(); _cts?.Dispose(); }
    }
    public sealed class MachineIdentityLabels
    {
        public string MachineName { get; } public string Environment { get; } public string AgentId { get; }
        public MachineIdentityLabels(string machineName, string environment, string agentId) { MachineName = machineName; Environment = environment; AgentId = agentId; }
    }
}
