using System.Text.RegularExpressions;
using Xunit;

namespace Ergonomy.Core.Tests
{
    /// <summary>
    /// CI gate for architecture rule ERGONOMY001: empty or comment-only catch
    /// bodies are forbidden. E0 sites must call ExceptionPolicy.IgnoreIfShuttingDown or
    /// IgnoreBestEffortDispose. No extra Roslyn package is required.
    /// </summary>
    public sealed class Ergonomy001EmptyCatchTests
    {
        private static readonly Regex CatchKeyword = new(@"\bcatch\b", RegexOptions.Compiled);

        [Fact]
        public void ERGONOMY001_forbids_empty_or_comment_only_catch_blocks()
        {
            string? root = FindRepoRoot();
            Assert.True(root != null, "Could not locate Ergonomy.sln from the test output directory.");

            var violations = new List<string>();
            foreach (string file in Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string text = File.ReadAllText(file);
                foreach ((int line, string inner) in FindEmptyCatchBodies(text))
                {
                    string rel = Path.GetRelativePath(root!, file);
                    violations.Add($"{rel}:{line}: ERGONOMY001 empty catch body `{TrimOneLine(inner)}`");
                }
            }

            Assert.True(
                violations.Count == 0,
                "ERGONOMY001 empty catch blocks must be replaced with ExceptionPolicy helpers:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
        }

        internal static IEnumerable<(int Line, string Inner)> FindEmptyCatchBodies(string text)
        {
            int searchFrom = 0;
            while (true)
            {
                Match match = CatchKeyword.Match(text, searchFrom);
                if (!match.Success)
                    yield break;

                int start = match.Index;
                if (IsInsideCommentOrString(text, start))
                {
                    searchFrom = start + 5;
                    continue;
                }

                int brace = text.IndexOf('{', start);
                if (brace < 0)
                    yield break;

                int end = FindMatchingBrace(text, brace);
                if (end < 0)
                    yield break;

                string inner = text.Substring(brace + 1, end - brace - 1);
                if (IsEmptyCatchBody(inner))
                {
                    int line = text.Take(start).Count(c => c == '\n') + 1;
                    yield return (line, inner);
                }

                searchFrom = end + 1;
            }
        }

        private static bool IsInsideCommentOrString(string text, int index)
        {
            bool inString = false;
            bool verbatim = false;
            bool inChar = false;
            bool inBlockComment = false;
            bool inLineComment = false;
            for (int i = 0; i < index; i++)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (inLineComment)
                {
                    if (c == '\n') inLineComment = false;
                    continue;
                }
                if (inBlockComment)
                {
                    if (c == '*' && next == '/') { inBlockComment = false; i++; }
                    continue;
                }
                if (inString)
                {
                    if (verbatim)
                    {
                        if (c == '"' && next == '"') { i++; continue; }
                        if (c == '"') { inString = false; verbatim = false; }
                        continue;
                    }
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (inChar)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') inChar = false;
                    continue;
                }
                if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
                if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
                if (c == '@' && next == '"') { inString = true; verbatim = true; i++; continue; }
                if (c == '"') { inString = true; continue; }
                if (c == '\'') { inChar = true; continue; }
            }

            return inString || inChar || inBlockComment || inLineComment;
        }

        internal static bool IsEmptyCatchBody(string inner)
        {
            string withoutComments = Regex.Replace(inner, @"//.*?$", string.Empty, RegexOptions.Multiline);
            withoutComments = Regex.Replace(withoutComments, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return string.IsNullOrWhiteSpace(withoutComments);
        }

        private static int FindMatchingBrace(string text, int openIndex)
        {
            int depth = 0;
            bool inString = false;
            bool inChar = false;
            bool verbatim = false;
            for (int i = openIndex; i < text.Length; i++)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';

                if (inString)
                {
                    if (verbatim)
                    {
                        if (c == '"' && next == '"') { i++; continue; }
                        if (c == '"') { inString = false; verbatim = false; }
                        continue;
                    }

                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }

                if (inChar)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') inChar = false;
                    continue;
                }

                if (c == '/' && next == '/')
                {
                    i = text.IndexOf('\n', i);
                    if (i < 0) return -1;
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0) return -1;
                    i = close + 1;
                    continue;
                }

                if (c == '@' && next == '"') { inString = true; verbatim = true; i++; continue; }
                if (c == '"') { inString = true; continue; }
                if (c == '\'') { inChar = true; continue; }

                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }

        private static string? FindRepoRoot()
        {
            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "Ergonomy.sln")))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }

            return null;
        }

        private static string TrimOneLine(string inner)
            => Regex.Replace(inner, @"\s+", " ").Trim();
    }
}
