using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;
using JetBrains.Util.Logging;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class ListTestsTool : IMcpTool
    {
        private static readonly ILogger Logger = JetBrains.Util.Logging.Logger.GetLogger(typeof(ListTestsTool));
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

            var frameworkFilter = ListTestsParser.ParseFrameworkFilter(framework);
            if (!string.IsNullOrWhiteSpace(framework) && frameworkFilter == null)
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

            IFile psiFile;
            try
            {
                psiFile = PsiHelpers.GetPsiFile(sourceFile);
            } catch (Exception ex)
            {
                Logger.Error($"Failed to get PSI file for {sourceFile.GetLocation().FullPath}: {ex.Message}");
                return;
            }
            if (psiFile == null) return;

            foreach (var declaration in psiFile.Descendants().OfType<IDeclaration>())
            {
                if (tests.Count >= maxResults) break;

                var method = declaration.DeclaredElement as IMethod;
                if (method == null || method.ShortName == ".ctor") continue;

                var testAttribute = ListTestsParser.TryGetTestAttribute(declaration.GetText(), frameworkFilter);
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
    }
}