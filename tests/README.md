# Rider navigation regression check

For plugin maintainers with the candidate plugin loaded in Rider. Use a disposable
NUnit solution, with a `Calculator.Add` method called by one test. Keep the test
file open in the editor and allow indexing to finish.

1. Build the backend and plugin: `./build-plugin.sh`. If the bundled JBR is too
   new for Gradle, set `JAVA_HOME` to a JDK 21 installation for this command.
2. Run concurrent MCP reads for about 30 seconds:

   ```sh
   python3 tests/live_concurrency.py \
     --solution McpVerification \
     --file Navigation.Tests/NavigationTests.cs \
     --query Calculator --symbol Navigation.Tests.Calculator.Add
   ```

   Set `--url` to a peer's HTTP endpoint to bypass the primary instance when
   testing a candidate loaded only in that peer. The script uses four workers
   for search, usages, suggestion dry runs, and diagnostics. It fails on HTTP,
   JSON-RPC, tool, or fixture-resolution errors. It does not apply fixes.
3. While it runs, repeatedly navigate from the `Add` call to its declaration
   and back in Rider. Confirm the caret reaches the declaration and the editor
   remains responsive. Also edit/save the disposable file to exercise writes
   interrupting background reads.
4. On a block-scoped namespace, call `apply_suggestions` with
   `inspectionIds: "ArrangeNamespaceBody", dryRun: true`. Confirm the file is
   unchanged. Repeat without `dryRun`, confirm conversion to a file-scoped
   namespace, and run the fixture's NUnit test.
5. Exercise `apply_quick_fix` on a known fix in the disposable file and confirm
   the edit. It shares the background-analysis/main-thread-execution path.

## Recorded run

Rider 2026.2 on macOS, SDK 2025.3.3:

- Release backend: zero warnings/errors. Plugin ZIP built with JDK 21.
- 1,200 calls across four workers completed without tool errors; slowest HTTP
  request was approximately 50 ms (150 ms including the harness pacing delay).
- Five concurrent navigation cycles completed; the caret reached `Add` at
  line 17, column 14. UI automation needed one-second waits for modal dialogs.
- Suggestion dry-run reported the namespace conversion; actual apply changed
  the fixture to a file-scoped namespace. The NUnit test passed.
- A position-based `CheckNamespaceQuickFix` applied successfully afterward.
- Absolute and solution-relative file paths resolved successfully.

This is a short smoke test on a small solution. It does not establish that the
intermittent production deadlock can never recur. Large-solution endurance,
request-timeout/solution-close cancellation, and forced invalidation between
analysis and application remain additional manual checks. The SDK provides the
invalidation/retry mechanism; this test does not deterministically force it.
