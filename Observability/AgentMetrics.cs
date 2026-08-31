using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ergonomy.Observability
{
    /// <summary>
    /// Minimal, thread-safe in-process metric registry for Prometheus text exposition.
    /// This intentionally does NOT use Kafka, SQLite, or ClickHouse for observability:
    /// the central Prometheus server scrapes the Agent over HTTP.
    /// Uses only low-cardinality labels (machine name / agent id / environment).
    /// </summary>
    public sealed class AgentMetrics
    {
        private readonly ConcurrentDictionary<string, MetricFamily> _families =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// شمارنده بدون برچسب را به اندازه مشخص افزایش می‌دهد.
        /// </summary>
        /// <param name="name">نام متریک پرومتئوس.</param>
        /// <param name="help">متن راهنمای متریک.</param>
        /// <param name="delta">مقدار افزایش.</param>
        /// <returns>دستگیره سبک شمارنده.</returns>
        public Counter IncrementCounter(string name, string help, long delta = 1)
        {
            var family = GetOrAddFamily(name, help, "counter");
            family.AddPoint(null, delta);
            return new Counter(family.Name);
        }

        /// <summary>
        /// شمارنده دارای برچسب کم‌کاردینالیتی را افزایش می‌دهد.
        /// </summary>
        /// <param name="name">نام متریک پرومتئوس.</param>
        /// <param name="help">متن راهنمای متریک.</param>
        /// <param name="delta">مقدار افزایش.</param>
        /// <param name="labels">برچسب‌های نقطه متریک.</param>
        /// <returns>دستگیره سبک شمارنده.</returns>
        public Counter IncrementCounter(
            string name, string help, long delta, IReadOnlyDictionary<string, string> labels)
        {
            var family = GetOrAddFamily(name, help, "counter");
            family.AddPoint(labels, delta);
            return new Counter(family.Name);
        }

        /// <summary>
        /// مقدار فعلی یک گیج بدون برچسب را جایگزین می‌کند.
        /// </summary>
        /// <param name="name">نام متریک.</param>
        /// <param name="help">متن راهنما.</param>
        /// <param name="value">مقدار جدید گیج.</param>
        public void SetGauge(string name, string help, double value)
        {
            var family = GetOrAddFamily(name, help, "gauge");
            family.AddPoint(null, value);
        }

        /// <summary>
        /// مقدار فعلی یک گیج دارای برچسب را جایگزین می‌کند.
        /// </summary>
        /// <param name="name">نام متریک.</param>
        /// <param name="help">متن راهنما.</param>
        /// <param name="value">مقدار جدید گیج.</param>
        /// <param name="labels">برچسب‌های نقطه متریک.</param>
        public void SetGauge(
            string name, string help, double value, IReadOnlyDictionary<string, string> labels)
        {
            var family = GetOrAddFamily(name, help, "gauge");
            family.AddPoint(labels, value);
        }

        /// <summary>
        /// مقدار گیج را به‌اندازه دلتا افزایش می‌دهد؛ برای گیج بدون برچسب استفاده می‌شود.
        /// </summary>
        /// <param name="name">نام متریک.</param>
        /// <param name="help">متن راهنما.</param>
        /// <param name="delta">مقدار افزایش.</param>
        public void IncrementGauge(string name, string help, double delta)
        {
            var family = GetOrAddFamily(name, help, "gauge");
            family.AddPoint(null, delta);
        }

        /// <summary>
        /// خانواده متریک را در صورت نبود ایجاد کرده و همان نمونه را برمی‌گرداند.
        /// </summary>
        /// <param name="name">نام خانواده.</param>
        /// <param name="help">متن راهنما.</param>
        /// <param name="type">نوع counter یا gauge.</param>
        /// <returns>خانواده متریک موجود یا جدید.</returns>
        private MetricFamily GetOrAddFamily(string name, string help, string type)
        {
            return _families.GetOrAdd(name, _ => new MetricFamily(name, help, type));
        }

        /// <summary>
        /// همه خانواده‌های متریک را به قالب متنی exposition پرومتئوس تبدیل می‌کند.
        /// </summary>
        /// <returns>بدنه متنی قابل اسکرپ.</returns>
        public string RenderPrometheusText()
        {
            var sb = new StringBuilder();
            foreach (var family in _families.Values.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                sb.Append("# HELP ").Append(family.Name).Append(' ').Append(family.Help).Append('\n');
                sb.Append("# TYPE ").Append(family.Name).Append(' ').Append(family.Type).Append('\n');

                foreach (var point in family.GetOrderedPoints())
                {
                    sb.Append(family.Name);
                    if (point.Labels != null && point.Labels.Count > 0)
                    {
                        sb.Append('{').Append(
                            string.Join(",", point.Labels.Select(kv =>
                                $"{kv.Key}=\"{EscapeLabel(kv.Value)}\""))).Append('}');
                    }
                    sb.Append(' ').Append(point.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .Append('\n');
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// نویسه‌های ویژه برچسب پرومتئوس را برای جلوگیری از شکستن قالب متنی escape می‌کند.
        /// </summary>
        /// <param name="value">مقدار خام برچسب.</param>
        /// <returns>مقدار امن برای خروجی.</returns>
        private static string EscapeLabel(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        }

        private sealed class MetricFamily
        {
            public string Name { get; }
            public string Help { get; }
            public string Type { get; }
            private readonly ConcurrentDictionary<string, MetricPoint> _points = new();

            /// <summary>
            /// یک خانواده متریک با نام، راهنما و نوع مشخص می‌سازد.
            /// </summary>
            /// <param name="name">نام خانواده.</param>
            /// <param name="help">متن راهنما.</param>
            /// <param name="type">نوع counter یا gauge.</param>
            public MetricFamily(string name, string help, string type)
            {
                Name = name;
                Help = help;
                Type = type;
            }

            /// <summary>
            /// نقطه متریک را بر اساس نوع خانواده جمع یا جایگزین می‌کند.
            /// </summary>
            /// <param name="labels">برچسب‌های اختیاری نقطه.</param>
            /// <param name="value">مقدار افزایش یا جایگزینی.</param>
            public void AddPoint(IReadOnlyDictionary<string, string>? labels, double value)
            {
                string key = labels == null
                    ? string.Empty
                    : string.Join("|", labels.Select(kv => kv.Key + "=" + kv.Value).OrderBy(k => k));

                if (Type == "gauge")
                {
                    // Gauges overwrite the current value.
                    _points.AddOrUpdate(
                        key,
                        _ => new MetricPoint(labels, value),
                        (_, existing) => { existing.Set(value); return existing; });
                }
                else
                {
                    // Counters accumulate.
                    _points.AddOrUpdate(
                        key,
                        _ => new MetricPoint(labels, value),
                        (_, existing) => { existing.Add(value); return existing; });
                }
            }

            /// <summary>
            /// نقاط خانواده را با ترتیب پایدار کلید برچسب برای خروجی پرومتئوس برمی‌گرداند.
            /// </summary>
            /// <returns>نقاط مرتب‌شده خانواده.</returns>
            public IEnumerable<MetricPoint> GetOrderedPoints() =>
                _points.Values.OrderBy(p => p.LabelsKey, StringComparer.Ordinal);
        }

        private sealed class MetricPoint
        {
            private double _value;
            private readonly object _sync = new();
            public IReadOnlyDictionary<string, string>? Labels { get; }
            public string LabelsKey { get; }
            public double Value { get { lock (_sync) return _value; } }

            /// <summary>
            /// یک نقطه متریک با مقدار اولیه و کلید پایدار برچسب می‌سازد.
            /// </summary>
            /// <param name="labels">برچسب‌های نقطه.</param>
            /// <param name="value">مقدار اولیه.</param>
            public MetricPoint(IReadOnlyDictionary<string, string>? labels, double value)
            {
                Labels = labels;
                LabelsKey = labels == null
                    ? string.Empty
                    : string.Join("|", labels.Select(kv => kv.Key + "=" + kv.Value).OrderBy(k => k));
                _value = value;
            }

            /// <summary>
            /// مقدار نقطه شمارنده را به‌صورت امن افزایش می‌دهد.
            /// </summary>
            /// <param name="delta">مقدار افزایش.</param>
            public void Add(double delta)
            {
                lock (_sync) _value += delta;
            }

            /// <summary>
            /// مقدار نقطه گیج را به‌صورت امن جایگزین می‌کند.
            /// </summary>
            /// <param name="value">مقدار جدید.</param>
            public void Set(double value)
            {
                lock (_sync) _value = value;
            }
        }
    }

    public readonly struct Counter
    {
        public string Name { get; }
        /// <summary>
        /// دستگیره سبک یک شمارنده ثبت‌شده را می‌سازد.
        /// </summary>
        /// <param name="name">نام شمارنده.</param>
        public Counter(string name) => Name = name;
    }
}
