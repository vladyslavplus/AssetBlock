import unittest
from pathlib import Path

from scripts.code_analysis.contracts import digest
from scripts.code_analysis.derivations import ast_rename, comment_format, exact_copy


def file_record(raw, language='python'):
    return {'fileId': 'a' * 64, 'sha256': digest(raw), 'language': language, 'partition': 'train',
            'familyId': 'b' * 64, 'assignmentReviewed': True,
            'permissions': {'evaluationAllowed': True}}


class DerivationTests(unittest.TestCase):
    def test_exact_copy_and_comment_format_change_bytes(self):
        raw = b'def inner(value):\n    return value + 1\n'
        copied, record = exact_copy(raw, file_record(raw))
        self.assertEqual(copied, raw)
        self.assertEqual(record['transformation'], 'EXACT_COPY')
        formatted, fmt = comment_format(raw, file_record(raw))
        self.assertNotEqual(formatted, raw)
        self.assertEqual(fmt['transformation'], 'COMMENT_FORMAT')

    def test_ast_rename_is_scoped_not_regex_wide(self):
        raw = b'def inner(value):\n    total = value + other.value\n    return total\n'
        renamed, record = ast_rename(raw, file_record(raw))
        self.assertNotEqual(renamed, raw)
        self.assertIn(b'analysis_renamed_local', renamed)
        self.assertIn(b'other.value', renamed)
        self.assertIn(b'def inner(value):', renamed)
        self.assertEqual(record['version'], 'ast-rename-v4')
    def test_ast_rename_renames_object_not_property(self):
        raw = b'function f() { const user = src; return user.name; }\n'
        renamed, record = ast_rename(raw, file_record(raw, 'javascript'))
        self.assertIn(b'analysis_renamed_local.name', renamed)
        self.assertNotIn(b'user.name', renamed)
        self.assertIn(b'function f()', renamed)
        self.assertEqual(record['version'], 'ast-rename-v4')

    def test_python_unproven_named_call_skips_parameter(self):
        raw = b'def f(value, other):\n    held = g(value=value) + value.attr + other\n    return held\n'
        renamed, _ = ast_rename(raw, file_record(raw))
        self.assertIn(b'def f(value, other)', renamed)
        self.assertIn(b'g(value=value)', renamed)
        self.assertIn(b'value.attr', renamed)
        self.assertIn(b'held = ' if False else b'analysis_renamed_local = g(value=value)', renamed)
        self.assertIn(b'return analysis_renamed_local', renamed)

    def test_exposed_parameter_without_closed_contract_is_refused(self):
        raw = b'def f(file_like):\n    return file_like\n'
        with self.assertRaisesRegex(ValueError, 'binding-safe'):
            ast_rename(raw, file_record(raw))

    def test_recursive_named_call_is_refused(self):
        raw = b'def f(value):\n    if value > 0:\n        return f(value=value - 1)\n    return value\n'
        with self.assertRaisesRegex(ValueError, 'binding-safe'):
            ast_rename(raw, file_record(raw))

    def test_nested_named_call_is_refused(self):
        raw = b'def outer(value):\n    def inner():\n        return outer(value=1)\n    return inner() + value\n'
        with self.assertRaisesRegex(ValueError, 'binding-safe'):
            ast_rename(raw, file_record(raw))

    def test_nested_shadow_and_collision_skip(self):
        raw = b'def outer(value):\n    def inner(value):\n        return value\n    held = value + inner(value)\n    return held\n'
        renamed, _ = ast_rename(raw, file_record(raw))
        self.assertIn(b'def inner(value)', renamed)
        self.assertIn(b'def outer(value)', renamed)
        self.assertIn(b'analysis_renamed_local = value + inner(value)', renamed)
        self.assertIn(b'return analysis_renamed_local', renamed)

    def test_shorthand_before_later_identifier_uses_original_offsets(self):
        raw = b'const f = () => { const user = src; return { user, n: user }; };\n'
        renamed, _ = ast_rename(raw, file_record(raw, 'javascript'))
        self.assertIn(b'user:', renamed)
        self.assertIn(b'n: analysis_renamed_local', renamed)

    def test_named_caller_outside_is_refused(self):
        raw = b'def f(value):\n    return value + 1\nresult = f(value=2)\n'
        with self.assertRaisesRegex(ValueError, 'binding-safe'):
            ast_rename(raw, file_record(raw))

    def test_annotated_parameter_skips_type_identifier(self):
        raw = b'def f(value: Other):\n    item = value\n    return item\n'
        renamed, _ = ast_rename(raw, file_record(raw))
        self.assertIn(b'def f(value: Other)', renamed)
        self.assertIn(b'analysis_renamed_local = value', renamed)
        self.assertIn(b'return analysis_renamed_local', renamed)

    def test_block_local_shadow_is_preserved(self):
        raw = b'function f() { let user = 0; { let user = 1; return user; } return user + 1; }\n'
        renamed, _ = ast_rename(raw, file_record(raw, 'javascript'))
        self.assertIn(b'let user = 1', renamed)
        self.assertIn(b'return user;', renamed)
        self.assertIn(b'let analysis_renamed_local = 0', renamed)
        self.assertIn(b'return analysis_renamed_local + 1', renamed)

    def test_collision_refuses_when_replacement_in_scope(self):
        raw = b'def f():\n    value = 1\n    analysis_renamed_local = 2\n    return value + analysis_renamed_local\n'
        with self.assertRaisesRegex(ValueError, 'binding-safe'):
            ast_rename(raw, file_record(raw))

    def test_fileno_local_binding_keeps_callable_parameter(self):
        raw = (b"from typing import IO, Callable\n"
               b"def get_fileno(file_like: IO[str]) -> int | None:\n"
               b"    fileno: Callable[[], int] | None = getattr(file_like, \"fileno\", None)\n"
               b"    return fileno() if fileno else None\n")
        renamed, record = ast_rename(raw, file_record(raw))
        text = renamed.decode('utf-8')
        self.assertEqual(record['changedRanges'][0]['from'], 'fileno')
        self.assertEqual(record['version'], 'ast-rename-v4')
        self.assertIn('from typing import IO, Callable', text)
        self.assertIn('def get_fileno(file_like: IO[str]) -> int | None:', text)
        self.assertNotIn('def get_fileno(analysis_renamed_local', text)
        self.assertNotIn('file_like: analysis_renamed_local', text)
        self.assertNotIn('analysis_renamed_local[str]', text)
        self.assertIn('getattr(file_like, "fileno", None)', text)
        self.assertIn('analysis_renamed_local: Callable[[], int] | None', text)

    def test_keyword_caller_keeps_file_like_parameter(self):
        raw = (b'from typing import IO, Callable\n'
               b'def get_fileno(file_like: IO[str]) -> int | None:\n'
               b'    fileno: Callable[[], int] | None = getattr(file_like, "fileno", None)\n'
               b'    return fileno() if fileno is not None else None\n'
               b'value = get_fileno(file_like=open("a"))\n')
        renamed, record = ast_rename(raw, file_record(raw))
        self.assertEqual(record['changedRanges'][0]['from'], 'fileno')
        self.assertIn(b'def get_fileno(file_like: IO[str])', renamed)
        self.assertIn(b'get_fileno(file_like=open("a"))', renamed)
        self.assertIn(b'getattr(file_like, "fileno", None)', renamed)
        self.assertIn(b'analysis_renamed_local: Callable[[], int] | None', renamed)

    def test_typed_default_and_qualified_generic_keep_type_names(self):
        raw = b'def f(value: typing.IO[str] = typing.IO()):\n    data = value.read()\n    return data\n'
        renamed, _ = ast_rename(raw, file_record(raw))
        self.assertIn(b'def f(value: typing.IO[str] = typing.IO())', renamed)
        self.assertIn(b'return analysis_renamed_local', renamed)
        self.assertNotIn(b'def f(analysis_renamed_local:', renamed)

    def test_generic_annotation_other_is_not_renamed(self):
        raw = b'def f(file_like: Other[str]) -> Other[str]:\n    result = file_like\n    return result\n'
        renamed, _ = ast_rename(raw, file_record(raw))
        self.assertIn(b'def f(file_like: Other[str])', renamed)
        self.assertIn(b'analysis_renamed_local = file_like', renamed)
        self.assertIn(b'-> Other[str]:', renamed)

    def test_unsupported_tuple_parameter_is_refused(self):
        raw = b'def f((left, right)):\n    return left + right\n'
        with self.assertRaisesRegex(ValueError, 'binding-safe'):
            ast_rename(raw, file_record(raw))

    def test_typescript_destructured_parameter_is_refused(self):
        raw = b'function f({user}: {user: string}) { return user; }\n'
        with self.assertRaisesRegex(ValueError, 'binding-safe'):
            ast_rename(raw, file_record(raw, 'typescript'))
