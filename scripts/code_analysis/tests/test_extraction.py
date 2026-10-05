import unittest

from scripts.code_analysis.contracts import digest
from scripts.code_analysis.extraction import extract_source


class SourceExtractionTests(unittest.TestCase):
    def extract(self, text, language, **extra):
        raw = text.encode('utf-8')
        file = {'fileId': 'a' * 64, 'sha256': digest(raw), 'language': language, **extra}
        return raw, extract_source(raw, file)

    def test_unicode_crlf_nested_decorated_exact_spans(self):
        text = '@trace\r\ndef outer(імя):\r\n    def inner():\r\n        return "привіт"\r\n    return inner()\r\nvalue = 3\r\n'
        raw, result = self.extract(text, 'python')
        self.assertEqual(result['actualOutcome'], 'VALID_EXTRACTED')
        outer = next(f for f in result['fragments'] if f['name'] == 'outer')
        inner = next(f for f in result['fragments'] if f['name'] == 'inner')
        module = next(f for f in result['fragments'] if f['kind'] == 'module_statement')
        self.assertEqual(raw[outer['startByte']:outer['endByte']], text[:text.index('\r\nvalue')].encode('utf-8'))
        self.assertEqual((outer['startLine'], outer['startColumn']), (1, 1))
        self.assertEqual((inner['startLine'], inner['startColumn']), (3, 5))
        self.assertEqual(raw[module['startByte']:module['endByte']], b'value = 3')
        self.assertEqual(inner['parentFragmentId'], outer['fragmentId'])
        for fragment in result['fragments']:
            self.assertEqual(digest(raw[fragment['startByte']:fragment['endByte']]), fragment['sha256'])
        self.assertLessEqual(result['coverage']['unionExecutableBytes'], len(raw))
        self.assertEqual(result['coverage']['unionAccountedBytes'] + result['coverage']['omittedBytes'], len(raw))

    def test_methods_constructors_context_and_tsx(self):
        cases = [
            ('javascript', 'class C { m(x) { return x + 1; } }', 'method_definition'),
            ('typescript', 'interface Shape { x: number }; const f = (x: number) => x + 1;', 'arrow_function'),
            ('java', 'class C { C() {} int m(int x) { return x; } }', 'constructor_declaration'),
            ('csharp', 'class C { public C() {} public int M(int x) { int Inner() => x; return Inner(); } }', 'local_function_statement'),
        ]
        for language, text, kind in cases:
            with self.subTest(language=language, kind=kind):
                raw, result = self.extract(text, language)
                self.assertEqual(result['actualOutcome'], 'VALID_EXTRACTED')
                self.assertIn(kind, {f['kind'] for f in result['fragments']})
                self.assertFalse(any(f['kind'] in ('class_declaration', 'interface_declaration') for f in result['fragments']))
                self.assertTrue(all(not c['searchable'] for c in result['contexts']))

    def test_ten_tsx_components_and_malformed_diagnostics(self):
        valid = [
            'const A = () => <div>A</div>;',
            'const B = (p) => <span title="x">{p}</span>;',
            'function C() { return <main>C</main>; }',
            'export function D({ n }: { n: number }) { return <p>{n}</p>; }',
            'const E = () => <ul>{[1].map(i => <li key={i}>{i}</li>)}</ul>;',
            'const F = () => <button type="button">ok</button>;',
            'function G(props: { t: string }) { return <h1>{props.t}</h1>; }',
            'const H = () => <section><header>H</header></section>;',
            'const I = () => <input aria-label="q" />;',
            'const J = () => <article data-id="1">J</article>;',
        ]
        for text in valid:
            raw, result = self.extract(text, 'typescript', dialect='tsx')
            self.assertEqual(result['actualOutcome'], 'VALID_EXTRACTED')
            self.assertTrue(result['searchableExecutable'])
            self.assertTrue(any(f['kind'] in {'arrow_function', 'function_declaration'} for f in result['fragments']))
        _, broken = self.extract('const Z = () => <div>', 'typescript', dialect='tsx')
        self.assertEqual(broken['actualOutcome'], 'INVALID_PARSE')
        self.assertFalse(broken['searchableExecutable'])

    def test_inclusive_end_is_last_complete_multibyte_codepoint(self):
        for statement, expected_column in (('імя = імя', 9), ('𐐀 = 𐐀', 5)):
            with self.subTest(statement=statement):
                raw, result = self.extract(statement + '\r\n', 'python')
                self.assertEqual(result['actualOutcome'], 'VALID_EXTRACTED')
                fragment = result['fragments'][0]
                self.assertEqual((fragment['endLine'], fragment['endColumn']), (1, expected_column))
                self.assertEqual(raw[fragment['startByte']:fragment['endByte']], statement.encode('utf-8'))
                self.assertEqual(fragment['sha256'], digest(statement.encode('utf-8')))

    def test_csharp_top_level_statement_and_expression_body(self):
        _, result = self.extract('System.Console.WriteLine("hello");\nclass C { int M() => 7; }', 'csharp')
        self.assertEqual(result['actualOutcome'], 'VALID_EXTRACTED')
        self.assertEqual({f['kind'] for f in result['fragments']}, {'module_statement', 'method_declaration'})

    def test_invalid_stays_invalid_and_nondiagnostic_source_cannot_search(self):
        _, result = self.extract('def broken(:\n pass', 'python')
        self.assertEqual(result['actualOutcome'], 'INVALID_PARSE')
        self.assertFalse(result['searchableExecutable'])
        self.assertTrue(result['diagnostics'])

    def test_type_only_and_generated_cannot_inflate_gallery(self):
        _, types = self.extract('interface A { id: string }', 'typescript')
        self.assertEqual(types['actualOutcome'], 'VALID_EXTRACTED')
        self.assertFalse(types['searchableExecutable'])
        _, generated = self.extract('function f() { return 1; }', 'javascript', generated=True)
        self.assertFalse(generated['searchableExecutable'])

    def test_default_exported_call_is_executable_but_export_list_is_context(self):
        raw, result = self.extract("import wrap from './wrap.js'; export default wrap(1, 'join');", 'javascript')
        self.assertEqual(result['actualOutcome'], 'VALID_EXTRACTED')
        self.assertTrue(result['searchableExecutable'])
        fragment = result['fragments'][0]
        self.assertEqual(raw[fragment['startByte']:fragment['endByte']], b"export default wrap(1, 'join');")
        _, exports = self.extract("export {default as join} from './join.js';", 'javascript')
        self.assertFalse(exports['searchableExecutable'])

    def test_hash_drift_and_unsupported_dialect(self):
        with self.assertRaisesRegex(ValueError, 'hash-drift'):
            extract_source(b'pass', {'fileId': 'a' * 64, 'sha256': 'b' * 64, 'language': 'python'})
        _, result = self.extract('const x = 1;', 'typescript', dialect='unknown')
        self.assertEqual(result['actualOutcome'], 'UNSUPPORTED_DIALECT')
