import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts.code_analysis.cli import main
from scripts.code_analysis.contracts import canonical
from scripts.code_analysis.corpus import ARTIFACT_ROOT
import test_intake as fixtures


class StageTests(unittest.TestCase):
    def setUp(self):
        ARTIFACT_ROOT.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.config_path = self.root / "sources.json"
        self.config_path.write_bytes(canonical(fixtures.config()))

    def invoke(self, stage, run="fixture-run"):
        with contextlib.redirect_stdout(io.StringIO()), patch(
                "scripts.code_analysis.cli.ARTIFACT_ROOT", self.root):
            # intake's default fetch is bound at definition time; patch its opener instead.
            from scripts.code_analysis.corpus import intake
            with patch("scripts.code_analysis.cli.intake", side_effect=lambda c, r, offline=False:
                       intake(c, r, offline=offline, fetch=fixtures.IntakeTests.fetch)):
                return main([stage, "--run-id", run, "--config", str(self.config_path)])

    def test_stages_bind_hashes_and_leave_counts_pending(self):
        for stage in ("intake", "verify-offline", "split", "summarize"):
            self.assertEqual(self.invoke(stage), 0)
        summary = json.loads((self.root / "runs/fixture-run/summarize.json").read_text())
        self.assertEqual(summary["status"], "CHANGES_NEEDED")
        self.assertEqual(summary["extractionDependentCounts"], "PENDING")
        self.assertFalse(summary["CanAuthorizePublication"])
        self.assertEqual(summary["SecurityEvidence"], "NOT_RUN")
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            self.invoke("intake")

    def test_failure_is_recorded_and_source_drift_blocks(self):
        self.assertEqual(self.invoke("intake"), 0)
        path = self.root / "runs/fixture-run/sources/one/src/plain.py"
        path.write_bytes(b"changed")
        self.assertEqual(self.invoke("verify-offline"), 1)
        record = json.loads((self.root / "runs/fixture-run/verify-offline-stage.json").read_text())
        self.assertEqual(record["ActualOutcome"], "FAILED")
        self.assertTrue(record["gaps"])

    def test_snapshot_or_config_drift_blocks_split(self):
        self.assertEqual(self.invoke("intake"), 0)
        changed = fixtures.config()
        changed["sources"][0]["partition"] = "validation"
        self.config_path.write_bytes(canonical(changed))
        self.assertEqual(self.invoke("split"), 1)

    def test_no_later_stage_or_bad_run_id(self):
        for stage, run in (("train", "fixture"), ("intake", "../escape")):
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                self.invoke(stage, run)

    def test_index_build_refuses_and_restore_is_a_stage(self):
        self.assertEqual(self.invoke("index-build"), 1)
        record = json.loads((self.root / "runs/fixture-run/index-build-stage.json").read_bytes())
        self.assertEqual(record["ActualOutcome"], "FAILED")
        self.assertTrue(any("rebuild refused" in item for item in record.get("gaps") or []))

    def reviewed_config(self):
        data = fixtures.config()
        source = data['sources'][0]
        source.update(partition='train', partitionReviewed=True)
        source['rights'] = {'status': 'REVIEWED', 'reviewer': 'Authored unit-test reviewer', 'reviewedOn': '2026-10-05',
                            'rationale': 'Authored fixture permission', 'terms': 'MIT', 'commit': source['commit'],
                            'fileHashes': {f['path']: f['sha256'] for f in source['files']},
                            'noticeHashes': {n['path']: n['sha256'] for n in source['notices']},
                            'grants': {'searchIndexAllowed': True, 'trainingAllowed': True, 'evaluationAllowed': True}}
        self.config_path.write_bytes(canonical(data))

    def test_source_stages_preserve_insufficiency_and_empty_exports(self):
        self.reviewed_config()
        for name in ('intake', 'split', 'extract', 'derive', 'export', 'summarize'):
            self.assertEqual(self.invoke(name), 0)
        run = self.root / 'runs/fixture-run'
        extracted = json.loads((run / 'extract.json').read_bytes())
        self.assertTrue(extracted['files'][0]['searchableExecutable'])
        self.assertEqual(json.loads((run / 'derive.json').read_bytes())['status'], 'EMPTY_NO_REVIEWED_QUERY_SEEDS')
        self.assertEqual(json.loads((run / 'export.json').read_bytes())['status'], 'EMPTY_NO_ELIGIBLE_REVIEWED_PAIRS')
        self.assertEqual(json.loads((run / 'summarize.json').read_bytes())['status'], 'CHANGES_NEEDED')

    def test_extraction_rechecks_assignments_and_split_bytes(self):
        for stage in ('intake', 'split'):
            self.assertEqual(self.invoke(stage), 0)
        self.assertEqual(self.invoke('extract'), 1)
        self.reviewed_config()
        for stage in ('intake', 'split'):
            self.assertEqual(self.invoke(stage, 'tampered'), 0)
        path = self.root / 'runs/tampered/split.json'
        split = json.loads(path.read_bytes())
        split['files'][0]['permissions']['trainingAllowed'] = False
        path.write_bytes(canonical(split))
        self.assertEqual(self.invoke('extract', 'tampered'), 1)
