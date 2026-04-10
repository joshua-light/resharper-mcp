using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JetBrains.Application.Progress;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.DeclaredElements;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class FlowTool : IMcpTool
    {
        private readonly ISolution _solution;

        public FlowTool(ISolution solution) => _solution = solution;

        public string Name => "flow";

        public string Description =>
            "Describe the control flow of a method or type: ordered execution steps, branch conditions, " +
            "loops, error paths (try/catch, guard clauses), inlined call targets, and why-hints from " +
            "comments and variable names. Like a senior dev explaining the code on a whiteboard. " +
            "For methods: produces a narrated control-flow summary. " +
            "For types: describes all non-trivial methods. " +
            "Provide either a symbolName or a file path with position. " +
            "Pass multiple symbols via the 'symbols' array to describe several in one call.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                symbolName = new
                {
                    type = "string",
                    description = "Symbol name (e.g. 'MyClass.Update', 'MyClass'). " +
                                  "For methods: describes the method flow. For types: describes all non-trivial methods."
                },
                kind = new
                {
                    type = "string",
                    description = "Filter by symbol kind: 'type', 'method', 'property'. " +
                                  "Helps disambiguate when multiple symbols share a name."
                },
                depth = new
                {
                    type = "integer",
                    description = "How many call levels to inline. " +
                                  "1 = just this method's flow, 2 = inline one level of calls (default), " +
                                  "3+ = rarely needed and token-expensive."
                },
                includeErrorPaths = new
                {
                    type = "boolean",
                    description = "Include try/catch blocks, early returns, guard clauses, throw statements. Default: true."
                },
                filePath = new { type = "string", description = "Absolute path to the file containing the symbol" },
                line = new { type = "integer", description = "1-based line number of the symbol" },
                column = new { type = "integer", description = "1-based column number of the symbol" },
                symbols = new
                {
                    type = "array",
                    description = "Array of symbols to describe in batch. Each item is an object with " +
                                  "symbolName/kind or filePath/line/column. Results are concatenated with separators.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            symbolName = new { type = "string", description = "Symbol name" },
                            kind = new { type = "string", description = "Symbol kind filter" },
                            filePath = new { type = "string", description = "File path" },
                            line = new { type = "integer", description = "1-based line" },
                            column = new { type = "integer", description = "1-based column" }
                        }
                    }
                }
            },
            required = new string[0]
        };

        // ── Batch entry point ─────────────────────────────────────────

        public object Execute(JObject arguments)
        {
            var symbolsToken = arguments["symbols"] as JArray;
            if (symbolsToken != null && symbolsToken.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < symbolsToken.Count; i++)
                {
                    if (i > 0) sb.AppendLine().AppendLine();
                    var itemArgs = CloneObject(symbolsToken[i] as JObject);
                    CopyIfPresent(arguments, itemArgs, "depth");
                    CopyIfPresent(arguments, itemArgs, "includeErrorPaths");

                    var label = itemArgs["symbolName"]?.ToString() ??
                                $"{itemArgs["filePath"]}:{itemArgs["line"]}:{itemArgs["column"]}";
                    sb.Append("=== [").Append(i + 1).Append('/').Append(symbolsToken.Count)
                      .Append("] ").Append(label).Append(" ===").AppendLine();
                    sb.Append(ResultToString(ExecuteSingle(itemArgs)));
                }
                return sb.ToString().TrimEnd();
            }

            return ExecuteSingle(arguments);
        }

        // ── Single-symbol dispatch ────────────────────────────────────

        private object ExecuteSingle(JObject arguments)
        {
            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(
                _solution,
                arguments["symbolName"]?.ToString(),
                arguments["kind"]?.ToString(),
                arguments["filePath"]?.ToString(),
                arguments["line"]?.Value<int>() ?? 0,
                arguments["column"]?.Value<int>() ?? 0);

            if (error != null) return error;

            var depth = arguments["depth"]?.Value<int>() ?? 2;
            if (depth < 1) depth = 1;
            if (depth > 5) depth = 5;

            var includeErrorPaths = arguments["includeErrorPaths"]?.Value<bool>() ?? true;

            var sb = new StringBuilder();

            // Type → describe all non-trivial methods
            if (declaredElement is ITypeElement typeElement)
            {
                DescribeType(typeElement, depth, includeErrorPaths, sb);
                return sb.ToString().TrimEnd();
            }

            // Property → describe getter/setter flow
            if (declaredElement is IProperty property)
            {
                DescribeProperty(property, depth, includeErrorPaths, sb, "");
                return sb.ToString().TrimEnd();
            }

            // Method / constructor / anything with a body → describe its flow
            var declarations = declaredElement.GetDeclarations();
            if (declarations.Count == 0)
                return new { error = $"No source declarations for '{declaredElement.ShortName}' (may be from a compiled assembly)" };

            DescribeFunction(declaredElement, declarations[0], depth, includeErrorPaths, sb, "");
            return sb.ToString().TrimEnd();
        }

        // ── Type-level flow ───────────────────────────────────────────

        private void DescribeType(ITypeElement typeElement, int depth, bool includeErrorPaths, StringBuilder sb)
        {
            sb.Append("type ").AppendLine(typeElement.ShortName);

            var members = typeElement.GetMembers().ToList();

            // Collect methods with bodies (including constructors, excluding compiler-generated)
            var methods = members
                .OfType<IMethod>()
                .Where(m => !IsCompilerGenerated(m) && HasBody(m))
                .ToList();

            // Collect properties with non-trivial bodies
            var properties = members
                .OfType<IProperty>()
                .Where(HasNonTrivialPropertyBody)
                .ToList();

            // Constructors first, then regular methods alphabetically
            var ctors = methods.Where(m => m.ShortName == ".ctor").ToList();
            var regular = methods.Where(m => m.ShortName != ".ctor").OrderBy(m => m.ShortName).ToList();

            foreach (var ctor in ctors)
            {
                sb.AppendLine();
                var decl = ctor.GetDeclarations().FirstOrDefault();
                if (decl != null) DescribeFunction(ctor, decl, depth, includeErrorPaths, sb, "");
            }

            foreach (var method in regular)
            {
                sb.AppendLine();
                var decl = method.GetDeclarations().FirstOrDefault();
                if (decl != null) DescribeFunction(method, decl, depth, includeErrorPaths, sb, "");
            }

            foreach (var prop in properties)
            {
                sb.AppendLine();
                DescribeProperty(prop, depth, includeErrorPaths, sb, "");
            }

            if (ctors.Count == 0 && regular.Count == 0 && properties.Count == 0)
                sb.AppendLine("  (no non-trivial method bodies in source)");
        }

        // ── Function-level flow (method, constructor, accessor, etc.) ─

        private void DescribeFunction(IDeclaredElement element, IDeclaration decl,
            int depth, bool includeErrorPaths, StringBuilder sb, string indent)
        {
            sb.Append(indent).AppendLine(PsiHelpers.FormatSignature(element) + ":");

            // Find block body among declaration children
            var body = FindFirst<IBlock>(decl);
            if (body != null)
            {
                WalkBlock(body, depth, includeErrorPaths, sb, indent + "  ", 0);
                return;
            }

            // Expression body (=> expr) — extract from declaration text
            var declText = decl.GetText();
            if (declText != null)
            {
                var arrowIdx = declText.IndexOf("=>");
                if (arrowIdx >= 0)
                {
                    var exprText = declText.Substring(arrowIdx + 2).TrimEnd(';', ' ', '\n', '\r', '\t').Trim();
                    sb.Append(indent).Append("  → ").AppendLine(Compact(exprText));

                    // Try to inline any invocations in the expression body
                    var invocation = FindFirst<IInvocationExpression>(decl);
                    if (invocation != null && depth > 1)
                        TryInlineInvocation(invocation, depth - 1, includeErrorPaths, sb, indent + "    ");
                    return;
                }
            }

            sb.Append(indent).AppendLine("  (no body — abstract or extern)");
        }

        // ── Property flow ─────────────────────────────────────────────

        private void DescribeProperty(IProperty property, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent)
        {
            sb.Append(indent).AppendLine(PsiHelpers.FormatSignature(property) + ":");

            var described = false;
            foreach (var decl in property.GetDeclarations())
            {
                // Look for accessor declarations (get/set/init bodies)
                foreach (var child in decl.Children())
                {
                    if (!(child is IAccessorDeclaration accessor)) continue;
                    var accText = accessor.GetText()?.TrimStart() ?? "";
                    var kind = accText.StartsWith("set") ? "set"
                             : accText.StartsWith("init") ? "init"
                             : "get";

                    var accBody = FindFirst<IBlock>(accessor);
                    if (accBody != null)
                    {
                        sb.Append(indent).Append("  ").Append(kind).AppendLine(":");
                        WalkBlock(accBody, depth, includeErrorPaths, sb, indent + "    ", 0);
                        described = true;
                    }
                    else
                    {
                        // Expression-bodied accessor
                        var accDeclText = accessor.GetText();
                        var arrowIdx = accDeclText?.IndexOf("=>") ?? -1;
                        if (arrowIdx >= 0)
                        {
                            var exprText = accDeclText.Substring(arrowIdx + 2).TrimEnd(';', ' ', '\n', '\r', '\t').Trim();
                            sb.Append(indent).Append("  ").Append(kind).Append(" → ")
                              .AppendLine(Compact(exprText));
                            described = true;
                        }
                    }
                }

                // Expression-bodied property (int Foo => 42)
                if (!described)
                {
                    var propText = decl.GetText();
                    var arrowIdx = propText?.IndexOf("=>") ?? -1;
                    if (arrowIdx >= 0)
                    {
                        var exprText = propText.Substring(arrowIdx + 2).TrimEnd(';', ' ', '\n', '\r', '\t').Trim();
                        sb.Append(indent).Append("  → ").AppendLine(Compact(exprText));
                        described = true;
                    }
                }
            }

            if (!described)
                sb.Append(indent).AppendLine("  (auto-property or no source body)");
        }

        // ── Block/statement walking ───────────────────────────────────

        private void WalkBlock(IBlock block, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent, int nestLevel)
        {
            var statements = block.Statements.ToList();
            var step = 0;

            foreach (var stmt in statements)
                WalkStatement(stmt, depth, includeErrorPaths, sb, indent, nestLevel, ref step);

            if (step == 0)
                sb.Append(indent).AppendLine("(empty)");
        }

        private void WalkStatementBody(ICSharpStatement body, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent, int nestLevel)
        {
            if (body is IBlock block)
                WalkBlock(block, depth, includeErrorPaths, sb, indent, nestLevel);
            else if (body != null)
            {
                var step = 0;
                WalkStatement(body, depth, includeErrorPaths, sb, indent, nestLevel, ref step);
            }
        }

        private void WalkStatement(ICSharpStatement stmt, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent, int nestLevel, ref int step)
        {
            var comment = GetPrecedingComment(stmt);

            // ─ Guard clauses: if (condition) → return/throw ─
            if (stmt is IIfStatement guardIf && IsGuardClause(guardIf))
            {
                if (!includeErrorPaths && IsErrorGuard(guardIf)) return;

                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" guard: if (")
                  .Append(Compact(guardIf.Condition?.GetText())).Append(") → ");

                var thenBody = UnwrapBlock(guardIf.Then);
                if (thenBody is IReturnStatement ret)
                {
                    sb.Append("return");
                    if (ret.Value != null) sb.Append(' ').Append(Compact(ret.Value.GetText()));
                }
                else if (thenBody is IThrowStatement throwStmt)
                    sb.Append("throw ").Append(Compact(throwStmt.Exception?.GetText()));
                else
                    sb.Append(Compact(thenBody?.GetText()));

                AppendComment(sb, comment);
                sb.AppendLine();
                return;
            }

            // ─ If/else branches ─
            if (stmt is IIfStatement ifStmt)
            {
                step++;
                DescribeIfChain(ifStmt, depth, includeErrorPaths, sb, indent,
                    StepLabel(nestLevel, step), nestLevel, comment);
                return;
            }

            // ─ Switch ─
            if (stmt is ISwitchStatement switchStmt)
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" branch on ")
                  .Append(Compact(ExtractSwitchCondition(switchStmt))).Append(':');
                AppendComment(sb, comment);
                sb.AppendLine();

                foreach (var section in switchStmt.Sections)
                {
                    // Collect case labels from section children
                    var labels = new List<string>();
                    foreach (var child in section.Children())
                    {
                        if (child is ICSharpStatement) break; // past labels
                        var txt = child.GetText()?.Trim();
                        if (txt != null && (txt.StartsWith("case ") || txt.StartsWith("default")))
                            labels.Add(txt.TrimEnd(':'));
                    }
                    var labelText = labels.Count > 0 ? string.Join(", ", labels) : Compact(section.GetText(), 40);
                    sb.Append(indent).Append("   ").Append(labelText).AppendLine(" →");

                    var subStep = 0;
                    foreach (var s in section.Statements)
                    {
                        if (s is IBreakStatement) continue;
                        WalkStatement(s, depth, includeErrorPaths, sb, indent + "     ", nestLevel + 1, ref subStep);
                    }
                }
                return;
            }

            // ─ Try/catch/finally ─
            if (stmt is ITryStatement tryStmt)
            {
                if (!includeErrorPaths)
                {
                    // Just walk the try body, skip error handling
                    var tryBody = FindFirst<IBlock>(tryStmt);
                    if (tryBody != null)
                        WalkBlock(tryBody, depth, includeErrorPaths, sb, indent, nestLevel);
                    return;
                }

                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" try:");
                AppendComment(sb, comment);
                sb.AppendLine();

                // Try block — first IBlock child of the try statement
                var firstBlock = FindFirst<IBlock>(tryStmt);
                if (firstBlock != null)
                    WalkBlock(firstBlock, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);

                // Catch clauses
                foreach (var child in tryStmt.Children())
                {
                    if (child is ISpecificCatchClause specific)
                    {
                        var exType = specific.ExceptionType?.GetPresentableName(CSharpLanguage.Instance) ?? "Exception";
                        sb.Append(indent).Append("   catch ").Append(Compact(exType)).AppendLine(" →");
                        var catchBody = FindFirst<IBlock>(specific);
                        if (catchBody != null)
                            WalkBlock(catchBody, depth, includeErrorPaths, sb, indent + "     ", nestLevel + 2);
                    }
                    else if (child is IGeneralCatchClause general)
                    {
                        sb.Append(indent).AppendLine("   catch (any) →");
                        var catchBody = FindFirst<IBlock>(general);
                        if (catchBody != null)
                            WalkBlock(catchBody, depth, includeErrorPaths, sb, indent + "     ", nestLevel + 2);
                    }
                }

                // Finally block — find by looking for a node whose text starts with "finally"
                foreach (var child in tryStmt.Children())
                {
                    var childText = child.GetText()?.TrimStart();
                    if (childText != null && childText.StartsWith("finally"))
                    {
                        sb.Append(indent).AppendLine("   finally →");
                        var finallyBody = FindFirst<IBlock>(child);
                        if (finallyBody != null)
                            WalkBlock(finallyBody, depth, includeErrorPaths, sb, indent + "     ", nestLevel + 2);
                        break;
                    }
                }
                return;
            }

            // ─ Foreach loop ─
            if (stmt is IForeachStatement foreachStmt)
            {
                step++;
                var iterVar = ExtractForeachVariable(foreachStmt) ?? "item";
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" loop: foreach ")
                  .Append(iterVar).Append(" in ").Append(Compact(foreachStmt.Collection?.GetText()));
                AppendComment(sb, comment);
                sb.AppendLine();
                WalkStatementBody(foreachStmt.Body, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                return;
            }

            // ─ For loop ─
            if (stmt is IForStatement forStmt)
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" loop: for (")
                  .Append(Compact(forStmt.Condition?.GetText() ?? "...")).Append(')');
                AppendComment(sb, comment);
                sb.AppendLine();
                WalkStatementBody(forStmt.Body, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                return;
            }

            // ─ While loop ─
            if (stmt is IWhileStatement whileStmt)
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" loop: while (")
                  .Append(Compact(whileStmt.Condition?.GetText())).Append(')');
                AppendComment(sb, comment);
                sb.AppendLine();
                WalkStatementBody(whileStmt.Body, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                return;
            }

            // ─ Do-while loop ─
            if (stmt is IDoStatement doStmt)
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" loop: do ... while (")
                  .Append(Compact(doStmt.Condition?.GetText())).Append(')');
                AppendComment(sb, comment);
                sb.AppendLine();
                WalkStatementBody(doStmt.Body, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                return;
            }

            // ─ Using statement ─
            if (stmt is IUsingStatement usingStmt)
            {
                step++;
                var usingHeader = ExtractHeader(usingStmt);
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" using ").Append(usingHeader);
                AppendComment(sb, comment);
                sb.AppendLine();
                WalkStatementBody(usingStmt.Body, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                return;
            }

            // ─ Lock statement ─
            if (stmt is ILockStatement lockStmt)
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" lock (")
                  .Append(Compact(lockStmt.Monitor?.GetText())).Append(')');
                AppendComment(sb, comment);
                sb.AppendLine();
                WalkStatementBody(lockStmt.Body, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                return;
            }

            // ─ Return ─
            if (stmt is IReturnStatement returnStmt)
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" return");
                if (returnStmt.Value != null)
                    sb.Append(' ').Append(Compact(returnStmt.Value.GetText()));
                AppendComment(sb, comment);
                sb.AppendLine();
                return;
            }

            // ─ Throw ─
            if (stmt is IThrowStatement throwStatement)
            {
                if (!includeErrorPaths) return;
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" throw ");
                sb.Append(throwStatement.Exception != null
                    ? Compact(throwStatement.Exception.GetText())
                    : "(rethrow)");
                AppendComment(sb, comment);
                sb.AppendLine();
                return;
            }

            // ─ Expression statements (calls, assignments) ─
            if (stmt is IExpressionStatement exprStmt)
            {
                DescribeExpression(exprStmt, depth, includeErrorPaths, sb, indent, nestLevel, ref step, comment);
                return;
            }

            // ─ Declaration statements (var x = ...) ─
            if (stmt is IDeclarationStatement)
            {
                DescribeDeclaration(stmt, depth, includeErrorPaths, sb, indent, nestLevel, ref step, comment);
                return;
            }

            // ─ Skip noise ─
            if (stmt is IBreakStatement || stmt is IContinueStatement || stmt is IEmptyStatement)
                return;

            // ─ Fallback: unknown statement type — emit text ─
            step++;
            sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(' ')
              .Append(Compact(stmt.GetText()));
            AppendComment(sb, comment);
            sb.AppendLine();
        }

        // ── Expression statement handling ─────────────────────────────

        private void DescribeExpression(IExpressionStatement exprStmt, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent, int nestLevel, ref int step, string comment)
        {
            var expr = exprStmt.Expression;
            var isAwait = expr is IAwaitExpression;
            if (isAwait)
                expr = ((IAwaitExpression)expr).Task;

            // Method invocation
            if (expr is IInvocationExpression invocation)
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(' ');
                if (isAwait) sb.Append("await ");
                sb.Append(FormatInvocation(invocation));
                AppendComment(sb, comment);
                sb.AppendLine();

                if (depth > 1)
                    TryInlineInvocation(invocation, depth - 1, includeErrorPaths, sb, indent + "   ");
                return;
            }

            // Assignment (possibly with invocation on RHS)
            if (expr is IAssignmentExpression assignment)
            {
                var dest = Compact(assignment.Dest?.GetText());
                var source = assignment.Source;
                var sourceIsAwait = source is IAwaitExpression;
                if (sourceIsAwait)
                    source = ((IAwaitExpression)source).Task;

                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(' ').Append(dest).Append(" = ");
                if (sourceIsAwait) sb.Append("await ");

                if (source is IInvocationExpression srcInvocation)
                {
                    sb.Append(FormatInvocation(srcInvocation));
                    AppendComment(sb, comment);
                    sb.AppendLine();
                    if (depth > 1)
                        TryInlineInvocation(srcInvocation, depth - 1, includeErrorPaths, sb, indent + "   ");
                }
                else
                {
                    sb.Append(Compact(source?.GetText()));
                    AppendComment(sb, comment);
                    sb.AppendLine();
                }
                return;
            }

            // Other expressions
            step++;
            sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(' ');
            if (isAwait) sb.Append("await ");
            sb.Append(Compact(expr.GetText()));
            AppendComment(sb, comment);
            sb.AppendLine();
        }

        // ── Declaration statement handling ─────────────────────────────

        private void DescribeDeclaration(ICSharpStatement stmt, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent, int nestLevel, ref int step, string comment)
        {
            // Check if the declaration contains an invocation (interesting) or is trivial
            var invocation = FindFirst<IInvocationExpression>(stmt);
            if (invocation != null)
            {
                // Extract variable name from declaration text (before '=')
                var text = stmt.GetText()?.Trim() ?? "";
                var eqIdx = text.IndexOf('=');
                var varName = "var";
                if (eqIdx > 0)
                {
                    var lhs = text.Substring(0, eqIdx).Trim();
                    var parts = lhs.Split(' ', '\t');
                    varName = parts[parts.Length - 1];
                }

                // Check for await
                var awaitExpr = FindFirst<IAwaitExpression>(stmt);
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(' ').Append(varName).Append(" = ");
                if (awaitExpr != null) sb.Append("await ");
                sb.Append(FormatInvocation(invocation));
                AppendComment(sb, comment);
                sb.AppendLine();

                if (depth > 1)
                    TryInlineInvocation(invocation, depth - 1, includeErrorPaths, sb, indent + "   ");
                return;
            }

            // Simple declaration — only emit if it has an initializer (skip bare "int x;")
            var stmtText = stmt.GetText()?.Trim() ?? "";
            if (stmtText.Contains("="))
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(' ').Append(Compact(stmtText));
                AppendComment(sb, comment);
                sb.AppendLine();
            }
        }

        // ── If/else chain ─────────────────────────────────────────────

        private void DescribeIfChain(IIfStatement ifStmt, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent, string label, int nestLevel, string comment)
        {
            sb.Append(indent).Append(label).Append(" if (")
              .Append(Compact(ifStmt.Condition?.GetText())).Append(')');
            AppendComment(sb, comment);
            sb.AppendLine(" →");

            WalkStatementBody(ifStmt.Then, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);

            var elseBody = ifStmt.Else;
            while (elseBody != null)
            {
                if (elseBody is IIfStatement elseIf)
                {
                    sb.Append(indent).Append("   else if (")
                      .Append(Compact(elseIf.Condition?.GetText())).AppendLine(") →");
                    WalkStatementBody(elseIf.Then, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                    elseBody = elseIf.Else;
                }
                else
                {
                    sb.Append(indent).AppendLine("   else →");
                    WalkStatementBody(elseBody, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                    break;
                }
            }
        }

        // ── Call inlining ─────────────────────────────────────────────

        private void TryInlineInvocation(IInvocationExpression invocation, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent)
        {
            if (depth <= 0) return;

            var target = ResolveInvocationTarget(invocation);
            if (target == null) return;

            InlineBody(target, depth, includeErrorPaths, sb, indent);
        }

        private void InlineBody(IDeclaredElement element, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent)
        {
            // Local function — use the declaration's Body directly
            if (element is ILocalFunction)
            {
                var decls = element.GetDeclarations();
                if (decls.Count == 0) return;
                if (decls[0] is ILocalFunctionDeclaration lfd)
                {
                    if (lfd.Body != null)
                    {
                        if (depth >= 2)
                            WalkBlock(lfd.Body, depth, includeErrorPaths, sb, indent, 1);
                        else
                            foreach (var stmt in lfd.Body.Statements)
                            {
                                var summary = SummarizeStatement(stmt, depth, includeErrorPaths);
                                if (summary != null)
                                    sb.Append(indent).Append("└─ ").AppendLine(summary);
                            }
                        return;
                    }
                    // Expression body — fall through to text-based extraction
                    var lfdText = lfd.GetText();
                    if (lfdText != null)
                    {
                        var arrowIdx = lfdText.IndexOf("=>");
                        if (arrowIdx >= 0)
                        {
                            var exprText = lfdText.Substring(arrowIdx + 2).TrimEnd(';', ' ', '\n', '\r', '\t').Trim();
                            sb.Append(indent).Append("└─ → ").AppendLine(Compact(exprText));
                        }
                    }
                    return;
                }
            }

            // Regular method
            var declarations = element.GetDeclarations();
            if (declarations.Count == 0) return;

            var decl = declarations[0];

            // Block body
            var body = FindFirst<IBlock>(decl);
            if (body != null)
            {
                // With remaining depth ≥ 2, use full structured walk so inlined methods
                // get the same rich treatment (branches, loops, nested inlines).
                // At depth 1, use compact summaries (└─ one-liners).
                if (depth >= 2)
                {
                    WalkBlock(body, depth, includeErrorPaths, sb, indent, 1);
                }
                else
                {
                    foreach (var stmt in body.Statements)
                    {
                        var summary = SummarizeStatement(stmt, depth, includeErrorPaths);
                        if (summary != null)
                            sb.Append(indent).Append("└─ ").AppendLine(summary);
                    }
                }
                return;
            }

            // Expression body
            var declText = decl.GetText();
            if (declText != null)
            {
                var arrowIdx = declText.IndexOf("=>");
                if (arrowIdx >= 0)
                {
                    var exprText = declText.Substring(arrowIdx + 2).TrimEnd(';', ' ', '\n', '\r', '\t').Trim();
                    sb.Append(indent).Append("└─ → ").AppendLine(Compact(exprText));
                }
            }
        }

        // ── Statement summarization (for inlined calls) ───────────────

        private string SummarizeStatement(ICSharpStatement stmt, int depth, bool includeErrorPaths)
        {
            if (stmt is IIfStatement ifStmt)
            {
                if (IsGuardClause(ifStmt))
                    return "guard: if (" + Compact(ifStmt.Condition?.GetText()) + ") → early exit";
                return "if (" + Compact(ifStmt.Condition?.GetText()) + ") → ...";
            }

            if (stmt is ISwitchStatement switchStmt)
                return "switch on " + Compact(ExtractSwitchCondition(switchStmt));

            if (stmt is ITryStatement)
                return includeErrorPaths ? "try/catch block" : null;

            if (stmt is IForeachStatement foreachStmt)
            {
                var iterVar = ExtractForeachVariable(foreachStmt) ?? "item";
                return "foreach " + iterVar + " in " + Compact(foreachStmt.Collection?.GetText());
            }

            if (stmt is IForStatement forStmt)
                return "loop: for (" + Compact(forStmt.Condition?.GetText() ?? "...") + ")";

            if (stmt is IWhileStatement whileStmt)
                return "loop: while (" + Compact(whileStmt.Condition?.GetText()) + ")";

            if (stmt is IDoStatement doStmt)
                return "loop: do ... while (" + Compact(doStmt.Condition?.GetText()) + ")";

            if (stmt is IReturnStatement returnStmt)
                return returnStmt.Value != null ? "return " + Compact(returnStmt.Value.GetText()) : "return";

            if (stmt is IThrowStatement throwStmt)
                return includeErrorPaths ? "throw " + Compact(throwStmt.Exception?.GetText()) : null;

            if (stmt is IExpressionStatement exprStmt)
            {
                var expr = exprStmt.Expression;
                var prefix = "";
                if (expr is IAwaitExpression awaitExpr)
                {
                    expr = awaitExpr.Task;
                    prefix = "await ";
                }

                if (expr is IInvocationExpression inv)
                    return prefix + FormatInvocation(inv);

                if (expr is IAssignmentExpression assign)
                    return Compact(assign.Dest?.GetText()) + " = " + Compact(assign.Source?.GetText());

                return prefix + Compact(expr.GetText());
            }

            if (stmt is IDeclarationStatement)
            {
                var inv = FindFirst<IInvocationExpression>(stmt);
                if (inv != null)
                {
                    var text = stmt.GetText()?.Trim() ?? "";
                    var eqIdx = text.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var parts = text.Substring(0, eqIdx).Trim().Split(' ', '\t');
                        return parts[parts.Length - 1] + " = " + FormatInvocation(inv);
                    }
                    return FormatInvocation(inv);
                }
                // Only include initialized declarations
                var declText = stmt.GetText()?.Trim();
                return declText != null && declText.Contains("=") ? Compact(declText) : null;
            }

            if (stmt is IBreakStatement || stmt is IContinueStatement || stmt is IEmptyStatement)
                return null;

            return Compact(stmt.GetText());
        }

        // ── Invocation resolution ─────────────────────────────────────

        private IDeclaredElement ResolveInvocationTarget(IInvocationExpression invocation)
        {
            IDeclaredElement resolved = null;

            // Strategy 1: Resolve via references on the invoked expression
            var invokedExpr = invocation.InvokedExpression;
            if (invokedExpr != null)
                resolved = ResolveCallableFromNode(invokedExpr);

            // Strategy 2: Resolve via references on the invocation node itself
            if (resolved == null)
                resolved = ResolveCallableFromNode(invocation);

            // Strategy 3: Walk up the tree looking for resolvable references
            // (mirrors PsiHelpers.GetDeclaredElement which is proven to work)
            if (resolved == null && invokedExpr != null)
            {
                ITreeNode current = invokedExpr;
                for (int depth = 0; current != null && depth < 5; depth++)
                {
                    foreach (var reference in current.GetReferences())
                    {
                        var result = reference.Resolve();
                        if (result.DeclaredElement is IMethod m)
                        {
                            resolved = m;
                            break;
                        }
                        if (result.DeclaredElement is ILocalFunction lf)
                        {
                            resolved = lf;
                            break;
                        }
                    }
                    if (resolved != null) break;
                    current = current.Parent;
                    // Stop walking up if we've left the invocation expression
                    if (current == invocation.Parent) break;
                }
            }

            if (resolved == null) return null;

            // For abstract/virtual/interface methods, try to find the single concrete impl
            if (resolved is IMethod method &&
                (method.IsAbstract || method.IsVirtual ||
                 method.GetContainingType() is IInterface))
            {
                var concrete = FindSingleImplementation(method);
                return concrete ?? method;
            }

            return resolved;
        }

        private static IDeclaredElement ResolveCallableFromNode(ITreeNode node)
        {
            foreach (var reference in node.GetReferences())
            {
                var result = reference.Resolve();
                if (result.DeclaredElement is IMethod method)
                    return method;
                if (result.DeclaredElement is ILocalFunction localFunc)
                    return localFunc;
            }
            return null;
        }

        private IMethod FindSingleImplementation(IMethod method)
        {
            var psiServices = _solution.GetPsiServices();
            var implementations = new List<IMethod>();

            psiServices.Finder.FindImplementingMembers(
                method,
                method.GetSearchDomain(),
                new FindResultConsumer(findResult =>
                {
                    if (implementations.Count >= 2) return FindExecution.Stop;
                    if (findResult is FindResultOverridableMember overrideResult &&
                        overrideResult.OverridableMember is IMethod impl)
                        implementations.Add(impl);
                    return FindExecution.Continue;
                }),
                true,
                NullProgressIndicator.Create());

            return implementations.Count == 1 ? implementations[0] : null;
        }

        // ── Helper methods ────────────────────────────────────────────

        private static T FindFirst<T>(ITreeNode node) where T : class, ITreeNode
        {
            for (var child = node.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is T match) return match;
                var found = FindFirst<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private static bool IsGuardClause(IIfStatement ifStmt)
        {
            if (ifStmt.Else != null) return false;
            var body = UnwrapBlock(ifStmt.Then);
            return body is IReturnStatement || body is IThrowStatement;
        }

        private static bool IsErrorGuard(IIfStatement ifStmt)
        {
            return UnwrapBlock(ifStmt.Then) is IThrowStatement;
        }

        private static ICSharpStatement UnwrapBlock(ICSharpStatement stmt)
        {
            if (stmt is IBlock block && block.Statements.Count == 1)
                return block.Statements[0];
            return stmt;
        }

        private static bool HasBody(IMethod method)
        {
            var decls = method.GetDeclarations();
            if (decls.Count == 0) return false;
            var decl = decls[0];
            // Has a block body
            if (FindFirst<IBlock>(decl) != null) return true;
            // Has an expression body
            var text = decl.GetText();
            return text != null && text.Contains("=>");
        }

        private static bool HasNonTrivialPropertyBody(IProperty property)
        {
            foreach (var decl in property.GetDeclarations())
            {
                for (var child = decl.FirstChild; child != null; child = child.NextSibling)
                {
                    if (child is IAccessorDeclaration accessor)
                    {
                        var body = FindFirst<IBlock>(accessor);
                        if (body != null && body.Statements.Count > 0) return true;
                        var text = accessor.GetText();
                        if (text != null && text.Contains("=>")) return true;
                    }
                }
                // Expression-bodied property
                var propText = decl.GetText();
                if (propText != null && propText.Contains("=>")) return true;
            }
            return false;
        }

        private static bool IsCompilerGenerated(IMethod method)
        {
            var name = method.ShortName;
            if (name.StartsWith("$") || name.StartsWith("<")) return true;
            if (name.StartsWith("get_") || name.StartsWith("set_") ||
                name.StartsWith("add_") || name.StartsWith("remove_")) return true;
            if (name == "op_Equality" || name == "op_Inequality") return true;
            return false;
        }

        private static string GetPrecedingComment(ITreeNode node)
        {
            var comments = new List<string>();
            var sibling = node.PrevSibling;
            while (sibling != null)
            {
                if (sibling is ICommentNode commentNode)
                {
                    var text = commentNode.CommentText?.Trim();
                    if (!string.IsNullOrEmpty(text))
                        comments.Insert(0, text);
                }
                else if (!(sibling is IWhitespaceNode))
                    break;
                sibling = sibling.PrevSibling;
            }
            return comments.Count > 0 ? string.Join("; ", comments) : null;
        }

        private static void AppendComment(StringBuilder sb, string comment)
        {
            if (comment != null)
                sb.Append("  // ").Append(comment);
        }

        private static string StepLabel(int nestLevel, int step)
        {
            if (nestLevel == 0) return step + ".";
            if (nestLevel == 1 && step <= 26) return ((char)('a' + step - 1)) + ".";
            return "-";
        }

        private static string FormatInvocation(IInvocationExpression invocation)
        {
            return Compact(invocation.GetText());
        }

        private static string ExtractSwitchCondition(ISwitchStatement switchStmt)
        {
            // Find the first expression child (the governing expression)
            for (var child = switchStmt.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is ICSharpExpression expr)
                    return expr.GetText()?.Trim();
            }
            // Fallback: extract from "switch (...)" text
            var text = switchStmt.GetText() ?? "";
            var open = text.IndexOf('(');
            var close = text.IndexOf(')');
            if (open >= 0 && close > open)
                return text.Substring(open + 1, close - open - 1).Trim();
            return "...";
        }

        private static string ExtractForeachVariable(IForeachStatement foreachStmt)
        {
            // Extract variable name from "foreach (Type varName in ...)"
            // Find the first IDeclaration descendant for the iterator variable
            foreach (var child in foreachStmt.Children())
            {
                if (child is IDeclaration decl)
                {
                    var name = decl.DeclaredElement?.ShortName;
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            // Fallback: parse from text — find word before "in"
            var text = foreachStmt.GetText() ?? "";
            var inIdx = text.IndexOf(" in ");
            if (inIdx > 0)
            {
                var before = text.Substring(0, inIdx).TrimEnd();
                var lastSpace = before.LastIndexOf(' ');
                if (lastSpace >= 0)
                    return before.Substring(lastSpace + 1);
            }
            return "item";
        }

        private static string ExtractHeader(ITreeNode node)
        {
            // Get text up to the first '{' (i.e., the header of a using/lock/etc.)
            var text = node.GetText();
            if (text == null) return "...";
            var braceIdx = text.IndexOf('{');
            if (braceIdx > 0)
                return Compact(text.Substring(0, braceIdx).Trim(), 80);
            return Compact(text, 80);
        }

        private static string Compact(string text, int maxLength = 100)
        {
            if (string.IsNullOrEmpty(text)) return "...";
            text = Regex.Replace(text.Trim(), @"\s+", " ");
            if (text.Length <= maxLength) return text;
            return text.Substring(0, maxLength - 3) + "...";
        }

        private static JObject CloneObject(JObject source)
        {
            if (source == null) return new JObject();
            return (JObject)source.DeepClone();
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
    }
}
