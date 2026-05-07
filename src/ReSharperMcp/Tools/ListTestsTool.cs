using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class ListTestsTool : IMcpTool
    {
        private readonly ISolution _solution;

        public ListTestsTool(ISolution solution) => _solution = solution;

        public string Name => "list_tests";

        public string Description =>
            "List test methods in the solution or in specific files. Detects common .NET test attributes " +
            "for xUnit ([Fact], [Theory]), NUnit ([Test], [TestCase], [TestCaseSource]), and MSTest " +
            "([TestMethod], [DataTestMethod]). Pass filePath to scan one file or filePaths for batch mode.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                filePath = new
                {
                    type = "string",
                    description =
                        "Optional absolute or solution-relative path to scan. If omitted, scans the whole solution."
                },
                filePaths = new
                {
                    type = "array",
                    description =
                        "Array of file paths to scan in batch. Results are concatenated with separators. Alternative to single 'filePath'.",
                    items = new { type = "string" }
                },
                framework = new
                    { type = "string", description = "Optional framework filter: 'xunit', 'nunit', or 'mstest'." },
                maxResults = new
                {
                    type = "integer", description = "Maximum number of tests to return. Default: 500, maximum: 2000."
                }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments)
        {
            var filePathsToken = arguments["filePaths"] as JArray;
            if (filePathsToken != null && filePathsToken.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < filePathsToken.Count; i++)
                {
                    if (i > 0) sb.AppendLine().AppendLine();
                    var itemArgs = new JObject { ["filePath"] = filePathsToken[i]?.ToString() };
                    CopyIfPresent(arguments, itemArgs, "framework");
                    CopyIfPresent(arguments, itemArgs, "maxResults");

                    sb.Append("=== [").Append(i + 1).Append('/').Append(filePathsToken.Count)
                        .Append("] ").Append(filePathsToken[i]).Append(" ===").AppendLine();
                    sb.Append(ResultToString(ExecuteSingle(itemArgs)));
                }

                return sb.ToString().TrimEnd();
            }

            return ExecuteSingle(arguments);
        }

        private object ExecuteSingle(JObject arguments)
        {
            var filePath = arguments["filePath"]?.ToString();
            var framework = arguments["framework"]?.ToString();
            var maxResults = arguments["maxResults"]?.Value<int>() ?? 500;

            if (maxResults <= 0) maxResults = 500;
            if (maxResults > 2000) maxResults = 2000;

            var frameworkFilter = ParseFrameworkFilter(framework);
            if (framework != null && frameworkFilter == null)
                return new { error = "framework must be one of: xunit, nunit, mstest" };

            var tests = new List<TestEntry>();
            var seen = new HashSet<string>();

            if (!string.IsNullOrEmpty(filePath))
            {
                var resolved = PsiHelpers.ResolveFile(_solution, filePath);
                if (!resolved.IsFound)
                    return new { error = resolved.Error };

                ScanSourceFile(resolved.SourceFile, resolved.ProjectFile?.GetProject()?.Name,
                    frameworkFilter, maxResults, tests, seen);
            }
            else
            {
                foreach (var project in _solution.GetAllProjects())
                {
                    foreach (var projectFile in project.GetAllProjectFiles())
                    {
                        foreach (var sourceFile in projectFile.ToSourceFiles())
                        {
                            ScanSourceFile(sourceFile, project.Name, frameworkFilter, maxResults, tests, seen);
                            if (tests.Count >= maxResults) break;
                        }

                        if (tests.Count >= maxResults) break;
                    }

                    if (tests.Count >= maxResults) break;
                }
            }

            tests = tests
                .OrderBy(t => t.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Line)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToList();

            return FormatResults(filePath, frameworkFilter, maxResults, tests);
        }

        private void ScanSourceFile(IPsiSourceFile sourceFile, string projectName, string frameworkFilter,
            int maxResults, List<TestEntry> tests, HashSet<string> seen)
        {
            if (sourceFile == null || tests.Count >= maxResults) return;

            var psiFile = PsiHelpers.GetPsiFile(sourceFile);
            if (psiFile == null) return;

            foreach (var declaration in psiFile.Descendants().OfType<IDeclaration>())
            {
                if (tests.Count >= maxResults) break;

                var method = declaration.DeclaredElement as IMethod;
                if (method == null || method.ShortName == ".ctor") continue;

                var testAttribute = TryGetTestAttribute(declaration.GetText(), frameworkFilter);
                if (testAttribute == null) continue;

                var range = TreeNodeExtensions.GetDocumentRange(declaration);
                if (!range.IsValid()) continue;

                var file = declaration.GetSourceFile()?.GetLocation().FullPath ?? sourceFile.GetLocation().FullPath;
                var position = PsiHelpers.GetLineColumn(range.StartOffset);
                var qualifiedName = PsiHelpers.GetQualifiedName(method);
                var key = $"{file}:{position.line}:{qualifiedName}";
                if (!seen.Add(key)) continue;

                tests.Add(new TestEntry
                {
                    Name = qualifiedName,
                    Project = projectName,
                    Framework = testAttribute.Framework,
                    Attribute = testAttribute.Attribute,
                    File = file,
                    Line = position.line,
                    Column = position.column
                });
            }
        }

        private static string FormatResults(string filePath, string frameworkFilter, int maxResults,
            List<TestEntry> tests)
        {
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(filePath) ? "solution" : filePath)
                .Append(" — ").Append(tests.Count).Append(" tests");
            if (frameworkFilter != null)
                sb.Append(" (").Append(frameworkFilter).Append(')');
            if (tests.Count >= maxResults)
                sb.Append(" (limited to ").Append(maxResults).Append(')');
            sb.AppendLine();

            string lastFile = null;
            foreach (var test in tests)
            {
                if (!string.Equals(lastFile, test.File, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine();
                    sb.AppendLine(test.File);
                    lastFile = test.File;
                }

                sb.Append("  ").Append(test.Name)
                    .Append(" [").Append(test.Framework).Append(':').Append(test.Attribute).Append(']');
                if (!string.IsNullOrEmpty(test.Project))
                    sb.Append(" project=").Append(test.Project);
                sb.Append(" :").Append(test.Line).Append(':').AppendLine(test.Column.ToString());
            }

            return sb.ToString().TrimEnd();
        }

        private static TestAttribute TryGetTestAttribute(string declarationText, string frameworkFilter)
        {
            foreach (var attributeName in GetLeadingAttributeNames(declarationText))
            {
                var normalized = NormalizeAttributeName(attributeName);
                var testAttribute = MatchTestAttribute(normalized, attributeName);
                if (testAttribute == null) continue;
                if (frameworkFilter != null && testAttribute.Framework != frameworkFilter) continue;
                return testAttribute;
            }

            return null;
        }

        private static IEnumerable<string> GetLeadingAttributeNames(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;

            var index = 0;
            while (index < text.Length)
            {
                index = SkipTrivia(text, index);
                if (index >= text.Length || text[index] != '[') yield break;

                var end = FindMatchingBracket(text, index);
                if (end < 0) yield break;

                foreach (var name in SplitAttributeSection(text.Substring(index + 1, end - index - 1)))
                    yield return name;

                index = end + 1;
            }
        }

        private static IEnumerable<string> SplitAttributeSection(string section)
        {
            foreach (var rawAttribute in SplitTopLevel(section, ','))
            {
                var attribute = rawAttribute.Trim();
                if (attribute.Length == 0) continue;

                if (attribute.StartsWith("global::", StringComparison.Ordinal))
                    attribute = attribute.Substring("global::".Length);

                var colon = attribute.IndexOf(':');
                if (colon >= 0)
                    attribute = attribute.Substring(colon + 1).TrimStart();

                if (attribute.StartsWith("global::", StringComparison.Ordinal))
                    attribute = attribute.Substring("global::".Length);

                var end = 0;
                while (end < attribute.Length)
                {
                    var c = attribute[end];
                    if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.')) break;
                    end++;
                }

                if (end > 0)
                    yield return attribute.Substring(0, end);
            }
        }

        private static IEnumerable<string> SplitTopLevel(string text, char separator)
        {
            var start = 0;
            var parenDepth = 0;
            var bracketDepth = 0;
            var inString = false;
            var stringChar = '\0';

            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (c == stringChar && (i == 0 || text[i - 1] != '\\')) inString = false;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    inString = true;
                    stringChar = c;
                }
                else if (c == '(') parenDepth++;
                else if (c == ')' && parenDepth > 0) parenDepth--;
                else if (c == '[') bracketDepth++;
                else if (c == ']' && bracketDepth > 0) bracketDepth--;
                else if (c == separator && parenDepth == 0 && bracketDepth == 0)
                {
                    yield return text.Substring(start, i - start);
                    start = i + 1;
                }
            }

            yield return text.Substring(start);
        }

        private static int SkipTrivia(string text, int index)
        {
            while (index < text.Length)
            {
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;

                if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '/')
                {
                    index += 2;
                    while (index < text.Length && text[index] != '\r' && text[index] != '\n') index++;
                    continue;
                }

                if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '*')
                {
                    index += 2;
                    while (index + 1 < text.Length && !(text[index] == '*' && text[index + 1] == '/')) index++;
                    index = Math.Min(index + 2, text.Length);
                    continue;
                }

                break;
            }

            return index;
        }

        private static int FindMatchingBracket(string text, int start)
        {
            var depth = 0;
            var inString = false;
            var stringChar = '\0';

            for (int i = start; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (c == stringChar && (i == 0 || text[i - 1] != '\\')) inString = false;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    inString = true;
                    stringChar = c;
                }
                else if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }

            return -1;
        }

        private static TestAttribute MatchTestAttribute(string normalizedName, string originalName)
        {
            if (NameEndsWith(normalizedName, "fact") || NameEndsWith(normalizedName, "theory"))
                return new TestAttribute("xunit", originalName);

            if (NameEndsWith(normalizedName, "test") || NameEndsWith(normalizedName, "testcase") ||
                NameEndsWith(normalizedName, "testcasesource"))
                return new TestAttribute("nunit", originalName);

            if (NameEndsWith(normalizedName, "testmethod") || NameEndsWith(normalizedName, "datatestmethod"))
                return new TestAttribute("mstest", originalName);

            return null;
        }

        private static bool NameEndsWith(string name, string shortName)
        {
            return name == shortName || name.EndsWith("." + shortName, StringComparison.Ordinal);
        }

        private static string NormalizeAttributeName(string attributeName)
        {
            var name = attributeName.Trim().ToLowerInvariant();
            return name.EndsWith("attribute", StringComparison.Ordinal)
                ? name.Substring(0, name.Length - "attribute".Length)
                : name;
        }

        private static string ParseFrameworkFilter(string framework)
        {
            if (string.IsNullOrWhiteSpace(framework)) return null;
            var normalized = framework.Trim().ToLowerInvariant();
            return normalized == "xunit" || normalized == "nunit" || normalized == "mstest"
                ? normalized
                : null;
        }

        private static void CopyIfPresent(JObject source, JObject target, string key)
        {
            var token = source[key];
            if (token != null) target[key] = token;
        }

        private static string ResultToString(object result)
        {
            if (result is string s) return s;
            var jo = JObject.FromObject(result);
            return "error: " + (jo["error"]?.ToString() ?? result.ToString());
        }

        private class TestEntry
        {
            public string Name;
            public string Project;
            public string Framework;
            public string Attribute;
            public string File;
            public int Line;
            public int Column;
        }

        private class TestAttribute
        {
            public readonly string Framework;
            public readonly string Attribute;

            public TestAttribute(string framework, string attribute)
            {
                Framework = framework;
                Attribute = attribute;
            }
        }
    }
}
