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

    def test_global_bound_name_is_not_renamed(self):
        raw = (b'def get_console():\n'
               b'    global _console\n'
               b'    _console = {}\n'
               b'    held = 1\n'
               b'    return held\n')
        renamed, record = ast_rename(raw, file_record(raw))
        self.assertEqual(record['changedRanges'][0]['from'], 'held')
        text = renamed.decode('utf-8')
        self.assertIn('global _console', text)
        self.assertIn('    _console = {}', text)
        self.assertNotIn('_console = analysis_renamed_local', text)
        self.assertIn('analysis_renamed_local = 1', text)

    def test_global_only_candidate_is_refused(self):
        raw = b'def set_flag():\n    global flag\n    flag = True\n    return flag\n'
        with self.assertRaisesRegex(ValueError, 'binding-safe'):
            ast_rename(raw, file_record(raw))

    def test_nonlocal_binding_of_a_nested_scope_is_refused(self):
        raw = (b'def outer():\n'
               b'    held = 0\n'
               b'    def inner():\n'
               b'        nonlocal held\n'
               b'        held = 1\n'
               b'        return held\n'
               b'    return inner() + held\n')
        with self.assertRaisesRegex(ValueError, 'binding-safe'):
            ast_rename(raw, file_record(raw))

    def test_nested_scope_loop_target_is_renamed_consistently(self):
        # The nested scope is not shadowing: every occurrence of the binding inside
        # the fragment is rewritten together, so the closure still names one variable.
        raw = (b'function outer() {\n'
               b'  let held = 1;\n'
               b'  const inner = () => { for (held of [1, 2]) { } return held; };\n'
               b'  return held + inner();\n'
               b'}\n')
        renamed, record = ast_rename(raw, file_record(raw, 'javascript'))
        self.assertEqual(record['changedRanges'][0]['from'], 'held')
        self.assertNotIn(b'held', renamed)
        self.assertIn(b'let analysis_renamed_local = 1;', renamed)
        self.assertIn(b'for (analysis_renamed_local of [1, 2])', renamed)

    def test_nested_closure_assignment_is_renamed_consistently(self):
        raw = (b'function outer() {\n'
               b'  let held = 1;\n'
               b'  const inner = () => { held = 2; return held; };\n'
               b'  return held + inner();\n'
               b'}\n')
        renamed, record = ast_rename(raw, file_record(raw, 'javascript'))
        self.assertEqual(record['changedRanges'][0]['from'], 'held')
        self.assertNotIn(b'held', renamed)
        self.assertIn(b'let analysis_renamed_local = 1;', renamed)
        self.assertIn(b'{ analysis_renamed_local = 2; return analysis_renamed_local; }', renamed)

    def test_nested_scope_with_its_own_local_still_renames(self):
        raw = (b'def outer():\n'
               b'    held = 1\n'
               b'    def inner():\n'
               b'        held = 2\n'
               b'        return held\n'
               b'    return held + inner()\n')
        renamed, record = ast_rename(raw, file_record(raw))
        self.assertEqual(record['changedRanges'][0]['from'], 'held')
        text = renamed.decode('utf-8')
        self.assertIn('analysis_renamed_local = 1', text)
        self.assertIn('        held = 2', text)
        self.assertIn('        return held', text)
        self.assertIn('return analysis_renamed_local + inner()', text)

    # -- catch/except bindings are read from the declared pattern only -----
    def test_catch_body_write_to_outer_local_is_renamed_consistently(self):
        # The catch clause declares `e`; the write to the enclosing `fail` is not a
        # declaration, so the whole binding (declaration, catch write, later read)
        # must be rewritten together.
        raw = (b'class A {\n'
               b'  void f() {\n'
               b'    Exception fail = null;\n'
               b'    try {\n'
               b'      parse();\n'
               b'    } catch (Exception e) {\n'
               b'      fail = e;\n'
               b'    }\n'
               b'    System.out.println(fail.getMessage());\n'
               b'  }\n'
               b'}\n')
        renamed, record = ast_rename(raw, file_record(raw, 'java'))
        text = renamed.decode('utf-8')
        self.assertEqual(record['changedRanges'][0]['from'], 'fail')
        self.assertNotIn('fail', text)
        self.assertIn('Exception analysis_renamed_local = null;', text)
        self.assertIn('analysis_renamed_local = e;', text)
        self.assertIn('fail.getMessage'.replace('fail', 'analysis_renamed_local'), text)
        # The catch parameter is a different binding and stays untouched.
        self.assertIn('catch (Exception e)', text)

    def test_catch_parameter_shadow_is_not_rewritten(self):
        # The catch parameter really shadows its own spelling, so the catch scope
        # is skipped while the enclosing block binding is renamed. The scopes are
        # disjoint and legal: the block that declares `caught` ends before the
        # try/catch, so the catch parameter is a separate binding.
        raw = (b'class B {\n'
               b'  void f() {\n'
               b'    {\n'
               b'      Exception caught = null;\n'
               b'      report(caught);\n'
               b'    }\n'
               b'    try {\n'
               b'      parse();\n'
               b'    } catch (Exception caught) {\n'
               b'      report(caught);\n'
               b'    }\n'
               b'  }\n'
               b'}\n')
        renamed, _ = ast_rename(raw, file_record(raw, 'java'))
        text = renamed.decode('utf-8')
        self.assertIn('Exception analysis_renamed_local = null;', text)
        self.assertIn('report(analysis_renamed_local);', text)
        self.assertIn('catch (Exception caught)', text)
        self.assertIn('      report(caught);', text)

    def test_csharp_typed_local_renames_the_value_not_the_type(self):
        raw = (b'class C {\n'
               b'  void F() {\n'
               b'    Exception fail = null;\n'
               b'    try { Parse(); }\n'
               b'    catch (Exception e) { fail = e; }\n'
               b'    System.Console.WriteLine(fail.Message);\n'
               b'  }\n'
               b'}\n')
        renamed, record = ast_rename(raw, file_record(raw, 'csharp'))
        text = renamed.decode('utf-8')
        self.assertEqual(record['changedRanges'][0]['from'], 'fail')
        # The type spelling is untouched and the value binding is renamed
        # consistently in its declaration, the catch write and the later read.
        self.assertIn('Exception analysis_renamed_local = null;', text)
        self.assertIn('catch (Exception e) { analysis_renamed_local = e; }', text)
        self.assertIn('System.Console.WriteLine(analysis_renamed_local.Message);', text)
        self.assertNotIn('analysis_renamed_local fail', text)

    def test_csharp_qualified_generic_type_is_not_a_binding(self):
        raw = (b'class D {\n'
               b'  void F() {\n'
               b'    System.Collections.Generic.List<int> items = new System.Collections.Generic.List<int>();\n'
               b'    use(items);\n'
               b'  }\n'
               b'}\n')
        renamed, record = ast_rename(raw, file_record(raw, 'csharp'))
        text = renamed.decode('utf-8')
        self.assertEqual(record['changedRanges'][0]['from'], 'items')
        self.assertIn('System.Collections.Generic.List<int> analysis_renamed_local =', text)
        self.assertIn('use(analysis_renamed_local);', text)
        self.assertNotIn('analysis_renamed_local<int>', text)

    def test_csharp_multiple_declarators_keep_the_type_spellings(self):
        raw = (b'class E {\n'
               b'  void F() {\n'
               b'    int alpha = 1, beta = 2;\n'
               b'    use(alpha); use(beta);\n'
               b'  }\n'
               b'}\n')
        renamed, record = ast_rename(raw, file_record(raw, 'csharp'))
        text = renamed.decode('utf-8')
        self.assertIn(record['changedRanges'][0]['from'], ('alpha', 'beta'))
        self.assertNotIn('analysis_renamed_local alpha', text)
        self.assertIn('int ', text)

    def test_csharp_unresolvable_declaration_pattern_is_refused(self):
        raw = (b'class G {\n'
               b'  void F() {\n'
               b'    var (first, second) = Pair();\n'
               b'    use(first);\n'
               b'  }\n'
               b'}\n')
        with self.assertRaisesRegex(ValueError, 'binding-safe'):
            ast_rename(raw, file_record(raw, 'csharp'))

    def test_python_except_alias_does_not_hide_outer_local_write(self):
        raw = (b'def f():\n'
               b'    fail = None\n'
               b'    try:\n'
               b'        parse()\n'
               b'    except ValueError as exc:\n'
               b'        fail = exc\n'
               b'    return fail\n')
        renamed, record = ast_rename(raw, file_record(raw))
        text = renamed.decode('utf-8')
        self.assertEqual(record['changedRanges'][0]['from'], 'fail')
        self.assertIn('fail = exc'.replace('fail', 'analysis_renamed_local'), text)
        self.assertIn('return analysis_renamed_local', text)
        self.assertNotIn(' fail', text)

    def test_python_except_without_alias_declares_nothing(self):
        raw = (b'def f():\n'
               b'    fail = None\n'
               b'    try:\n'
               b'        parse()\n'
               b'    except ValueError:\n'
               b'        fail = None\n'
               b'    return fail\n')
        renamed, record = ast_rename(raw, file_record(raw))
        text = renamed.decode('utf-8')
        self.assertEqual(record['changedRanges'][0]['from'], 'fail')
        self.assertNotIn(' fail', text)
