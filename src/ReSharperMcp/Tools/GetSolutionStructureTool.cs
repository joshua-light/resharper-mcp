using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Impl;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class GetSolutionStructureTool : IMcpTool
    {
        private readonly ISolution _solution;

        public GetSolutionStructureTool(ISolution solution) => _solution = solution;

        public string Name => "get_solution_structure";

        public string Description =>
            "Get the solution structure: all projects with their paths, target frameworks, " +
            "and project-to-project references. Useful for understanding the architecture and dependency graph.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                includeFiles = new { type = "boolean", description = "If true, include file lists for each project. Default: false (can be verbose)." }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments)
        {
            var includeFiles = arguments["includeFiles"]?.Value<bool>() ?? false;

            var solutionPath = _solution.SolutionFilePath.FullPath;
            var sb = new StringBuilder();

            var projects = _solution.GetAllProjects()
                .Where(p => p.ProjectFileLocation != null && !p.ProjectFileLocation.IsEmpty)
                .ToList();

            sb.Append(solutionPath).Append(" — ").Append(projects.Count).AppendLine(" projects");

            foreach (var project in projects)
            {
                sb.AppendLine();
                sb.Append("[").Append(project.Name).Append("] ").AppendLine(project.ProjectFileLocation.FullPath);

                // Target frameworks
                var tfms = project.TargetFrameworkIds;
                if (tfms != null && tfms.Any())
                    sb.Append("  frameworks: ").AppendLine(string.Join(", ", tfms.Select(t => t.ToString())));

                // Project references
                var projectRefs = new HashSet<string>();
                foreach (var tfm in project.TargetFrameworkIds ?? Enumerable.Empty<JetBrains.Util.Dotnet.TargetFrameworkIds.TargetFrameworkId>())
                {
                    foreach (var reference in project.GetProjectReferences(tfm))
                    {
                        var refName = ProjectReferenceExtension.GetReferencedName(reference);
                        if (!string.IsNullOrEmpty(refName))
                            projectRefs.Add(refName);
                    }
                }
                if (projectRefs.Count > 0)
                    sb.Append("  references: ").AppendLine(string.Join(", ", projectRefs.OrderBy(r => r)));

                // File count
                var fileCount = project.GetAllProjectFiles().Count();
                sb.Append("  files: ").AppendLine(fileCount.ToString());

                // File list (optional)
                if (includeFiles)
                {
                    foreach (var file in project.GetAllProjectFiles().OrderBy(f => f.Location.FullPath))
                    {
                        sb.Append("    ").AppendLine(file.Location.FullPath);
                    }
                }
            }

            return sb.ToString().TrimEnd();
        }
    }
}
