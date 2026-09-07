#!/usr/bin/env python3
"""Exercise MCP background reads while navigating in a running Rider instance."""
import argparse
import concurrent.futures
import json
import time
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url', default='http://127.0.0.1:23741/')
    parser.add_argument('--solution', required=True)
    parser.add_argument('--file', required=True, help='Path to an indexed disposable C# test file')
    parser.add_argument('--query', required=True, help='A type name present in the fixture')
    parser.add_argument('--symbol', required=True, help='A qualified member name with at least one usage')
    parser.add_argument('--calls', type=int, default=1200)
    args = parser.parse_args()
    if args.calls < 4:
        parser.error('--calls must be at least 4')

    cases = [
        ('search_symbol', {'query': args.query}),
        ('find_usages', {'symbolName': args.symbol}),
        ('apply_suggestions', {'filePath': args.file, 'all': True, 'dryRun': True}),
        ('get_diagnostics', {'filePath': args.file}),
    ]

    def call(index):
        name, arguments = cases[index % len(cases)]
        request = urllib.request.Request(
            args.url,
            json.dumps({
                'jsonrpc': '2.0', 'id': index, 'method': 'tools/call',
                'params': {'name': name, 'arguments': dict(arguments, solutionName=args.solution)},
            }).encode(),
            {'Content-Type': 'application/json'},
        )
        started = time.monotonic()
        with urllib.request.urlopen(request, timeout=130) as response:
            result = json.load(response)
        if 'error' in result or result['result'].get('isError'):
            raise RuntimeError(result)
        text = '\n'.join(item.get('text', '') for item in result['result']['content'])
        if not text or '"error":' in text or text.lower().startswith('error:'):
            raise RuntimeError(text)
        if name == 'search_symbol' and ('0 results' in text or args.query not in text):
            raise RuntimeError('Fixture query did not resolve: ' + text)
        if name == 'find_usages' and ('0 usages' in text or 'usages in' not in text):
            raise RuntimeError('Fixture member has no usages: ' + text)
        if name == 'get_diagnostics' and 'diagnosticsCount' not in text:
            raise RuntimeError('Expected diagnostics response: ' + text)
        elapsed = time.monotonic() - started
        # Keep traffic active long enough to exercise IDE navigation in parallel.
        time.sleep(0.1)
        return elapsed

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        elapsed = list(pool.map(call, range(args.calls)))
    print(f'PASS: {len(elapsed)} calls; slowest request {max(elapsed):.3f}s')


if __name__ == '__main__':
    main()
