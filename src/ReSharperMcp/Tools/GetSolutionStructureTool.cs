using System.Collections.Generic;
using System.Linq;
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
            var projects = new List<object>();

            foreach (var project in _solution.GetAllProjects())
            {
                // Skip misc/solution-level items
                if (project.ProjectFileLocation == null || project.ProjectFileLocation.IsEmpty)
                    continue;

                var projectInfo = new Dictionary<string, object>
                {
                    ["name"] = project.Name,
                    ["path"] = project.ProjectFileLocation.FullPath,
                };

                // Target frameworks
                var tfms = project.TargetFrameworkIds;
                if (tfms != null && tfms.Any())
                    projectInfo["targetFrameworks"] = tfms.Select(t => t.ToString()).ToList();

                // Project references (try each TFM)
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
                    projectInfo["projectReferences"] = projectRefs.OrderBy(r => r).ToList();

                // File count (always)
                var fileCount = project.GetAllProjectFiles().Count();
                projectInfo["fileCount"] = fileCount;

                // File list (optional)
                if (includeFiles)
                {
                    var files = project.GetAllProjectFiles()
                        .Select(f => f.Location.FullPath)
                        .OrderBy(f => f)
                        .ToList();
                    projectInfo["files"] = files;
                }

                projects.Add(projectInfo);
            }

            return new
            {
                solution = solutionPath,
                projectCount = projects.Count,
                projects
            };
        }
    }
}
