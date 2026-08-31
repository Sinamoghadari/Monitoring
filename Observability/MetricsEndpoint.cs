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

        /// <summary>
        /// نقطه پایانی HTTP پرومتئوس را با رجیستری متریک و برچسب هویت ماشین می‌سازد.
        /// </summary>
        /// <param name="metrics">رجیستری متریک درون‌فرایندی.</param>
        /// <param name="identity">برچسب‌های ماشین، محیط و شناسه عامل.</param>
        /// <param name="logger">ثبت‌کننده اتصال و خطای شنونده.</param>
        public MetricsEndpoint(AgentMetrics metrics, MachineIdentityLabels identity, ILogger<MetricsEndpoint> logger)
        { _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics)); _identity = identity ?? throw new ArgumentNullException(nameof(identity)); _logger = logger ?? throw new ArgumentNullException(nameof(logger)); }

        /// <summary>
        /// شنونده HTTP را ابتدا روی همه رابط‌ها و در صورت شکست ACL روی loopback شروع می‌کند.
        /// </summary>
        /// <param name="port">درگاه اسکرپ پرومتئوس.</param>
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
        /// <summary>
        /// تلاش می‌کند شنونده HttpListener را روی پیشوند داده‌شده متصل کند.
        /// </summary>
        /// <param name="prefix">پیشوند URL مانند http://+:9100/.</param>
        /// <returns>اگر اتصال موفق باشد true است.</returns>
        private bool TryStart(string prefix)
        {
            try { var l = new HttpListener(); l.Prefixes.Add(prefix); l.Start(); _listener = l; BindUrl = prefix; return true; }
            catch (HttpListenerException ex) { _logger.LogDebug(ex, "Metrics bind failed."); return false; }
        }
        /// <summary>
        /// حلقه ناهمگام پذیرش درخواست‌های HTTP را تا لغو یا بسته شدن شنونده ادامه می‌دهد.
        /// </summary>
        /// <param name="listener">شنونده فعال HTTP.</param>
        /// <param name="ct">توکن لغو حلقه.</param>
        /// <returns>وظیفه‌ای که تا توقف شنونده زنده است.</returns>
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
        /// <summary>
        /// درخواست در حال پردازش را ردیابی می‌کند تا هنگام خاموشی بتوان آن را کامل کرد.
        /// </summary>
        /// <param name="request">وظیفه پردازش درخواست.</param>
        private void TrackRequest(Task request)
        {
            int id = Interlocked.Increment(ref _requestId);
            _requests[id] = request;
            request.ContinueWith(completed => { _requests.TryRemove(id, out Task? ignored); }, TaskScheduler.Default);
        }
        /// <summary>
        /// مسیر /metrics را با متن پرومتئوس پاسخ می‌دهد و مسیرهای دیگر را 404 می‌کند.
        /// </summary>
        /// <param name="ctx">زمینه درخواست HTTP.</param>
        /// <returns>وظیفه‌ای که پس از نوشتن پاسخ کامل می‌شود.</returns>
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
        /// <summary>
        /// گیج هویت عامل را به انتهای خروجی پرومتئوس اضافه می‌کند.
        /// </summary>
        /// <param name="body">متن متریک‌های موجود.</param>
        /// <returns>متن کامل همراه با agent_info.</returns>
        private string RenderWithIdentity(string body) => body + $"agent_info{{machine=\"{Escape(_identity.MachineName)}\",environment=\"{Escape(_identity.Environment)}\",agent_id=\"{Escape(_identity.AgentId)}\"}} 1\n";
        /// <summary>
        /// نویسه‌های ویژه مقدار برچسب پرومتئوس را escape می‌کند.
        /// </summary>
        /// <param name="v">مقدار خام.</param>
        /// <returns>مقدار امن.</returns>
        private static string Escape(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        /// <summary>
        /// شنونده را متوقف کرده، حلقه پذیرش را لغو می‌کند و برای تخلیه درخواست‌های فعال منتظر می‌ماند.
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel(); try { _listener?.Stop(); _listener?.Close(); } catch { }
            try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
            Task[] active = _requests.Values.ToArray();
            try { Task.WaitAll(active, TimeSpan.FromSeconds(3)); } catch { }
        }
        /// <summary>
        /// نقطه پایانی متریک را متوقف کرده و توکن لغو را آزاد می‌کند.
        /// </summary>
        public void Dispose() { if (_disposed) return; _disposed = true; Stop(); _cts?.Dispose(); }
    }
    public sealed class MachineIdentityLabels
    {
        public string MachineName { get; } public string Environment { get; } public string AgentId { get; }

        /// <summary>
        /// برچسب‌های کم‌کاردینالیتی هویت عامل را برای خروجی پرومتئوس می‌سازد.
        /// </summary>
        /// <param name="machineName">نام ماشین.</param>
        /// <param name="environment">نام محیط.</param>
        /// <param name="agentId">شناسه عامل.</param>
        public MachineIdentityLabels(string machineName, string environment, string agentId) { MachineName = machineName; Environment = environment; AgentId = agentId; }
    }
}
