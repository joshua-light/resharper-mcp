using System.Collections.Generic;
using System.IO;
using System.Linq;
using JetBrains.Application.Progress;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class FindUsagesTool : IMcpTool
    {
        private readonly ISolution _solution;

        public FindUsagesTool(ISolution solution) => _solution = solution;

        public string Name => "find_usages";

        public string Description =>
            "Find all usages/references of a code symbol (class, method, property, variable, etc.) " +
            "in the current solution. Provide either a symbolName (e.g. 'MyClass' or 'Namespace.MyClass') " +
            "or a file path with position (line/column). " +
            "Results are grouped by project and file for easy navigation.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                symbolName = new { type = "string", description = "Symbol name to find usages of (e.g. 'MyClass', 'Namespace.MyClass', 'MyClass.MyMethod'). Alternative to filePath+line+column." },
                kind = new { type = "string", description = "Filter by symbol kind when using symbolName: 'type', 'method', 'property', 'field', 'event'. Helps disambiguate when multiple symbols share a name." },
                filePath = new { type = "string", description = "Absolute path to the file containing the symbol" },
                line = new { type = "integer", description = "1-based line number of the symbol" },
                column = new { type = "integer", description = "1-based column number of the symbol" },
                excludeDeclarationFile = new { type = "boolean", description = "Exclude usages from the file(s) where the symbol is declared. Useful for finding consumers rather than definition-site references. Default: false." }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments)
        {
            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(
                _solution,
                arguments["symbolName"]?.ToString(),
                arguments["kind"]?.ToString(),
                arguments["filePath"]?.ToString(),
                arguments["line"]?.Value<int>() ?? 0,
                arguments["column"]?.Value<int>() ?? 0);

            if (error != null) return error;

            var excludeDeclFile = arguments["excludeDeclarationFile"]?.Value<bool>() ?? false;

            // Collect declaration file paths for exclusion
            var declFilePaths = new HashSet<string>();
            if (excludeDeclFile)
            {
                foreach (var decl in declaredElement.GetDeclarations())
                {
                    var sf = decl.GetSourceFile();
                    if (sf != null)
                        declFilePaths.Add(sf.GetLocation().FullPath);
                }
            }

            // Collect raw usages
            var rawUsages = new List<RawUsage>();
            var psiServices = _solution.GetPsiServices();
            var searchDomain = SearchDomainFactory.Instance.CreateSearchDomain(_solution, false);

            psiServices.Finder.FindReferences(
                declaredElement,
                searchDomain,
                new FindResultConsumer(findResult =>
                {
                    if (findResult is FindResultReference reference)
                    {
                        var refNode = reference.Reference.GetTreeNode();
                        var refSourceFile = refNode.GetSourceFile();
                        if (refSourceFile != null)
                        {
                            var filePath = refSourceFile.GetLocation().FullPath;

                            if (excludeDeclFile && declFilePaths.Contains(filePath))
                                return FindExecution.Continue;

                            var refRange = TreeNodeExtensions.GetDocumentRange(refNode);
                            if (refRange.IsValid())
                            {
                                var (refLine, refCol) = PsiHelpers.GetLineColumn(refRange.StartOffset);
                                var projectName = refSourceFile.GetProject()?.Name;
                                rawUsages.Add(new RawUsage
                                {
                                    Project = projectName ?? "(unknown)",
                                    File = filePath,
                                    Line = refLine,
                                    Column = refCol,
                                    Text = PsiHelpers.TruncateSnippet(
                                        refNode.Parent?.GetText() ?? refNode.GetText())
                                });
                            }
                        }
                    }
                    return FindExecution.Continue;
                }),
                NullProgressIndicator.Create());

            // Deduplicate: keep only one usage per line per file (multiple hits on the
            // same line are typically parameter occurrences in the same expression)
            var deduped = rawUsages
                .GroupBy(u => $"{u.File}:{u.Line}")
                .Select(g => g.First())
                .ToList();

            // Group by project → file
            var grouped = deduped
                .GroupBy(u => u.Project)
                .OrderByDescending(g => g.Count())
                .Select(projectGroup => new
                {
                    project = projectGroup.Key,
                    usageCount = projectGroup.Count(),
                    files = projectGroup
                        .GroupBy(u => u.File)
                        .OrderByDescending(fg => fg.Count())
                        .Select(fileGroup => new
                        {
                            file = fileGroup.Key,
                            fileName = Path.GetFileName(fileGroup.Key),
                            usageCount = fileGroup.Count(),
                            usages = fileGroup.Select(u => new
                            {
                                line = u.Line,
                                column = u.Column,
                                text = u.Text
                            }).ToList()
                        }).ToList()
                }).ToList();

            var fileCount = deduped.Select(u => u.File).Distinct().Count();
            var projectCount = deduped.Select(u => u.Project).Distinct().Count();

            var result = new Dictionary<string, object>
            {
                ["symbol"] = declaredElement.ShortName,
                ["kind"] = declaredElement.GetElementType().PresentableName,
                ["qualifiedName"] = PsiHelpers.GetQualifiedName(declaredElement),
                ["usagesCount"] = deduped.Count,
                ["fileCount"] = fileCount,
                ["projectCount"] = projectCount,
                ["projects"] = grouped
            };

            // Include declaration location if available
            var declarations = declaredElement.GetDeclarations();
            if (declarations.Count > 0)
            {
                var decl = declarations[0];
                var declRange = TreeNodeExtensions.GetDocumentRange(decl);
                if (declRange.IsValid())
                {
                    var declSourceFile = decl.GetSourceFile();
                    if (declSourceFile != null)
                    {
                        var (declLine, declCol) = PsiHelpers.GetLineColumn(declRange.StartOffset);
                        result["declarationFile"] = declSourceFile.GetLocation().FullPath;
                        result["declarationLine"] = declLine;
                        result["declarationColumn"] = declCol;
                    }
                }
            }

            return result;
        }

        private class RawUsage
        {
            public string Project;
            public string File;
            public int Line;
            public int Column;
            public string Text;
        }
    }
}
