using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Ergonomy.Observability
{
    /// <summary>
    /// Internal HTTP endpoint exposing Prometheus text metrics. The central Prometheus server
    /// scrapes this URL directly (network/firewall rules allow the scrape). No persistence
    /// pipeline (Kafka/ClickHouse/SQLite) is used for observability metrics.
    /// </summary>
    public sealed class MetricsEndpoint : IDisposable
    {
        private readonly AgentMetrics _metrics;
        private readonly MachineIdentityLabels _identity;
        private readonly ILogger<MetricsEndpoint> _logger;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private bool _disposed;

        public MetricsEndpoint(
            AgentMetrics metrics,
            MachineIdentityLabels identity,
            ILogger<MetricsEndpoint> logger)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string BindUrl { get; private set; } = "127.0.0.1:9090";

        public void Start(int port)
        {
            if (_listener != null)
                return;

            // Prefer a wildcard bind so the central Prometheus server can scrape the Agent.
            bool bound = TryStart($"http://+:{port}/");

            if (!bound)
            {
                // Fall back to loopback so the Agent can still be scraped on the same machine.
                _logger.LogWarning(
                    "Metrics endpoint could not bind to wildcard prefix on port {Port}. " +
                    "Falling back to loopback. Apply a URL ACL / firewall rule for scraping.",
                    port);
                bound = TryStart($"http://127.0.0.1:{port}/");
            }

            if (!bound)
            {
                _logger.LogError(
                    "Metrics endpoint could not be started on any prefix; continuing without metrics.");
                _listener = null;
                return;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => ServeLoopAsync(_listener, _cts.Token), _cts.Token);
            _logger.LogInformation("Prometheus metrics endpoint listening on {Url}", BindUrl);
        }

        private bool TryStart(string prefix)
        {
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();
                _listener = listener;
                BindUrl = prefix;
                return true;
            }
            catch (HttpListenerException ex)
            {
                _logger.LogDebug("Metrics bind attempt to {Prefix} failed: {Message}", prefix, ex.Message);
                return false;
            }
        }

        private async Task ServeLoopAsync(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Metrics endpoint accept failed.");
                    continue;
                }

                _ = Task.Run(() => HandleRequest(ctx));
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url?.AbsolutePath ?? "/";
                bool isMetrics = path.Equals("/metrics", StringComparison.OrdinalIgnoreCase)
                                 || path.Equals("/stats/prometheus", StringComparison.OrdinalIgnoreCase)
                                 || path.Equals("/", StringComparison.OrdinalIgnoreCase);

                byte[] body;
                string contentType;
                int status;

                if (isMetrics)
                {
                    body = System.Text.Encoding.UTF8.GetBytes(
                        RenderWithIdentity(_metrics.RenderPrometheusText()));
                    contentType = "text/plain; version=0.0.4; charset=utf-8";
                    status = 200;
                }
                else
                {
                    body = System.Text.Encoding.UTF8.GetBytes("Not found.\n");
                    contentType = "text/plain; charset=utf-8";
                    status = 404;
                }

                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = contentType;
                ctx.Response.OutputStream.Write(body, 0, body.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Metrics request handling failed.");
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
            }
        }

        private string RenderWithIdentity(string body)
        {
            // Inject stable, low-cardinality identity labels (machine name, env, agent id).
            // No usernames, session ids, or message ids.
            return body
                + $"agent_info{{machine=\"{Escape(_identity.MachineName)}\",environment=\"{Escape(_identity.Environment)}\",agent_id=\"{Escape(_identity.AgentId)}\"}} 1\n";
        }

        private static string Escape(string v) =>
            v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            Stop();
            _cts?.Dispose();
            _listener?.Close();
        }
    }

    /// <summary>Stable identity labels used only in metrics (low-cardinality).</summary>
    public sealed class MachineIdentityLabels
    {
        public string MachineName { get; }
        public string Environment { get; }
        public string AgentId { get; }

        public MachineIdentityLabels(string machineName, string environment, string agentId)
        {
            MachineName = machineName;
            Environment = environment;
            AgentId = agentId;
        }
    }
}
