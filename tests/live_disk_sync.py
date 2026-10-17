#!/usr/bin/env python3
"""Check that MCP answers reflect edits made on disk while Rider is in the background.

Edits the fixture file in place and restores it at the end. Use a disposable C# file in an
SDK-style project (globbing) that compiles cleanly before the run.
"""
import argparse
import json
import pathlib
import time
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url', default='http://127.0.0.1:23741/')
    parser.add_argument('--solution', required=True)
    parser.add_argument('--file', required=True, help='Absolute path to a disposable, error-free C# file')
    parser.add_argument('--autosave-wait', type=int, default=20, help='Seconds to watch for Rider writing the file back')
    args = parser.parse_args()

    fixture = pathlib.Path(args.file)
    created = fixture.with_name('McpDiskSyncProbe.cs')
    original = fixture.read_text()

    def call(name, **arguments):
        request = urllib.request.Request(
            args.url,
            json.dumps({
                'jsonrpc': '2.0', 'id': 1, 'method': 'tools/call',
                'params': {'name': name, 'arguments': dict(arguments, solutionName=args.solution)},
            }).encode(),
            {'Content-Type': 'application/json'},
        )
        started = time.monotonic()
        with urllib.request.urlopen(request, timeout=130) as response:
            result = json.load(response)
        print(f'  {name}: {time.monotonic() - started:.2f}s')
        return '\n'.join(item.get('text', '') for item in result['result']['content'])

    def check(condition, message, text):
        if not condition:
            raise SystemExit(f'FAIL: {message}\n{text}')
        print(f'ok: {message}')

    try:
        # Health and instructions answer without a tool call.
        with urllib.request.urlopen(args.url.rstrip('/') + '/health', timeout=5) as response:
            health = json.load(response)
        check('solutions' in health and 'indexing' in health, 'GET /health answers', health)

        initialize = urllib.request.Request(
            args.url,
            json.dumps({'jsonrpc': '2.0', 'id': 0, 'method': 'initialize', 'params': {'capabilities': {}}}).encode(),
            {'Content-Type': 'application/json'},
        )
        with urllib.request.urlopen(initialize, timeout=5) as response:
            instructions = json.load(response)['result'].get('instructions', '')
        check('get_file_errors' in instructions, 'initialize returns instructions', instructions)

        # B1: an edit made on disk is seen by the very next call.
        text = call('get_file_errors', filePath=args.file)
        check(' 0 diagnostics' in text, 'fixture starts clean', text)

        fixture.write_text(original.rstrip() + '\n// probe\nclass McpProbe { void M() { var x = undefinedSymbolForMcpProbe; } }\n')
        text = call('get_file_errors', filePath=args.file)
        check('undefinedSymbolForMcpProbe' in text, 'disk edit is visible immediately', text)

        fixture.write_text(original)
        text = call('get_file_errors', filePath=args.file)
        check(' 0 diagnostics' in text, 'disk revert is visible immediately', text)
        check('differs from disk' not in text, 'Rider reloaded the revert', text)

        # A reload must not leave Rider holding "unsaved" text that autosave later writes over disk.
        reverted = fixture.stat().st_mtime_ns
        time.sleep(args.autosave_wait)
        check(fixture.read_text() == original and fixture.stat().st_mtime_ns == reverted,
              f'Rider wrote nothing back within {args.autosave_wait}s', fixture.read_text())

        # B1: a file created by a shell is found in the next call.
        created.write_text('namespace McpProbe { class Created { void M() { var x = alsoUndefinedForMcpProbe; } } }\n')
        text = call('get_file_errors', filePath=str(created))
        check('alsoUndefinedForMcpProbe' in text, 'new file is found and checked', text)

        # B1: fix_usings never saves old text over a newer disk edit.
        edited = 'using System;\n' + original
        fixture.write_text(edited)
        text = call('fix_usings', filePath=args.file)
        check(fixture.read_text().startswith('using System;'), 'disk edit survives fix_usings', text)
        print(f'  fix_usings said: {text.splitlines()[0] if text else text}')

        print('PASS')
    finally:
        fixture.write_text(original)
        if created.exists():
            created.unlink()


if __name__ == '__main__':
    main()
