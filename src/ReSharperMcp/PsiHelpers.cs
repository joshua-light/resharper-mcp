using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.Application;
using JetBrains.DocumentModel;
using JetBrains.Util;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using JetBrains.Util.Logging;
using JetBrains.ReSharper.Psi.Modules;

namespace ReSharperMcp
{
    public static class PsiHelpers
    {
        public const int MaxSnippetLength = 2000;

        private static readonly ILogger Log = Logger.GetLogger(typeof(PsiHelpers));

        /// <summary>
        /// Result of a file lookup: found source file, or diagnostic info about why it failed.
        /// </summary>
        public class FileResolveResult
        {
            public IPsiSourceFile SourceFile { get; set; }
            public IProjectFile ProjectFile { get; set; }
            public string Error { get; set; }

            public bool IsFound => SourceFile != null;
        }

        public static IPsiSourceFile GetSourceFile(ISolution solution, string filePath)
        {
            return ResolveFile(solution, filePath).SourceFile;
        }

        public static FileResolveResult ResolveFile(ISolution solution, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return new FileResolveResult { Error = "File path is required" };

            IProjectFile projectFile = null;
            filePath = SolutionRoot.Trimmed(filePath);
            var absolutePath = SolutionRoot.Of(solution).Absolute(filePath) ?? filePath;

            if (Path.IsPathRooted(absolutePath))
                projectFile = solution.FindProjectItemsByLocation(VirtualFileSystemPath.Parse(absolutePath, InteractionContext.SolutionContext))
                    .OfType<IProjectFile>()
                    .FirstOrDefault();

            var allFilesQuery = solution.GetAllProjects()
                .SelectMany(project => project.GetAllProjectFiles());

            // 3. Case-insensitive comparison (handles macOS case differences)
            if (projectFile == null)
            {
                projectFile = allFilesQuery.FirstOrDefault(file =>
                {
                    Interruption.Current.CheckAndThrow();

                    return string.Equals(file.Location.FullPath, absolutePath, StringComparison.OrdinalIgnoreCase);
                });
            }

            // 4. Suffix match (match from end with path separator boundary, case-insensitive)
            if (projectFile == null)
            {
                var suffix = filePath.Replace('\\', '/');
                projectFile = allFilesQuery.FirstOrDefault(f =>
                {
                    Interruption.Current.CheckAndThrow();
                    var full = f.Location.FullPath.Replace('\\', '/');
                    return full.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                        && (full.Length == suffix.Length || full[full.Length - suffix.Length - 1] == '/');
                });
            }

            if (projectFile == null)
                return new FileResolveResult { Error = NotInSolution(solution, filePath, absolutePath) };

            // Project file found — try to get the PSI source file
            var sourceFile = projectFile.ToSourceFiles().FirstOrDefault();
            if (sourceFile != null)
                return new FileResolveResult { SourceFile = sourceFile, ProjectFile = projectFile };

            // Fallback: search PSI modules directly for a source file at this path.
            // ToSourceFiles() can return empty when the PSI cache is stale or the file
            // hasn't been indexed yet (common in git worktree solutions).
            var targetPath = projectFile.Location.FullPath;
            var psiServices = solution.GetPsiServices();
            foreach (var project in solution.GetAllProjects())
            {
                foreach (var module in psiServices.Modules.GetPsiModules(project))
                {
                    foreach (var sf in module.SourceFiles)
                    {
                        if (sf.GetLocation().FullPath == targetPath)
                            return new FileResolveResult { SourceFile = sf, ProjectFile = projectFile };
                    }
                }
            }

            return new FileResolveResult
            {
                ProjectFile = projectFile,
                Error = $"File exists in project but PSI source is not available (index may be stale): {projectFile.Location.FullPath}"
            };
        }

        private static string NotInSolution(ISolution solution, string filePath, string absolutePath)
        {
            var name = solution.SolutionFilePath?.NameWithoutExtension ?? solution.Name;
            var roots = SolutionRoot.Of(solution);
            var root = roots.Workspace == roots.Directory ? roots.Directory ?? "?" : $"{roots.Directory} or {roots.Workspace}";

            if (Path.IsPathRooted(absolutePath) && File.Exists(absolutePath))
                return $"File is on disk but in no project of {name}: {absolutePath}. Rider was asked to rescan it; "
                    + "if a project includes it (e.g. by a glob), retry in a moment, otherwise add it to a project.";

            return $"File not found: {filePath}. It is not on disk under {root}, "
                + $"and no file of {name} ends with that path.";
        }

        /// <summary>
        /// Result of resolving a symbol by name. Either a single element, an ambiguity error, or not found.
        /// </summary>
        public class SymbolResolveResult
        {
            public IDeclaredElement Element { get; set; }
            public List<SymbolCandidate> Candidates { get; set; }
            public SkippedElements Skipped { get; set; }
            public bool IsAmbiguous => Candidates != null && Candidates.Count > 1;
            public bool IsFound => Element != null;
        }

        public class SymbolCandidate
        {
            public string Name { get; set; }
            public string QualifiedName { get; set; }
            public string Kind { get; set; }
            public string File { get; set; }
            public int Line { get; set; }
        }

        /// <summary>
        /// Resolves a declared element by name across the solution.
        /// Accepts "Type", "Type.Member", "Namespace.Type.Member", generic arguments and parameter lists
        /// (see <see cref="SymbolName"/>). Optionally filters by kind ("type", "method", "property", "field", "event").
        /// Returns ambiguity info when multiple distinct symbols match.
        /// </summary>
        /// <remarks>
        /// Looks in sources first, then in compiled references, then in sources again ignoring case. Only the first
        /// pass scans every type for an unqualified member name: that scan takes seconds on a large solution.
        /// </remarks>
        public static SymbolResolveResult ResolveSymbolByName(ISolution solution, string symbolName, string kind = null)
        {
            var name = SymbolName.Parse(symbolName);
            if (name == null)
                return new SymbolResolveResult();

            var symbols = solution.GetPsiServices().Symbols;
            var skipped = new SkippedElements();

            var candidates = Candidates(symbols.GetSymbolScope(LibrarySymbolScope.NONE, caseSensitive: true),
                name, kind, inSources: true, scanAllTypes: true, skipped);
            if (candidates.Count == 0)
                candidates = Candidates(symbols.GetSymbolScope(LibrarySymbolScope.FULL, caseSensitive: true),
                    name, kind, inSources: false, scanAllTypes: false, skipped);
            if (candidates.Count == 0)
                candidates = Candidates(symbols.GetSymbolScope(LibrarySymbolScope.NONE, caseSensitive: false),
                    name.IgnoringCase, kind, inSources: true, scanAllTypes: false, skipped);

            if (candidates.Count == 0)
                return new SymbolResolveResult { Skipped = skipped };

            if (candidates.Count == 1)
                return new SymbolResolveResult { Element = candidates[0].element, Skipped = skipped };

            return new SymbolResolveResult
            {
                Candidates = candidates.Select(x => Candidate(x.element, x.fqn)).ToList(),
                Skipped = skipped
            };
        }

        private static List<(IDeclaredElement element, string fqn)> Candidates(
            ISymbolScope scope, SymbolName name, string kind, bool inSources, bool scanAllTypes, SkippedElements skipped)
        {
            var candidates = new List<(IDeclaredElement element, string fqn)>();
            var seen = new HashSet<string>();

            // Types, from the symbol cache's short-name index.
            foreach (var element in scope.GetElementsByShortName(name.Short))
            {
                skipped.Guard(element, () =>
                {
                    if (element is INamespace || kind != null && !MatchesKind(element, kind))
                        return;

                    var fqn = GetQualifiedName(element);
                    if (!name.IsQualified || name.Matches(fqn))
                        Add(element, fqn);
                });
            }

            var wantsMembers = kind == null || kind == "method" || kind == "property" || kind == "field" || kind == "event";
            if (candidates.Count > 0 || !wantsMembers)
                return candidates;

            if (name.IsQualified)
            {
                // "Type.Member": members of every type with that short name.
                foreach (var type in scope.GetElementsByShortName(name.Container).OfType<ITypeElement>())
                    skipped.Guard(type, () => AddMembers(type, qualified: true));
            }
            else if (scanAllTypes)
            {
                // An unqualified member: scan every type, stopping at 10 candidates for common names.
                foreach (var typeName in scope.GetAllShortNames())
                {
                    Interruption.Current.CheckAndThrow();
                    skipped.Guard(typeName, () =>
                    {
                        foreach (var type in scope.GetElementsByShortName(typeName).OfType<ITypeElement>())
                            skipped.Guard(type, () => AddMembers(type, qualified: false));
                    });

                    if (candidates.Count >= 10)
                        break;
                }
            }

            // Local functions: "Type.Function" or "Type.Method.Function".
            if (candidates.Count == 0 && inSources && name.IsQualified && (kind == null || kind == "method"))
                AddLocalFunctions();

            return candidates;

            void Add(IDeclaredElement element, string fqn)
            {
                if (seen.Add(fqn))
                    candidates.Add((element, fqn));
            }

            void AddMembers(ITypeElement type, bool qualified)
            {
                foreach (var member in type.GetMembers())
                {
                    if (!name.IsShort(member.ShortName) || kind != null && !MatchesKind(member, kind))
                        continue;

                    var fqn = GetQualifiedName(member);
                    if (!qualified || name.Matches(fqn))
                        Add(member, fqn);
                }
            }

            void AddLocalFunctions()
            {
                var parts = name.Parts;
                var typeName = parts.Length >= 3 ? parts[parts.Length - 3] : parts[parts.Length - 2];
                var methodName = parts.Length >= 3 ? parts[parts.Length - 2] : null;

                foreach (var type in scope.GetElementsByShortName(typeName).OfType<ITypeElement>())
                {
                    skipped.Guard(type, () =>
                    {
                        foreach (var member in type.GetMembers())
                        {
                            if (methodName != null && !string.Equals(member.ShortName, methodName, name.Comparison))
                                continue;

                            foreach (var declaration in member.GetDeclarations())
                            {
                                foreach (var function in FindAllLocalFunctions(declaration))
                                {
                                    if (!name.IsShort(function.DeclaredName) || function.DeclaredElement == null)
                                        continue;

                                    Add(function.DeclaredElement, $"{type.ShortName}.{member.ShortName}.{function.DeclaredName}");
                                }
                            }
                        }
                    });
                }
            }
        }

        private static SymbolCandidate Candidate(IDeclaredElement element, string fqn)
        {
            var (file, line) = Declaration(element);

            return new SymbolCandidate
            {
                Name = element.ShortName,
                QualifiedName = fqn,
                Kind = element.GetElementType().PresentableName,
                File = file ?? AssemblyLabel(element) ?? "[no source]",
                Line = line
            };
        }

        /// <summary>
        /// The first source declaration with a file path, or (null, 0) for compiled or generated elements.
        /// </summary>
        public static (string file, int line) Declaration(IDeclaredElement element)
        {
            foreach (var declaration in element.GetDeclarations())
            {
                var path = declaration.GetSourceFile()?.GetLocation().FullPath;
                if (string.IsNullOrEmpty(path))
                    continue;

                var range = TreeNodeExtensions.GetDocumentRange(declaration);

                return (path, range.IsValid() ? GetLineColumn(range.StartOffset).line : 0);
            }

            return (null, 0);
        }

        /// <summary>
        /// "[assembly Name.dll]" for an element compiled into a referenced assembly, otherwise null.
        /// </summary>
        public static string AssemblyLabel(IDeclaredElement element)
        {
            if (!(element is IClrDeclaredElement { Module: IAssemblyPsiModule module }))
                return null;

            return $"[assembly {module.Assembly.AssemblyName?.Name ?? module.DisplayName}.dll]";
        }

        /// <summary>
        /// Up to <paramref name="count"/> symbols whose name is close to one that was not found, as
        /// "QualifiedName — location" lines: members of the named type for "Type.Member", otherwise types from
        /// sources and compiled references. Close means containing either way, or a few typos apart.
        /// </summary>
        public static List<string> Suggestions(ISolution solution, SymbolName name, int count = 5)
        {
            var symbols = solution.GetPsiServices().Symbols;
            var sources = symbols.GetSymbolScope(LibrarySymbolScope.NONE, caseSensitive: false);
            var libraries = symbols.GetSymbolScope(LibrarySymbolScope.FULL, caseSensitive: false);
            var skipped = new SkippedElements();
            var ranked = new List<(int distance, IDeclaredElement element)>();

            if (name.Container != null)
            {
                foreach (var scope in new[] { sources, libraries })
                {
                    foreach (var type in scope.GetElementsByShortName(name.Container).OfType<ITypeElement>())
                    {
                        skipped.Guard(type, () =>
                        {
                            foreach (var member in type.GetMembers())
                                Rank(member.ShortName, () => new[] { member });
                        });
                    }
                }
            }

            if (ranked.Count == 0)
            {
                foreach (var scope in new[] { sources, libraries })
                {
                    foreach (var shortName in scope.GetAllShortNames())
                    {
                        Interruption.Current.CheckAndThrow();
                        Rank(shortName, () => scope.GetElementsByShortName(shortName).Where(x => !(x is INamespace)));
                    }
                }
            }

            var found = new List<string>();
            var seen = new HashSet<string>();
            foreach (var (_, element) in ranked.OrderBy(x => x.distance))
            {
                if (found.Count >= count)
                    break;

                skipped.Guard(element, () =>
                {
                    var fqn = GetQualifiedName(element);
                    if (seen.Add(fqn))
                        found.Add($"{fqn} — {Location(element)}");
                });
            }

            return found;

            void Rank(string candidate, Func<IEnumerable<IDeclaredElement>> elements)
            {
                var distance = Distance(name.Short, candidate);
                if (distance < 0)
                    return;

                foreach (var element in elements())
                    ranked.Add((distance, element));
            }
        }

        /// <summary>
        /// Lower is closer: 0 for a case-insensitive match, the edit count for a name a few typos apart, then names
        /// that contain the wanted one, shorter first. -1 when not close at all.
        /// </summary>
        private static int Distance(string wanted, string candidate)
        {
            if (string.Equals(wanted, candidate, StringComparison.OrdinalIgnoreCase))
                return 0;

            if (candidate.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                return 100 + candidate.Length - wanted.Length;

            var budget = Math.Max(1, wanted.Length / 4);
            if (Math.Abs(wanted.Length - candidate.Length) > budget)
                return -1;

            var a = wanted.ToLowerInvariant();
            var b = candidate.ToLowerInvariant();
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
                previous[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                var best = current[0];
                for (var j = 1; j <= b.Length; j++)
                {
                    var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                    current[j] = Math.Min(substitution, Math.Min(previous[j], current[j - 1]) + 1);
                    best = Math.Min(best, current[j]);
                }

                if (best > budget)
                    return -1;

                (previous, current) = (current, previous);
            }

            var edits = previous[b.Length];

            return edits <= budget ? edits : -1;
        }

        /// <summary>
        /// "path:line" for a source element, "[assembly Name.dll]" for a compiled one.
        /// </summary>
        public static string Location(IDeclaredElement element)
        {
            var (file, line) = Declaration(element);

            return file != null ? $"{file}:{line}" : AssemblyLabel(element) ?? "[generated]";
        }

        private static bool MatchesKind(IDeclaredElement element, string kind)
        {
            switch (kind.ToLowerInvariant())
            {
                case "type": return element is ITypeElement;
                case "method": return element is IMethod ||
                    element is JetBrains.ReSharper.Psi.CSharp.DeclaredElements.ILocalFunction;
                case "property": return element is IProperty;
                case "field": return element is IField;
                case "event": return element is IEvent;
                default: return false;
            }
        }

        private static IEnumerable<ILocalFunctionDeclaration> FindAllLocalFunctions(ITreeNode node)
        {
            for (var child = node.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is ILocalFunctionDeclaration lfd)
                {
                    yield return lfd;
                    // Also search nested local functions inside this one
                    foreach (var nested in FindAllLocalFunctions(lfd))
                        yield return nested;
                }
                else
                {
                    foreach (var found in FindAllLocalFunctions(child))
                        yield return found;
                }
            }
        }

        public static string GetQualifiedName(IDeclaredElement element)
        {
            var parts = new List<string> { element.ShortName };

            if (element is IClrDeclaredElement clr)
            {
                var containingType = clr.GetContainingType();
                while (containingType != null)
                {
                    parts.Insert(0, containingType.ShortName);
                    containingType = containingType.GetContainingType();
                }

                var ns = clr is ITypeElement te
                    ? te.GetContainingNamespace()
                    : clr.GetContainingType()?.GetContainingNamespace();

                if (ns != null && !string.IsNullOrEmpty(ns.QualifiedName))
                    parts.Insert(0, ns.QualifiedName);
            }

            return string.Join(".", parts);
        }

        /// <summary>
        /// Common resolution logic used by all tools. Resolves a symbol from either symbolName+kind or filePath+line+column.
        /// Returns either a resolved element or an error object to return to the caller.
        /// </summary>
        public static (IDeclaredElement element, object error) ResolveFromArgs(
            ISolution solution, string symbolName, string kind, string filePath, int line, int column)
        {
            if (!string.IsNullOrEmpty(symbolName))
            {
                var result = ResolveSymbolByName(solution, symbolName, kind);

                if (result.IsAmbiguous)
                    return (null, AmbiguityError(symbolName, result,
                        "Use a qualified name, add 'kind' filter, or use filePath+line+column instead."));

                if (!result.IsFound)
                {
                    if (kind != null)
                    {
                        // Check if the symbol exists with a different kind
                        var withoutKind = ResolveSymbolByName(solution, symbolName, null);
                        if (withoutKind.IsFound)
                        {
                            var actualKind = withoutKind.Element.GetElementType().PresentableName;
                            return (null, new { error = $"No {kind} named '{symbolName}' found. Did you mean the {actualKind} '{symbolName}'?" });
                        }
                        if (withoutKind.IsAmbiguous)
                        {
                            var kinds = string.Join(", ", withoutKind.Candidates.Select(c => c.Kind).Distinct());
                            return (null, new { error = $"No {kind} named '{symbolName}' found. Found symbols with that name of kind(s): {kinds}" });
                        }
                    }

                    return (null, new { error = NotFoundError(solution, symbolName, result.Skipped) });
                }

                return (result.Element, null);
            }

            if (!string.IsNullOrEmpty(filePath) && line > 0 && column > 0)
            {
                var resolved = ResolveFile(solution, filePath);
                if (!resolved.IsFound)
                    return (null, new { error = resolved.Error });

                return ResolveAt(solution, resolved.SourceFile, line, column, kind);
            }

            return (null, new { error = "Provide either 'symbolName' or 'filePath'+'line'+'column'" });
        }

        private static object AmbiguityError(string symbolName, SymbolResolveResult result, string advice) => new
        {
            error = $"Ambiguous symbol '{symbolName}': found {result.Candidates.Count} matches. {advice}",
            candidates = result.Candidates.Select(c => new
            {
                qualifiedName = c.QualifiedName,
                kind = c.Kind,
                file = c.File,
                line = c.Line
            }).ToList()
        };

        private static string NotFoundError(ISolution solution, string symbolName, SkippedElements skipped)
        {
            var text = new StringBuilder($"Symbol not found: {symbolName}");
            var name = SymbolName.Parse(symbolName);
            var suggestions = name == null ? new List<string>() : Suggestions(solution, name);
            if (suggestions.Count > 0)
            {
                text.Append("\ndid you mean:");
                foreach (var suggestion in suggestions)
                    text.Append("\n  ").Append(suggestion);
            }
            else
            {
                text.Append("\nTry search_symbol with part of the name, or includeLibraries: true for compiled types.");
            }

            if (skipped?.Line != null)
                text.Append('\n').Append(skipped.Line);

            return text.ToString();
        }

        /// <remarks>
        /// Resolving at a position can run ReSharper's language-injection analysis over nearby string literals,
        /// which reads the ambient compilation context (without one: NRE in GetRuntimeFeatures, observed). If it
        /// throws anyway, the identifier under the position is resolved by name instead.
        /// </remarks>
        private static (IDeclaredElement element, object error) ResolveAt(
            ISolution solution, IPsiSourceFile sourceFile, int line, int column, string kind)
        {
            try
            {
                using var context = CompilationContexts.For(sourceFile);
                var node = GetNodeAtPosition(sourceFile, line, column);
                if (node == null)
                    return (null, new { error = $"No syntax node found at {line}:{column}" });

                var element = GetDeclaredElement(node);
                if (element != null)
                    return (element, null);

                // Try to extract reference name for a more helpful error message
                var refName = GetNearestReferenceName(node);
                if (refName != null)
                    return (null, new { error = $"Cannot resolve symbol '{refName}' at {line}:{column}. It may be from an external/compiled assembly; try symbolName: '{refName}'." });

                return (null, new { error = $"No resolvable symbol found at {line}:{column}" });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Interruption.Current.CheckAndThrow();
                Log.Warn(exception, ExceptionOrigin.Algorithmic, $"ReSharperMcp failed to resolve {sourceFile.GetLocation()}:{line}:{column}");

                var identifier = IdentifierAt(sourceFile, line, column);
                var failure = $"ReSharper failed to resolve the reference at {line}:{column} ({exception.GetType().Name})";
                if (identifier == null)
                    return (null, new { error = $"{failure}. Try symbolName instead." });

                var byName = ResolveSymbolByName(solution, identifier, kind);
                if (byName.IsFound)
                    return (byName.Element, null);

                if (byName.IsAmbiguous)
                    return (null, AmbiguityError(identifier, byName,
                        $"{failure}, so '{identifier}' was looked up by name. Pick one by qualified name or 'kind'."));

                return (null, new { error = $"{failure}, and no symbol named '{identifier}' was found by name." });
            }
        }

        /// <summary>
        /// The identifier around a position, read from the document text so that it cannot fail the way PSI can.
        /// </summary>
        private static string IdentifierAt(IPsiSourceFile sourceFile, int line, int column)
        {
            try
            {
                var document = sourceFile.Document;
                var coords = new DocumentCoords((Int32<DocLine>)(line - 1), (Int32<DocColumn>)(column - 1));
                var offset = document.GetOffsetByCoords(coords);
                var text = document.GetText();

                var start = offset;
                while (start > 0 && IsIdentifierChar(text[start - 1]))
                    start--;

                var end = offset;
                while (end < text.Length && IsIdentifierChar(text[end]))
                    end++;

                return end > start ? text.Substring(start, end - start) : null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return null;
            }

            static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
        }

        /// <summary>
        /// Gets the primary PSI file for a source file (C#, F#, VB, ...).
        /// </summary>
        /// <remarks>
        /// Primary only: asking for every PSI file (<c>GetPsiFiles&lt;KnownLanguage&gt;()</c>) also builds the injected
        /// files, which runs ReSharper's language-injection scan over every string literal in the file. That scan is
        /// slow, never needed here, and crashes on some files (observed: NullReferenceException in
        /// <c>GetRuntimeFeatures</c> via <c>LiteralsInjectionPsiProvider</c>, on every call that touched the file).
        /// </remarks>
        public static IFile GetPsiFile(IPsiSourceFile sourceFile) =>
            sourceFile.GetPsiFiles<KnownLanguage>(PsiLanguageCategories.Primary).FirstOrDefault();

        public static ITreeNode GetNodeAtPosition(IPsiSourceFile sourceFile, int line, int column)
        {
            var psiFile = GetPsiFile(sourceFile);
            if (psiFile == null) return null;

            var document = sourceFile.Document;
            var docLine = (Int32<DocLine>)(line - 1);
            var docColumn = (Int32<DocColumn>)(column - 1);
            var coords = new DocumentCoords(docLine, docColumn);
            var offset = document.GetOffsetByCoords(coords);
            var treeOffset = psiFile.Translate(new DocumentOffset(document, offset));

            // A column names a character, so the answer is the token holding it. FindNodeAt takes an empty range,
            // which at a token's first character also borders the whitespace before it, and returned that
            // whitespace's parent (observed: the start of `Wait` in `var m = Wait<M>(...)` resolved to `m`).
            var token = psiFile.FindTokenAt(treeOffset);
            if (token == null)
                return psiFile.FindNodeAt(treeOffset);

            return token.IsWhitespaceToken() ? token.GetNextToken() ?? token : token;
        }

        public static IDeclaredElement GetDeclaredElement(ITreeNode node)
        {
            var originalOffset = node.GetTreeStartOffset();

            // Phase 1: Walk up looking for resolvable references (what the cursor points AT).
            // This handles usages, type references, member access, etc.
            var current = node;
            for (var depth = 0; current != null && depth < 5; depth++)
            {
                var resolved = ResolveReferences(current);
                if (resolved != null)
                    return resolved;

                current = current.Parent;
            }

            // Phase 2: No references resolved — look for a nearby declaration.
            // This handles clicking on a declaration's name, keyword, or modifier.
            // Limited to 3 levels to avoid jumping to distant ancestor declarations
            // (e.g. returning a class when the cursor was on an unresolvable type reference
            // deep inside the class body).
            current = node;
            for (var depth = 0; current != null && depth < 3; depth++)
            {
                if (current is IDeclaration declaration)
                    return declaration.DeclaredElement;
                current = current.Parent;
            }

            return null;
        }

        /// <remarks>
        /// A string literal's references belong to an injected language (regex, paths, ...), never to the code the
        /// caller points at, and asking for them runs ReSharper's injection analysis. A node whose references throw
        /// is stepped over so that its parent still gets a chance.
        /// </remarks>
        private static IDeclaredElement ResolveReferences(ITreeNode node)
        {
            if (IsLiteral(node))
                return null;

            try
            {
                foreach (var reference in node.GetReferences())
                {
                    var resolved = reference.Resolve();
                    if (resolved.DeclaredElement != null)
                        return resolved.DeclaredElement;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Interruption.Current.CheckAndThrow();
                Log.Warn(exception, ExceptionOrigin.Algorithmic, "ReSharperMcp stepped over a node whose references failed to resolve");
            }

            return null;
        }

        public static bool IsLiteral(ITreeNode node) =>
            node is ILiteralExpression || node is ITokenNode token && token.GetTokenType().IsStringLiteral;

        /// <summary>
        /// Walks up the tree from a node to find the nearest reference name.
        /// Used to produce better error messages when symbol resolution fails.
        /// </summary>
        private static string GetNearestReferenceName(ITreeNode node)
        {
            var current = node;
            for (int i = 0; i < 3 && current != null; i++)
            {
                foreach (var r in current.GetReferences())
                    return r.GetName();
                current = current.Parent;
            }
            // Fall back to the token text
            var text = node.GetText()?.Trim();
            return !string.IsNullOrEmpty(text) && text.Length <= 100 ? text : null;
        }

        public static (int line, int column) GetLineColumn(DocumentOffset offset)
        {
            var coords = offset.ToDocumentCoords();
            return ((int)coords.Line + 1, (int)coords.Column + 1);
        }

        public static string TruncateSnippet(string text, int maxLength = MaxSnippetLength)
        {
            if (text == null) return null;
            text = text.Trim();
            if (text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// Formats a compact one-line signature for a declared element.
        /// Examples: "property Id : int", "static method DoStuff(x:int, y:string) : void", "class MyClass"
        /// </summary>
        public static string FormatSignature(IDeclaredElement element)
        {
            var lang = element.PresentationLanguage ?? CSharpLanguage.Instance;
            var sb = new StringBuilder();

            // Modifiers
            if (element is IModifiersOwner mod)
            {
                if (mod.IsStatic) sb.Append("static ");
                if (mod.IsAbstract) sb.Append("abstract ");
                if (element is IMethod m)
                {
                    if (m.IsVirtual) sb.Append("virtual ");
                    if (m.IsOverride) sb.Append("override ");
                }
            }

            // Kind + Name
            sb.Append(element.GetElementType().PresentableName);
            sb.Append(' ');
            sb.Append(element.ShortName);

            // Parameters (methods/local functions always get parens; others only if they have params, e.g. indexers)
            if (element is IMethod || element is JetBrains.ReSharper.Psi.CSharp.DeclaredElements.ILocalFunction)
                AppendParams(sb, (IParametersOwner)element, lang);
            else if (element is IParametersOwner po && po.Parameters.Count > 0)
                AppendParams(sb, po, lang);

            // Type: return type for methods/local functions, declared type for fields/properties
            if (element is IMethod method)
            {
                sb.Append(" : ");
                sb.Append(method.ReturnType.GetPresentableName(lang));
            }
            else if (element is JetBrains.ReSharper.Psi.CSharp.DeclaredElements.ILocalFunction localFunc)
            {
                sb.Append(" : ");
                sb.Append(localFunc.ReturnType.GetPresentableName(lang));
            }
            else if (element is ITypeOwner typeOwner)
            {
                sb.Append(" : ");
                sb.Append(typeOwner.Type.GetPresentableName(lang));
            }

            return sb.ToString();
        }

        private static void AppendParams(StringBuilder sb, IParametersOwner owner, PsiLanguageType lang)
        {
            sb.Append('(');
            var first = true;
            foreach (var p in owner.Parameters)
            {
                if (!first) sb.Append(", ");
                sb.Append(p.ShortName);
                sb.Append(':');
                sb.Append(p.Type.GetPresentableName(lang));
                first = false;
            }
            sb.Append(')');
        }
    }
}
