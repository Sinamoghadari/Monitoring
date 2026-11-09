using System.Text;
using Xunit;

namespace Ergonomy.Core.Tests
{
    /// <summary>
    /// ERGONOMY001: empty catch blocks are forbidden in the Phase 0–2 surface.
    /// Call ExceptionPolicy.Report / IgnoreIfShuttingDown / IgnoreBestEffortDispose instead.
    /// Comments and string literals are skipped so documentation cannot trip the rule.
    /// </summary>
    public sealed class Ergonomy001Tests
    {
        private static readonly string[] Scoped =
        {
            "Ergonomy.Core/Diagnostics",
            "Ergonomy.Core/Hosting",
            "Ergonomy.Core/Ipc",
            "Ergonomy.Service/Program.cs",
            "Ergonomy.Service/Hosting",
            "Ergonomy.Task/Program.cs",
            "Program.cs",
            "LocalDatabaseManager.cs",
            "SyncEngine.cs",
            "AdvancedMetricsCollector.cs",
            "SqliteOutboxConnectionProvider.cs",
            "Observability/MetricsEndpoint.cs",
            "Services/WorkerBase.cs",
            "Services/AdvancedMetricsWorker.cs",
            "Services/ErrorOnlyAppLogLoggerProvider.cs",
        };

        [Fact]
        public void Phase02_files_have_no_empty_catch_blocks()
        {
            string root = FindRepoRoot();
            var failures = new List<string>();

            foreach (string cs in EnumerateScoped(root))
            {
                foreach (string hit in FindEmptyCatches(cs, File.ReadAllText(cs)))
                    failures.Add(hit);
            }

            Assert.True(
                failures.Count == 0,
                "ERGONOMY001 empty catch blocks:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }

        private static IEnumerable<string> EnumerateScoped(string root)
        {
            foreach (string relative in Scoped)
            {
                string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                {
                    yield return path;
                    continue;
                }

                if (!Directory.Exists(path))
                    continue;

                foreach (string file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
                    yield return file;
            }
        }

        private static IEnumerable<string> FindEmptyCatches(string path, string text)
        {
            string stripped = StripCommentsAndStrings(text);
            int i = 0;
            while (i < stripped.Length)
            {
                int catchAt = IndexOfCatchKeyword(stripped, i);
                if (catchAt < 0)
                    yield break;

                int brace = stripped.IndexOf('{', catchAt);
                if (brace < 0)
                    yield break;

                int end = MatchingBrace(stripped, brace);
                if (end < 0)
                    yield break;

                string inner = stripped.Substring(brace + 1, end - brace - 1);
                if (string.IsNullOrWhiteSpace(inner))
                {
                    int line = stripped.Take(catchAt).Count(c => c == '\n') + 1;
                    yield return $"{Path.GetFileName(path)}:{line}";
                }

                i = end + 1;
            }
        }

        private static int IndexOfCatchKeyword(string text, int start)
        {
            int i = start;
            while (i < text.Length)
            {
                int at = text.IndexOf("catch", i, StringComparison.Ordinal);
                if (at < 0)
                    return -1;
                bool leftOk = at == 0 || !IsIdent(text[at - 1]);
                bool rightOk = at + 5 >= text.Length || !IsIdent(text[at + 5]);
                if (leftOk && rightOk)
                    return at;
                i = at + 5;
            }
            return -1;
        }

        private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static int MatchingBrace(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }
            return -1;
        }

        private static string StripCommentsAndStrings(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';

                if (c == '/' && next == '/')
                {
                    sb.Append(' ');
                    sb.Append(' ');
                    i += 2;
                    while (i < text.Length && text[i] != '\n')
                    {
                        sb.Append(text[i] == '\r' ? '\r' : ' ');
                        i++;
                    }
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    sb.Append(' ');
                    sb.Append(' ');
                    i += 2;
                    while (i < text.Length)
                    {
                        if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/')
                        {
                            sb.Append(' ');
                            sb.Append(' ');
                            i += 2;
                            break;
                        }
                        sb.Append(text[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                    continue;
                }

                if (c == '@' && next == '"')
                {
                    sb.Append(' ');
                    sb.Append(' ');
                    i += 2;
                    while (i < text.Length)
                    {
                        if (text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"')
                        {
                            sb.Append(' ');
                            sb.Append(' ');
                            i += 2;
                            continue;
                        }
                        if (text[i] == '"')
                        {
                            sb.Append(' ');
                            i++;
                            break;
                        }
                        sb.Append(text[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                    continue;
                }

                if (c == '"')
                {
                    sb.Append(' ');
                    i++;
                    while (i < text.Length)
                    {
                        if (text[i] == '\\' && i + 1 < text.Length)
                        {
                            sb.Append(' ');
                            sb.Append(' ');
                            i += 2;
                            continue;
                        }
                        if (text[i] == '"')
                        {
                            sb.Append(' ');
                            i++;
                            break;
                        }
                        sb.Append(text[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                    continue;
                }

                if (c == '\'')
                {
                    sb.Append(' ');
                    i++;
                    while (i < text.Length)
                    {
                        if (text[i] == '\\' && i + 1 < text.Length)
                        {
                            sb.Append(' ');
                            sb.Append(' ');
                            i += 2;
                            continue;
                        }
                        if (text[i] == '\'')
                        {
                            sb.Append(' ');
                            i++;
                            break;
                        }
                        sb.Append(' ');
                        i++;
                    }
                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Ergonomy.csproj")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate Ergonomy.csproj from " + AppContext.BaseDirectory);
        }
    }
}
