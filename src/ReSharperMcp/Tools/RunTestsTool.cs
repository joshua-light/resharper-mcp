using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.UnitTestFramework;
using JetBrains.ReSharper.UnitTestFramework.Criteria;
using JetBrains.ReSharper.UnitTestFramework.Elements;
using JetBrains.ReSharper.UnitTestFramework.Execution;
using JetBrains.ReSharper.UnitTestFramework.Execution.Hosting;
using JetBrains.ReSharper.UnitTestFramework.Execution.Launch;
using JetBrains.ReSharper.UnitTestFramework.Features;
using JetBrains.ReSharper.UnitTestFramework.Persistence;
using JetBrains.ReSharper.UnitTestFramework.Session;
using JetBrains.Util;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class RunTestsTool : IMcpDirectTool
    {
        private const int DefaultTimeoutSeconds = 300;
        private const int MaxTimeoutSeconds = 3600;

        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;
        private readonly IUnitTestingFacade _unitTestingFacade;
        private readonly IUnitTestPsiManager _unitTestPsiManager;

        public RunTestsTool(
            ISolution solution,
            IShellLocks shellLocks,
            IUnitTestingFacade unitTestingFacade,
            IUnitTestPsiManager unitTestPsiManager)
        {
            _solution = solution;
            _shellLocks = shellLocks;
            _unitTestingFacade = unitTestingFacade;
            _unitTestPsiManager = unitTestPsiManager;
        }

        public string Name => "run_tests";

        public string Description =>
            "Run tests using Rider/ReSharper's unit test runner. Does not shell out to dotnet test. " +
            "Supports solution, project, file, and individual test scopes.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                scope = new
                {
                    type = "string",
                    description = "Test scope: 'solution' (default), 'project', 'file', or 'test'."
                },
                project = new
                {
                    type = "string",
                    description = "Project name or project file path for project scope."
                },
                filePath = new
                {
                    type = "string",
                    description = "Absolute or solution-relative file path for file or test scope."
                },
                line = new
                {
                    type = "integer",
                    description = "1-based line for resolving a single test when scope is 'test'."
                },
                column = new
                {
                    type = "integer",
                    description = "1-based column for resolving a single test when scope is 'test'."
                },
                testName = new
                {
                    type = "string",
                    description = "Fully qualified test method name, matching list_tests output, for test scope."
                },
                buildPolicy = new
                {
                    type = "string",
                    description = "Optional build policy: 'automatic' (default), 'never', or 'wholeSolution'."
                },
                openSession = new
                {
                    type = "boolean",
                    description = "Open/focus Rider's unit test session UI. Default: false."
                },
                timeoutSeconds = new
                {
                    type = "integer",
                    description = "Maximum time to wait for the run. Default: 300, maximum: 3600."
                }
            },
            required = Array.Empty<string>()
        };

        public object Execute(JObject arguments)
        {
            var timeoutSeconds = arguments["timeoutSeconds"]?.Value<int>() ?? DefaultTimeoutSeconds;
            if (timeoutSeconds <= 0) timeoutSeconds = DefaultTimeoutSeconds;
            if (timeoutSeconds > MaxTimeoutSeconds) timeoutSeconds = MaxTimeoutSeconds;

            var target = ResolveTarget(arguments);
            if (target.Error != null) return target.Error;

            var buildPolicy = ParseBuildPolicy(arguments["buildPolicy"]?.ToString());
            if (buildPolicy.Error != null) return buildPolicy.Error;

            var openSession = arguments["openSession"]?.Value<bool>() ?? false;
            var sessionName = "MCP: " + target.Name;
            var elements = new UnitTestElements(target.Criterion, target.ExplicitElements);

            _unitTestingFacade.Initialized.Wait(TimeSpan.FromSeconds(30));

            var session = _unitTestingFacade.SessionRepository.CreateSession(target.Criterion, sessionName, null);

            try
            {
                if (openSession)
                    _unitTestingFacade.SessionManager.OpenSession(session, true,
                        target.ExplicitElements.Select(e => e.Id).ToList()).Wait(TimeSpan.FromSeconds(30));

                var launch = _unitTestingFacade.LaunchManager.CreateLaunch(
                    session,
                    elements,
                    new RunHostProvider(),
                    buildPolicy.Value,
                    null,
                    null,
                    null,
                    false);

                var sw = Stopwatch.StartNew();
                var task = launch.Run();
                var completed = task.Wait(TimeSpan.FromSeconds(timeoutSeconds));
                sw.Stop();

                if (!completed)
                {
                    launch.Abort();
                    return FormatResults(session, target, sw.Elapsed, timedOut: true);
                }

                return FormatResults(session, target, sw.Elapsed, timedOut: false);
            }
            finally
            {
                if (!openSession)
                {
                    _unitTestingFacade.SessionRepository.DestroySession(session);
                }
            }
        }

        private RunTarget ResolveTarget(JObject arguments)
        {
            RunTarget target = null;
            object error = null;
            var done = new ManualResetEventSlim(false);

            _shellLocks.ExecuteOrQueueReadLock(
                "ReSharperMcp.run_tests.resolve",
                () =>
                {
                    try
                    {
                        _solution.GetPsiServices().Files.CommitAllDocuments();
                        target = ResolveTargetOnPsiThread(arguments);
                    }
                    catch (Exception ex)
                    {
                        error = new { error = ex.Message };
                    }
                    finally
                    {
                        done.Set();
                    }
                });

            if (!done.Wait(TimeSpan.FromSeconds(30)))
                return new RunTarget { Error = new { error = "Timed out resolving test scope. The IDE may be busy." } };

            return error != null ? new RunTarget { Error = error } : target;
        }

        private RunTarget ResolveTargetOnPsiThread(JObject arguments)
        {
            var scope = (arguments["scope"]?.ToString() ?? "solution").Trim().ToLowerInvariant();
            switch (scope)
            {
                case "solution":
                    return new RunTarget
                    {
                        Name = "solution",
                        Criterion = SolutionCriterion.Instance
                    };

                case "project":
                    return ResolveProject(arguments["project"]?.ToString());

                case "file":
                    return ResolveFile(arguments["filePath"]?.ToString());

                case "test":
                    return ResolveTest(arguments);

                default:
                    return new RunTarget
                        { Error = new { error = "scope must be one of: solution, project, file, test" } };
            }
        }

        private RunTarget ResolveProject(string projectArg)
        {
            if (string.IsNullOrWhiteSpace(projectArg))
                return new RunTarget { Error = new { error = "project is required when scope is 'project'" } };

            var project = _solution.GetAllProjects().FirstOrDefault(p =>
                string.Equals(p.Name, projectArg, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.ProjectFileLocation.FullPath, projectArg, StringComparison.OrdinalIgnoreCase) ||
                p.ProjectFileLocation.FullPath.EndsWith(projectArg.Replace('\\', '/'),
                    StringComparison.OrdinalIgnoreCase));

            if (project == null)
                return new RunTarget { Error = new { error = $"Project not found: {projectArg}" } };

            return new RunTarget
            {
                Name = project.Name,
                Criterion = new ProjectCriterion(project)
            };
        }

        private RunTarget ResolveFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return new RunTarget { Error = new { error = "filePath is required when scope is 'file'" } };

            var resolved = PsiHelpers.ResolveFile(_solution, filePath);
            if (!resolved.IsFound)
                return new RunTarget { Error = new { error = resolved.Error } };
            if (resolved.ProjectFile == null)
                return new RunTarget { Error = new { error = $"Project file unavailable for: {filePath}" } };

            return new RunTarget
            {
                Name = resolved.ProjectFile.Location.Name,
                Criterion = new ProjectFileCriterion(resolved.ProjectFile)
            };
        }

        private RunTarget ResolveTest(JObject arguments)
        {
            IDeclaredElement element = null;
            object error = null;
            var testName = arguments["testName"]?.ToString();

            if (!string.IsNullOrWhiteSpace(testName))
            {
                var resolved = PsiHelpers.ResolveSymbolByName(_solution, testName, "method");
                if (resolved.IsAmbiguous)
                    return new RunTarget { Error = new { error = $"Ambiguous testName: {testName}" } };
                if (!resolved.IsFound)
                    return new RunTarget { Error = new { error = $"Test method not found: {testName}" } };
                element = resolved.Element;
            }
            else
            {
                var filePath = arguments["filePath"]?.ToString();
                var line = arguments["line"]?.Value<int>() ?? 0;
                var column = arguments["column"]?.Value<int>() ?? 0;
                var resolved = PsiHelpers.ResolveFromArgs(_solution, null, "method", filePath, line, column);
                element = resolved.element;
                error = resolved.error;
                if (error != null) return new RunTarget { Error = error };
            }

            var testElement = _unitTestPsiManager.GetElement(element);
            if (testElement == null)
                return new RunTarget
                {
                    Error = new
                    {
                        error = $"Resolved symbol is not a known runnable test: {PsiHelpers.GetQualifiedName(element)}"
                    }
                };

            return new RunTarget
            {
                Name = testElement.GetPresentation(),
                Criterion = new TestElementCriterion(new[] { testElement }),
                ExplicitElements = new[] { testElement }
            };
        }

        private static BuildPolicyParseResult ParseBuildPolicy(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Equals("automatic", StringComparison.OrdinalIgnoreCase))
                return new BuildPolicyParseResult { Value = null };
            if (raw.Equals("never", StringComparison.OrdinalIgnoreCase))
                return new BuildPolicyParseResult { Value = BuildPolicy.Never };
            if (raw.Equals("wholeSolution", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("whole_solution", StringComparison.OrdinalIgnoreCase))
                return new BuildPolicyParseResult { Value = BuildPolicy.WholeSolution };

            return new BuildPolicyParseResult
                { Error = new { error = "buildPolicy must be one of: automatic, never, wholeSolution" } };
        }

        private string FormatResults(IUnitTestSession session, RunTarget target, TimeSpan elapsed, bool timedOut)
        {
            var ids = GetResultIds(session, target).ToList();
            var entries = ids
                .Select(id => new ResultEntry
                {
                    Id = id,
                    Element = _unitTestingFacade.ElementRepository.GetBy(id),
                    Result = _unitTestingFacade.ResultManager.GetResult(id, session),
                    Data = _unitTestingFacade.ResultManager.GetResultData(id, session)
                })
                .Where(e => e.Element != null)
                .ToList();

            var sb = new StringBuilder();
            sb.Append("run_tests ").Append(target.Name).Append(timedOut ? " — timed out" : " — finished").AppendLine()
                .Append("elapsed: ").AppendLine(elapsed.ToString())
                .Append("results: ").Append(entries.Count).AppendLine();

            AppendStatusCount(sb, entries, UnitTestStatus.Success, "passed");
            AppendStatusCount(sb, entries, UnitTestStatus.Failed, "failed");
            AppendStatusCount(sb, entries, UnitTestStatus.Ignored, "ignored");
            AppendStatusCount(sb, entries, UnitTestStatus.Inconclusive, "inconclusive");
            AppendStatusCount(sb, entries, UnitTestStatus.Aborted, "aborted");
            AppendStatusCount(sb, entries, UnitTestStatus.Unknown, "unknown");

            var failed = entries.Where(e =>
                e.Result.Status == UnitTestStatus.Failed || e.Result.Status == UnitTestStatus.Aborted).ToList();
            if (failed.Count > 0)
            {
                sb.AppendLine().AppendLine("failures:");
                foreach (var entry in failed.Take(20))
                {
                    sb.Append("  ").Append(GetElementName(entry.Element)).Append(" — ")
                        .AppendLine(entry.Result.ShortMessage ?? entry.Result.Status.ToString());
                    AppendOutput(sb, entry.Data, indent: "    ");
                }

                if (failed.Count > 20)
                    sb.Append("  ... ").Append(failed.Count - 20).AppendLine(" more failures");
            }

            return sb.ToString().TrimEnd();
        }

        private IEnumerable<Int32<IUnitTestElement>> GetResultIds(IUnitTestSession session, RunTarget target)
        {
            if (target.ExplicitElements != null && target.ExplicitElements.Count > 0)
            {
                foreach (var element in target.ExplicitElements)
                    yield return element.Id;
                yield break;
            }

            var query = _unitTestingFacade.ElementRepository.Query(session.Criterion.Value);
            foreach (var id in query.Ids)
                yield return id;
        }

        private static void AppendStatusCount(StringBuilder sb, IEnumerable<ResultEntry> entries, UnitTestStatus status,
            string label)
        {
            sb.Append(label).Append(": ").AppendLine(entries.Count(e => e.Result.Status == status).ToString());
        }

        private static string GetElementName(IUnitTestElement element)
        {
            var declared = element.GetDeclaredElement();
            return declared != null ? PsiHelpers.GetQualifiedName(declared) : element.GetPresentation();
        }

        private static void AppendOutput(StringBuilder sb, UnitTestResultData data, string indent)
        {
            if (data == null) return;

            for (var i = 0; i < data.ExceptionCount; i++)
                sb.Append(indent).AppendLine(data.GetExceptionInfo(i).ToString());

            for (var i = 0; i < data.OutputChunksCount; i++)
            {
                var output = data.GetOutputChunk(i);
                if (string.IsNullOrWhiteSpace(output)) continue;
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(20))
                    sb.Append(indent).AppendLine(line);
            }
        }

        private class RunTarget
        {
            public string Name;
            public IUnitTestElementCriterion Criterion;
            public IReadOnlyCollection<IUnitTestElement> ExplicitElements = Array.Empty<IUnitTestElement>();
            public object Error;
        }

        private class BuildPolicyParseResult
        {
            public BuildPolicy? Value;
            public object Error;
        }

        private class ResultEntry
        {
            public Int32<IUnitTestElement> Id;
            public IUnitTestElement Element;
            public UnitTestResult Result;
            public UnitTestResultData Data;
        }
    }
}
