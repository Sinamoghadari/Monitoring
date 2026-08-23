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

        public Counter IncrementCounter(string name, string help, long delta = 1)
        {
            var family = GetOrAddFamily(name, help, "counter");
            family.AddPoint(null, delta);
            return new Counter(family.Name);
        }

        public Counter IncrementCounter(
            string name, string help, long delta, IReadOnlyDictionary<string, string> labels)
        {
            var family = GetOrAddFamily(name, help, "counter");
            family.AddPoint(labels, delta);
            return new Counter(family.Name);
        }

        public void SetGauge(string name, string help, double value)
        {
            var family = GetOrAddFamily(name, help, "gauge");
            family.AddPoint(null, value);
        }

        public void SetGauge(
            string name, string help, double value, IReadOnlyDictionary<string, string> labels)
        {
            var family = GetOrAddFamily(name, help, "gauge");
            family.AddPoint(labels, value);
        }

        public void IncrementGauge(string name, string help, double delta)
        {
            var family = GetOrAddFamily(name, help, "gauge");
            family.AddPoint(null, delta);
        }

        private MetricFamily GetOrAddFamily(string name, string help, string type)
        {
            return _families.GetOrAdd(name, _ => new MetricFamily(name, help, type));
        }

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

            public MetricFamily(string name, string help, string type)
            {
                Name = name;
                Help = help;
                Type = type;
            }

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

            public MetricPoint(IReadOnlyDictionary<string, string>? labels, double value)
            {
                Labels = labels;
                LabelsKey = labels == null
                    ? string.Empty
                    : string.Join("|", labels.Select(kv => kv.Key + "=" + kv.Value).OrderBy(k => k));
                _value = value;
            }

            public void Add(double delta)
            {
                lock (_sync) _value += delta;
            }

            public void Set(double value)
            {
                lock (_sync) _value = value;
            }
        }
    }

    public readonly struct Counter
    {
        public string Name { get; }
        public Counter(string name) => Name = name;
    }
}
