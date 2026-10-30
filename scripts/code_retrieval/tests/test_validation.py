import shutil
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from scripts.code_analysis.contracts import digest
from scripts.code_retrieval import corpus_index, config as config_module, inputs, prepare, validation
from scripts.code_retrieval.tests import _support as fx

LANGUAGE = "python"
REVIEWER = "Fixture validation reviewer"
REVIEWED_ON = "2026-10-19"
CONFIG_PATH = Path(__file__).resolve().parents[1] / "config" / "training-config.json"


def evidence(file_row, fragments, *, label, query_fragment_id, covered=None,
             review_status="INDEPENDENTLY_REVIEWED"):
    """One reviewed file-level evidence record covering every fragment of the file."""
    reviewed = review_status == "INDEPENDENTLY_REVIEWED"
    ids = [row["fragmentId"] if isinstance(row, dict) else row for row in fragments]
    return {"fileId": file_row["fileId"], "fileSha256": file_row["sha256"], "label": label,
            "scope": "WHOLE_FILE",
            "coveredFragmentIds": list(sorted(covered if covered is not None else ids)),
            "queryFragmentId": query_fragment_id, "reviewStatus": review_status,
            "reviewer": REVIEWER if reviewed else None,
            "reviewedOn": REVIEWED_ON if reviewed else None,
            "rationale": "Authored file-level judgment." if reviewed else None}


class ValidationScene:
    """One-language authored scene: train/final-test/validation files with fragments."""

    def __init__(self, temp, counter, *, validation_files=8, fragments_per_file=1, extra_nodes=()):
        self.root = Path(temp) / ("scene-%d" % counter)
        nodes = [fx.node("fixture", "q.py", LANGUAGE, "train", "fam-train", fx.python_body("q")),
                 fx.node("fixture", "ft.py", LANGUAGE, "final-test", "fam-ft", fx.python_body("ft"))]
        for index in range(validation_files):
            nodes.append(fx.node("fixture", "v%d.py" % index, LANGUAGE, "validation", "fam-val",
                                 fx.python_body("v%d" % index)))
        nodes += list(extra_nodes)
        fx.write_sources(self.root, nodes)
        fragments = []
        for node in nodes:
            if node["row"]["partition"] != "validation":
                continue
            for part in range(fragments_per_file):
                if fragments_per_file == 1:
                    # One fragment per file covers the whole file, so a derived
                    # variant splices back in as valid syntax of this language.
                    start, end = 0, len(node["body"])
                else:
                    start = part * 12
                    end = min(start + 12, len(node["body"]))
                fragments.append(fx.fragment(node, start, end))
        manifest, split, extract = fx.corpus(
            nodes, fragments,
            sources=[fx.source("fixture", [(row["row"]["path"], row["row"]["sha256"],
                                             row["row"]["language"]) for row in nodes])])
        self.nodes = nodes
        self.fragments = fragments
        self.manifest = manifest
        self.split = split
        self.extract = extract
        self.files = [row["row"] for row in nodes]
        self.validation_rows = [row["row"] for row in nodes
                                if row["row"]["partition"] == "validation"
                                and row["row"]["language"] == LANGUAGE]
        self.fragments_by_file = {}
        for row in fragments:
            self.fragments_by_file.setdefault(row["fileId"], []).append(row)

    def seed(self, parent_row, *, gallery_ids=None, relevant_ids=None, distractor_rows=None,
             evidence_rows=None, strata="EXACT_COPY", query=None):
        query = query or self.fragments_by_file[parent_row["fileId"]][0]
        gallery_ids = list(gallery_ids if gallery_ids is not None
                           else [row["fileId"] for row in self.validation_rows])
        relevant_ids = list(relevant_ids if relevant_ids is not None else [parent_row["fileId"]])
        distractor_rows = list(distractor_rows if distractor_rows is not None
                               else [row for row in self.validation_rows
                                     if row["fileId"] != parent_row["fileId"]])
        distractors = [{"fragmentId": self.fragments_by_file[row["fileId"]][0]["fragmentId"],
                        "fragmentSha256": self.fragments_by_file[row["fileId"]][0]["sha256"],
                        "leftFragmentId": query["fragmentId"], "parentFileId": row["fileId"],
                        "label": "DISSIMILAR", "reviewStatus": "INDEPENDENTLY_REVIEWED",
                        "reviewer": REVIEWER} for row in distractor_rows]
        if evidence_rows is None:
            evidence_rows = [evidence(parent_row, self.fragments_by_file[parent_row["fileId"]],
                                      label="RELEVANT", query_fragment_id=query["fragmentId"])]
            evidence_rows += [evidence(row, self.fragments_by_file[row["fileId"]],
                                       label="DISSIMILAR", query_fragment_id=query["fragmentId"])
                              for row in distractor_rows]
        return {"queryFragmentId": query["fragmentId"], "querySha256": query["sha256"],
                "seedFileId": parent_row["fileId"], "familyId": parent_row["familyId"],
                "querySourceSha256": parent_row["sha256"], "language": LANGUAGE, "strata": strata,
                "galleryFileIds": gallery_ids, "relevantFileIds": relevant_ids,
                "fileEvidence": evidence_rows, "distractors": distractors,
                "reviewStatus": "INDEPENDENTLY_REVIEWED", "reviewer": REVIEWER}


class ValidationAdmissionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.counter = 0

    def scene(self, **kwargs):
        self.counter += 1
        return ValidationScene(self.temp.name, self.counter, **kwargs)

    def admit(self, scene, payload, *, index=None):
        return validation.admit(payload,
                                files=index["files"] if index else scene.files,
                                fragments=index["fragments"] if index else scene.fragments,
                                split_files={row["fileId"]: row for row in scene.files},
                                sources=scene.manifest["sources"], sources_root=scene.root,
                                file_aliases=index.get("fileAliases") if index else None)

    def payload(self, seeds_by_language, *, status="INDEPENDENTLY_REVIEWED"):
        return {"schema": validation.SCHEMA, "status": status, "reviewer": REVIEWER,
                "reviewedOn": REVIEWED_ON, "rationale": "Authored reviewed validation corpus.",
                "languages": {language: {"seeds": list(seeds)}
                              for language, seeds in seeds_by_language.items()}}

    def small_floors(self):
        return mock.patch.multiple(validation, MIN_SEEDS_PER_LANGUAGE=1, MIN_GALLERY_FILES=4,
                                   MIN_DISTRACTORS_PER_SEED=2, SCORED_K=2)

    def issues(self, result):
        return result["languages"][LANGUAGE]["issues"]

    # -- stratum binding -------------------------------------------------
    def test_original_query_relabeled_comment_format_is_blocked(self):
        scene = self.scene(validation_files=6)
        with self.small_floors():
            seed = scene.seed(scene.validation_rows[0], strata="COMMENT_FORMAT")
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        self.assertIn("STRATUM_MISMATCH_ORIGINAL", self.issues(result))

    def test_ast_renamed_original_query_is_blocked(self):
        scene = self.scene(validation_files=6)
        with self.small_floors():
            seed = scene.seed(scene.validation_rows[0], strata="AST_BOUND_RENAME")
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        self.assertIn("STRATUM_MISMATCH_ORIGINAL", self.issues(result))

    def test_identical_output_derived_query_is_blocked(self):
        scene = self.scene(validation_files=6)
        train_node, train_frag = scene.nodes[0], fx.fragment(scene.nodes[0])
        scene.fragments.append(train_frag)
        derived = fx.derived_positive(train_node, train_frag, output=train_node["body"])
        scene.fragments.append(derived)
        with self.small_floors():
            seed = scene.seed(scene.validation_rows[0],
                              query=derived,
                              strata=derived["derivation"]["transformation"])
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        issues = set(self.issues(result))
        self.assertTrue({"STRATUM_DERIVATION_MISMATCH", "DERIVED_OUTPUT_IDENTICAL_TO_ORIGINAL"} & issues,
                        "expected identical-output derivative rejection, got: %s" % sorted(issues))

    def test_wrong_transformation_stratum_is_blocked(self):
        scene = self.scene(validation_files=6)
        train_node, train_frag = scene.nodes[0], fx.fragment(scene.nodes[0])
        scene.fragments.append(train_frag)
        derived = fx.derived_positive(train_node, train_frag,
                                      transformation="AST_BOUND_RENAME", version="ast-rename-v4")
        scene.fragments.append(derived)
        with self.small_floors():
            seed = scene.seed(scene.validation_rows[0], query=derived, strata="COMMENT_FORMAT")
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        self.assertIn("STRATUM_DERIVATION_MISMATCH", self.issues(result))

    def test_accepted_derived_comment_format_clears_its_gate(self):
        scene = self.scene(validation_files=6)
        train_node, train_frag = scene.nodes[0], fx.fragment(scene.nodes[0])
        scene.fragments.append(train_frag)
        derived = fx.derived_positive(train_node, train_frag,
                                      transformation="COMMENT_FORMAT", review=True)
        scene.fragments.append(derived)
        # The derived query's parent is the train file q.py — not validation-eligible,
        # so this seed reports only a genuinely ineligible issue, not a stratum issue.
        with self.small_floors():
            seed = scene.seed(scene.validation_rows[0], query=derived,
                              strata="COMMENT_FORMAT")
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        issues = self.issues(result)
        self.assertNotIn("STRATUM_MISMATCH_ORIGINAL", issues)
        self.assertNotIn("STRATUM_DERIVATION_MISMATCH", issues)
        self.assertNotIn("STRATUM_BYTES_MISMATCH", issues)

    # -- nonidentical variant coverage is an admission gate ---------------
    def _variant_seed(self, scene, parent, *, drop_output_digest=False, **overrides):
        node = next(n for n in scene.nodes if n["row"]["fileId"] == parent["fileId"])
        original = scene.fragments_by_file[parent["fileId"]][0]
        piece = node["body"][original["startByte"]:original["endByte"]]
        comment = b"# fixture\n" if parent["language"] == "python" else b"// fixture\n"
        output = piece + comment if "output" not in overrides else None
        derived = fx.derived_positive(node, original, output=output, **overrides)
        if drop_output_digest:
            derived["derivation"].pop("outputSha256")
        scene.fragments.append(derived)
        return scene.seed(parent, query=derived, strata=overrides.get("transformation", "COMMENT_FORMAT"))

    def test_twenty_exact_only_seeds_cannot_clear_readiness(self):
        scene = self.scene(validation_files=22)
        with self.small_floors():
            seeds = [scene.seed(row) for row in scene.validation_rows[:20]]
            result = self.admit(scene, self.payload({LANGUAGE: seeds}))
        self.assertEqual(result["status"], validation.BLOCKER)
        language = result["languages"][LANGUAGE]
        self.assertIn("NONIDENTICAL_VARIANT_UNCOVERED", language["issues"])
        self.assertEqual(language["nonidenticalVariantFiles"], 0)
        self.assertEqual(language["distinctSeedFiles"], 20)

    def test_one_seed_file_without_variant_is_named_concretely(self):
        scene = self.scene(validation_files=22)
        missing = scene.validation_rows[7]
        with self.small_floors():
            seeds = [scene.seed(row) for row in scene.validation_rows[:20]]
            variants = [self._variant_seed(scene, row) for row in scene.validation_rows[:20]
                        if row["fileId"] != missing["fileId"]]
            result = self.admit(scene, self.payload({LANGUAGE: seeds + variants}))
        language = result["languages"][LANGUAGE]
        self.assertEqual(language["seedFilesWithoutNonidenticalVariant"], [missing["fileId"]])
        self.assertEqual(language["nonidenticalVariantFiles"], 19)
        self.assertIn("NONIDENTICAL_VARIANT_UNCOVERED", language["issues"])

    def test_identical_derivative_of_proper_subspan_is_blocked(self):
        scene = self.scene(validation_files=6, fragments_per_file=2)
        parent = scene.validation_rows[0]
        node = next(n for n in scene.nodes if n["row"]["fileId"] == parent["fileId"])
        subspan = scene.fragments_by_file[parent["fileId"]][1]
        piece = node["body"][subspan["startByte"]:subspan["endByte"]]
        derived = fx.derived_positive(node, subspan, output=piece)
        scene.fragments.append(derived)
        with self.small_floors():
            seed = scene.seed(parent, query=derived, strata="COMMENT_FORMAT")
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        issues = self.issues(result)
        self.assertIn("DERIVED_OUTPUT_IDENTICAL_TO_ORIGINAL", issues)
        self.assertIn("NONIDENTICAL_VARIANT_UNCOVERED", issues)

    def test_missing_output_digest_is_blocked(self):
        scene = self.scene(validation_files=10)
        parent = scene.validation_rows[0]
        with self.small_floors():
            seed = self._variant_seed(scene, parent, drop_output_digest=True)
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        issues = self.issues(result)
        self.assertIn("DERIVED_OUTPUT_DIGEST_MISSING", issues)
        self.assertEqual({row["fileId"] for row in scene.validation_rows[:1]},
                         set(result["languages"][LANGUAGE]["seedFilesWithoutNonidenticalVariant"]))

    def test_wrong_derivation_version_is_blocked(self):
        scene = self.scene(validation_files=10)
        parent = scene.validation_rows[0]
        with self.small_floors():
            seed = self._variant_seed(scene, parent, version="comment-format-v4")
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        issues = self.issues(result)
        self.assertIn("DERIVED_VERSION_UNBOUND", issues)
        self.assertIn("NONIDENTICAL_VARIANT_UNCOVERED", issues)

    # -- relevance and gallery binding ----------------------------------
    def test_missing_relevant_target_is_blocked(self):
        scene = self.scene(validation_files=6)
        with self.small_floors():
            seed = scene.seed(scene.validation_rows[0], relevant_ids=[])
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        self.assertEqual(result["status"], validation.BLOCKER)
        self.assertIn("NO_RELEVANT_TARGET", self.issues(result))

    def test_relevant_target_outside_the_gallery_is_blocked(self):
        scene = self.scene(validation_files=6)
        excluded = scene.validation_rows[5]
        with self.small_floors():
            seed = scene.seed(scene.validation_rows[0],
                              gallery_ids=[row["fileId"] for row in scene.validation_rows[:5]],
                              relevant_ids=[excluded["fileId"]],
                              distractor_rows=scene.validation_rows[1:5])
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        self.assertIn("RELEVANT_TARGET_OUTSIDE_GALLERY", self.issues(result))

    def test_distractor_records_are_bound_to_the_declared_gallery(self):
        scene = self.scene(validation_files=6)
        with self.small_floors():
            seed = scene.seed(scene.validation_rows[0],
                              gallery_ids=[row["fileId"] for row in scene.validation_rows[:5]],
                              distractor_rows=scene.validation_rows[1:6])
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        self.assertIn("DISTRACTOR_OFF_GALLERY", self.issues(result))

    def test_cross_language_distractor_is_blocked(self):
        other = fx.node("fixture", "js0.js", "javascript", "validation", "fam-js", fx.python_body("js0"))
        scene = self.scene(validation_files=6, extra_nodes=[other])
        with self.small_floors():
            seed = scene.seed(scene.validation_rows[0],
                              distractor_rows=scene.validation_rows[1:5] + [other["row"]])
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        self.assertIn("DISTRACTOR_LANGUAGE_MISMATCH", self.issues(result))

    def test_relevance_and_distractor_labels_may_not_contradict(self):
        scene = self.scene(validation_files=6)
        parent, other = scene.validation_rows[0], scene.validation_rows[1]
        with self.small_floors():
            seed = scene.seed(parent, relevant_ids=[parent["fileId"], other["fileId"]])
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        self.assertIn("DISTRACTOR_IS_RELEVANT", self.issues(result))

    # -- seed file identity and source bytes ----------------------------
    def test_seed_floor_counts_unique_source_files_not_fragments(self):
        scene = self.scene(validation_files=3, fragments_per_file=4)
        parent = scene.validation_rows[0]
        queries = scene.fragments_by_file[parent["fileId"]]
        with mock.patch.multiple(validation, MIN_SEEDS_PER_LANGUAGE=3, MIN_GALLERY_FILES=2,
                                 MIN_DISTRACTORS_PER_SEED=1, SCORED_K=1):
            seeds = [scene.seed(parent, query=query, distractor_rows=scene.validation_rows[1:2])
                     for query in queries[:3]]
            result = self.admit(scene, self.payload({LANGUAGE: seeds}))
        language = result["languages"][LANGUAGE]
        self.assertEqual(result["status"], validation.BLOCKER)
        self.assertEqual(language["distinctSeedFiles"], 1)
        self.assertIn("INSUFFICIENT_SEED_FILES", language["issues"])

    def test_absent_query_source_bytes_are_blocked(self):
        scene = self.scene(validation_files=6)
        with self.small_floors():
            seed = scene.seed(scene.validation_rows[0])
            (scene.root / "fixture" / "v0.py").unlink()
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        self.assertIn("QUERY_BYTES_UNRESOLVABLE", self.issues(result))

    def test_stale_query_source_bytes_are_blocked(self):
        scene = self.scene(validation_files=6)
        with self.small_floors():
            seed = scene.seed(scene.validation_rows[0])
            target = scene.root / "fixture" / "v0.py"
            target.write_bytes(target.read_bytes() + b"\n# appended\n")
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        self.assertIn("QUERY_BYTES_UNRESOLVABLE", self.issues(result))

    def test_held_out_normalized_content_is_blocked(self):
        leaking = fx.node("fixture", "vleak.py", LANGUAGE, "validation", "fam-val",
                          fx.python_body("vleak"),
                          normalized=fx.sha(b"norm:" + fx.python_body("q")))
        scene = self.scene(validation_files=6, extra_nodes=[leaking])
        with self.small_floors():
            seed = scene.seed(leaking["row"])
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        self.assertIn("SEED_PARENT_HELD_OUT_CONTENT", self.issues(result))
        self.assertIn("GALLERY_HELD_OUT_CONTENT", self.issues(result))

    # -- file-level evidence --------------------------------------------
    def test_single_fragment_evidence_for_a_multi_fragment_file_is_blocked(self):
        scene = self.scene(validation_files=6, fragments_per_file=2)
        parent = scene.validation_rows[0]
        fragments = scene.fragments_by_file[parent["fileId"]]
        with self.small_floors():
            rows = [evidence(parent, fragments[:1], label="RELEVANT",
                             query_fragment_id=fragments[0]["fragmentId"])]
            rows += [evidence(row, scene.fragments_by_file[row["fileId"]],
                              label="DISSIMILAR", query_fragment_id=fragments[0]["fragmentId"])
                     for row in scene.validation_rows[1:]]
            seed = scene.seed(parent, evidence_rows=rows)
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        self.assertIn("EVIDENCE_INCOMPLETE_FILE_COVERAGE", self.issues(result))

    def test_unreviewed_evidence_record_is_blocked(self):
        scene = self.scene(validation_files=6)
        parent = scene.validation_rows[0]
        query = scene.fragments_by_file[parent["fileId"]][0]
        with self.small_floors():
            rows = [evidence(parent, scene.fragments_by_file[parent["fileId"]], label="RELEVANT",
                             query_fragment_id=query["fragmentId"],
                             review_status="CANDIDATE_UNREVIEWED")]
            rows += [evidence(row, scene.fragments_by_file[row["fileId"]], label="DISSIMILAR",
                              query_fragment_id=query["fragmentId"])
                     for row in scene.validation_rows[1:]]
            seed = scene.seed(parent, evidence_rows=rows)
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        self.assertIn("EVIDENCE_UNREVIEWED", self.issues(result))

    def test_missing_evidence_for_a_compared_file_is_blocked(self):
        scene = self.scene(validation_files=6)
        parent = scene.validation_rows[0]
        query = scene.fragments_by_file[parent["fileId"]][0]
        with self.small_floors():
            rows = [evidence(parent, scene.fragments_by_file[parent["fileId"]], label="RELEVANT",
                             query_fragment_id=query["fragmentId"])]
            rows += [evidence(row, scene.fragments_by_file[row["fileId"]], label="DISSIMILAR",
                              query_fragment_id=query["fragmentId"])
                     for row in scene.validation_rows[1:-1]]
            seed = scene.seed(parent, evidence_rows=rows)
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        self.assertIn("EVIDENCE_MISSING_FOR_COMPARED_FILE", self.issues(result))

    # -- alias identity cannot inflate floors ----------------------------
    def test_alias_gallery_ids_do_not_inflate_the_gallery_floor(self):
        scene = self.scene(validation_files=6)
        aliases = [{**scene.validation_rows[0], "fileId": "alias-a"},
                   {**scene.validation_rows[1], "fileId": "alias-b"}]
        index = fx.index(scene.manifest, scene.split, scene.extract, [], extra_files=aliases)
        parent = scene.validation_rows[0]
        gallery = [row["fileId"] for row in scene.validation_rows[:4]] + ["alias-a", "alias-b"]
        with mock.patch.multiple(validation, MIN_SEEDS_PER_LANGUAGE=1, MIN_GALLERY_FILES=5,
                                 MIN_DISTRACTORS_PER_SEED=1, SCORED_K=2):
            seed = scene.seed(parent, gallery_ids=gallery, distractor_rows=scene.validation_rows[1:3])
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}), index=index)
        language = result["languages"][LANGUAGE]
        self.assertEqual(language["galleryFileCount"], 4)
        self.assertIn("GALLERY_BELOW_FLOOR", language["issues"])

    # -- scored gallery allowlist (per-query receipts) ---------------------
    def test_unjudged_gallery_files_are_excluded_not_scored(self):
        scene = self.scene(validation_files=8)
        parent = scene.validation_rows[0]
        judged = scene.validation_rows[1:5]
        unjudged = scene.validation_rows[5:]
        with self.small_floors():
            seed = scene.seed(parent, distractor_rows=judged)
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        receipt = result["languages"][LANGUAGE]["queryScoredReceipts"][0]
        scored = set(receipt["scoredGallery"])
        excluded = set(receipt["excludedUnknownGalleryFiles"])
        self.assertTrue(set(row["fileId"] for row in judged) <= scored,
                        "judged distractor parents must be scored")
        self.assertEqual(scored & {row["fileId"] for row in unjudged}, set())
        self.assertTrue({row["fileId"] for row in unjudged} <= excluded,
                        "unjudged gallery files must be explicitly excluded")

    def test_missing_evidence_for_a_scored_file_is_blocked(self):
        scene = self.scene(validation_files=6)
        parent = scene.validation_rows[0]
        query = scene.fragments_by_file[parent["fileId"]][0]
        with self.small_floors():
            rows = [evidence(parent, scene.fragments_by_file[parent["fileId"]], label="RELEVANT",
                             query_fragment_id=query["fragmentId"])]
            # one distractor parent has no evidence row
            rows += [evidence(row, scene.fragments_by_file[row["fileId"]], label="DISSIMILAR",
                              query_fragment_id=query["fragmentId"])
                     for row in scene.validation_rows[1:-1]]
            seed = scene.seed(parent, evidence_rows=rows)
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        issues = self.issues(result)
        self.assertIn("EVIDENCE_MISSING_FOR_COMPARED_FILE", issues)
        self.assertIn("DISTRACTOR_NOT_IN_SCORED_GALLERY", issues)

    def test_sufficient_reviewed_corpus_reports_exact_scored_gallery(self):
        scene = self.scene(validation_files=6)
        with self.small_floors():
            seed = scene.seed(scene.validation_rows[0])
            result = self.admit(scene, self.payload({LANGUAGE: [seed]}))
        language = result["languages"][LANGUAGE]
        parent_id = scene.validation_rows[0]["fileId"]
        self.assertEqual(language["queryScoredReceipts"][0]["scoredGalleryFileCount"], 6)
        self.assertEqual({row["seedFileId"] for row in language["queryScoredReceipts"]}, {parent_id})
        self.assertEqual(language["descriptiveScoredUnionFileCount"], 6)
        self.assertEqual(language["descriptiveUnknownUnionFileCount"], 0)

    # -- per-query receipts, never one global scoring set ------------------
    def test_unknown_for_one_query_stays_scored_for_another(self):
        scene = self.scene(validation_files=8)
        a, b = scene.validation_rows[0], scene.validation_rows[1]
        shared = [row["fileId"] for row in scene.validation_rows[:6]]
        with self.small_floors():
            seed_a = scene.seed(a, gallery_ids=shared,
                                distractor_rows=list(scene.validation_rows[1:6]))
            # b reviews the query-relative distractors after a's file, so a's
            # file is declared in the shared gallery but judged by no one
            # (UNKNOWN for b) while still scored for a.
            seed_b = scene.seed(b, gallery_ids=shared,
                                distractor_rows=list(scene.validation_rows[2:6]))
            result = self.admit(scene, self.payload({LANGUAGE: [seed_a, seed_b]}))
        language = result["languages"][LANGUAGE]
        by_query = {row["queryFragmentId"]: row for row in language["queryScoredReceipts"]}
        self.assertEqual(len(by_query), 2)
        receipt_a = by_query[scene.fragments_by_file[a["fileId"]][0]["fragmentId"]]
        receipt_b = by_query[scene.fragments_by_file[b["fileId"]][0]["fragmentId"]]
        self.assertIn(a["fileId"], receipt_a["scoredGallery"])
        self.assertIn(a["fileId"], receipt_b["excludedUnknownGalleryFiles"])
        self.assertNotIn(a["fileId"], receipt_b["scoredGallery"])
        # Each receipt matches its own evidence exactly.
        self.assertEqual(set(receipt_a["scoredGallery"]), set(shared))
        self.assertEqual(set(receipt_b["scoredGallery"]),
                         set(shared) - {a["fileId"]})

    def test_different_unknown_subsets_report_consistently(self):
        scene = self.scene(validation_files=9)
        parents = scene.validation_rows[:4]

        def make(parent, gallery_rows):
            return scene.seed(parent, gallery_ids=[row["fileId"] for row in gallery_rows],
                              distractor_rows=[row for row in gallery_rows
                                               if row["fileId"] != parent["fileId"]])

        with self.small_floors():
            seeds = [make(row, scene.validation_rows[:7] if index % 2 else scene.validation_rows[:6])
                     for index, row in enumerate(parents)]
            result = self.admit(scene, self.payload({LANGUAGE: seeds}))
        language = result["languages"][LANGUAGE]
        self.assertEqual(len(language["queryScoredReceipts"]), len(seeds))
        for seed, receipt in zip(seeds, language["queryScoredReceipts"]):
            self.assertEqual(receipt["queryFragmentId"], seed["queryFragmentId"])
            self.assertEqual(receipt["seedFileId"], seed["seedFileId"])
            compared = {row["fileId"] for row in seed["fileEvidence"]}
            self.assertEqual(set(receipt["scoredGallery"]), compared)
            self.assertEqual(set(receipt["excludedUnknownGalleryFiles"]),
                             set(seed["galleryFileIds"]) - compared)
        union_scored, union_unknown = set(), set()
        for receipt in language["queryScoredReceipts"]:
            union_scored |= set(receipt["scoredGallery"])
            union_unknown |= set(receipt["excludedUnknownGalleryFiles"])
        self.assertEqual(language["descriptiveScoredUnionFileCount"], len(union_scored))
        self.assertEqual(language["descriptiveUnknownUnionFileCount"], len(union_unknown))

    # -- malformed / unreviewed payloads --------------------------------
    def test_malformed_and_unreviewed_payloads_are_blocked(self):
        scene = self.scene(validation_files=6)
        self.assertEqual(self.admit(scene, None)["status"], validation.BLOCKER)
        self.assertEqual(self.admit(scene, {"schema": "code-corpus-v1"})["status"], validation.BLOCKER)
        unreviewed = self.payload({LANGUAGE: []}, status="CANDIDATE_FOR_INDEPENDENT_REVIEW")
        self.assertEqual(self.admit(scene, unreviewed)["status"], validation.BLOCKER)
        no_provenance = {"schema": validation.SCHEMA, "status": "INDEPENDENTLY_REVIEWED",
                         "reviewer": None, "reviewedOn": REVIEWED_ON, "rationale": "x",
                         "languages": {}}
        result = self.admit(scene, no_provenance)
        self.assertEqual(result["status"], validation.BLOCKER)
        self.assertEqual(result["reason"], "validation corpus lacks complete review provenance")


class ValidationProposeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.scene = ValidationScene(self.temp.name, 1, validation_files=6)
        self.index = fx.index(self.scene.manifest, self.scene.split, self.scene.extract, [])

    def propose(self):
        return validation.propose(self.index, sources=self.scene.manifest["sources"],
                                  sources_root=self.scene.root, seeds_per_language=1,
                                  gallery_files=4, distractors_per_seed=2)

    def test_proposal_keeps_the_source_target_inside_the_gallery(self):
        seed = self.propose()["languages"][LANGUAGE]["seeds"][0]
        self.assertIn(seed["seedFileId"], seed["galleryFileIds"])
        self.assertEqual(seed["relevantFileIds"], [seed["seedFileId"]])
        relevant = [row for row in seed["fileEvidence"] if row["label"] == "RELEVANT"]
        self.assertEqual([row["fileId"] for row in relevant], [seed["seedFileId"]])
        self.assertEqual(relevant[0]["coveredFragmentIds"],
                         sorted(row["fragmentId"] for row in
                                self.scene.fragments_by_file[seed["seedFileId"]]))
        self.assertGreaterEqual(len(seed["galleryFileIds"]), 4)
        distractor_parents = {row["parentFileId"] for row in seed["distractors"]}
        self.assertGreaterEqual(len(distractor_parents), 2)
        self.assertTrue(distractor_parents <= set(seed["galleryFileIds"]))

    def test_proposed_candidates_cannot_self_admit(self):
        seed = self.propose()["languages"][LANGUAGE]["seeds"][0]
        self.assertTrue(all(row["reviewStatus"] == "CANDIDATE_UNREVIEWED"
                            for row in seed["fileEvidence"]))
        with mock.patch.multiple(validation, MIN_SEEDS_PER_LANGUAGE=1, MIN_GALLERY_FILES=4,
                                 MIN_DISTRACTORS_PER_SEED=2, SCORED_K=2):
            result = validation.admit(self.payload_with(seed),
                                      files=self.scene.files,
                                      fragments=self.scene.fragments,
                                      split_files={row["fileId"]: row for row in self.scene.files},
                                      sources=self.scene.manifest["sources"],
                                      sources_root=self.scene.root)
        self.assertEqual(result["status"], validation.BLOCKER)
        issues = set(result["languages"][LANGUAGE]["issues"])
        self.assertIn("SEED_UNREVIEWED", issues)
        self.assertIn("DISTRACTOR_UNREVIEWED", issues)
        self.assertIn("EVIDENCE_UNREVIEWED", issues)

    def payload_with(self, seed):
        return {"schema": validation.SCHEMA, "status": "INDEPENDENTLY_REVIEWED",
                "reviewer": REVIEWER, "reviewedOn": REVIEWED_ON,
                "rationale": "Authored reviewed validation corpus.",
                "languages": {LANGUAGE: {"seeds": [seed]}}}


class PrepareValidationWiringTests(unittest.TestCase):
    """inputs.load -> prepare.run wiring for the retrieval-validation-corpus role."""

    LANGUAGES = ("csharp", "java", "javascript", "python", "typescript")
    VALIDATION_FILES = 51
    SEEDS = 20
    DISTRACTORS = 40

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.corpus = self.root / "runs" / "corpus-fixture"
        self.run_id = "validation-fixture-" + self.root.name
        self.counter = 0

    def build_corpus(self):
        nodes, fragments = [], []
        for language in self.LANGUAGES:
            nodes.append(fx.node("fixture", "train-%s.py" % language, language, "train",
                                 "fam-train-" + language, fx.polyglot_body(language, "t" + language)))
            nodes.append(fx.node("fixture", "final-%s.py" % language, language, "final-test",
                                 "fam-final-" + language, fx.polyglot_body(language, "f" + language)))
            for index in range(self.VALIDATION_FILES):
                nodes.append(fx.node("fixture", "val-%s-%02d.py" % (language, index), language,
                                     "validation", "fam-val-" + language,
                                     fx.polyglot_body(language, "v%s%02d" % (language, index))))
        for node in nodes:
            fragments.append(fx.fragment(node))
        fx.write_sources(self.corpus / "sources", nodes)
        manifest, split, extract = fx.corpus(
            nodes, fragments,
            sources=[fx.source("fixture", [(row["row"]["path"], row["row"]["sha256"],
                                             row["row"]["language"]) for row in nodes])])
        return nodes, manifest, split, extract

    def node_by_file(self, nodes, file_id):
        return next(node for node in nodes if node["row"]["fileId"] == file_id)

    def validation_payload(self, nodes, *, variant_coverage="all"):
        """Reviewed corpus rows plus the accepted derived variant fragments."""
        fragment_of_file = {}
        for node in nodes:
            fragment_of_file[node["row"]["fileId"]] = fx.fragment(node)
        derived_rows = []
        languages = {}
        for language in self.LANGUAGES:
            rows = [node["row"] for node in nodes
                    if node["row"]["partition"] == "validation" and node["row"]["language"] == language]
            seeds = []
            for index, parent in enumerate(rows[:self.SEEDS]):
                query = fragment_of_file[parent["fileId"]]
                distractor_rows = [row for row in rows if row["fileId"] != parent["fileId"]][:self.DISTRACTORS]
                distractors = [{"fragmentId": fragment_of_file[row["fileId"]]["fragmentId"],
                                "fragmentSha256": fragment_of_file[row["fileId"]]["sha256"],
                                "leftFragmentId": query["fragmentId"], "parentFileId": row["fileId"],
                                "label": "DISSIMILAR", "reviewStatus": "INDEPENDENTLY_REVIEWED",
                                "reviewer": REVIEWER} for row in distractor_rows]
                rows_evidence = [evidence(parent, [query], label="RELEVANT",
                                          query_fragment_id=query["fragmentId"])]
                rows_evidence += [evidence(row, [fragment_of_file[row["fileId"]]], label="DISSIMILAR",
                                           query_fragment_id=query["fragmentId"])
                                  for row in distractor_rows]
                seeds.append({"queryFragmentId": query["fragmentId"], "querySha256": query["sha256"],
                              "seedFileId": parent["fileId"], "familyId": parent["familyId"],
                              "querySourceSha256": parent["sha256"], "language": language,
                              "strata": "EXACT_COPY",
                              "galleryFileIds": [row["fileId"] for row in rows],
                              "relevantFileIds": [parent["fileId"]], "fileEvidence": rows_evidence,
                              "distractors": distractors,
                              "reviewStatus": "INDEPENDENTLY_REVIEWED", "reviewer": REVIEWER})
                if variant_coverage == "all" or (variant_coverage == "one-missing" and index > 0):
                    # The accepted nonidentical variant for this canonical
                    # seed file, bound to the real COMMENT_FORMAT derivation.
                    derived = fx.derived_positive(self.node_by_file(nodes, parent["fileId"]),
                                                  query, review=True)
                    derived_rows.append(derived)
                    variant_distractors = [dict(row, leftFragmentId=derived["fragmentId"])
                                           for row in distractors]
                    variant_evidence = [evidence(parent, [query], label="RELEVANT",
                                                 query_fragment_id=derived["fragmentId"])]
                    variant_evidence += [evidence(row, [fragment_of_file[row["fileId"]]],
                                                  label="DISSIMILAR",
                                                  query_fragment_id=derived["fragmentId"])
                                         for row in distractor_rows]
                    seeds.append({"queryFragmentId": derived["fragmentId"], "querySha256": derived["sha256"],
                                  "seedFileId": parent["fileId"], "familyId": parent["familyId"],
                                  "querySourceSha256": parent["sha256"], "language": language,
                                  "strata": "COMMENT_FORMAT",
                                  "galleryFileIds": [row["fileId"] for row in rows],
                                  "relevantFileIds": [parent["fileId"]], "fileEvidence": variant_evidence,
                                  "distractors": variant_distractors,
                                  "reviewStatus": "INDEPENDENTLY_REVIEWED", "reviewer": REVIEWER})
            languages[language] = {"seeds": seeds}
        payload = {"schema": validation.SCHEMA, "status": "INDEPENDENTLY_REVIEWED",
                   "reviewer": REVIEWER, "reviewedOn": REVIEWED_ON,
                   "rationale": "Authored reviewed validation corpus for wiring tests.",
                   "languages": languages}
        return payload, derived_rows

    def write_inputs(self, *, validation_acceptance=False, extra_fragments=()):
        nodes, manifest, split, extract = self.build_corpus()
        artifacts = {"source-manifest": manifest, "split": split, "extract": extract,
                     "reviewed-export": {"schema": "code-corpus-v1", "pairReviews": [],
                                         "extraFiles": [], "extraFragments": list(extra_fragments),
                                         "trainTriplets": []}}
        if validation_acceptance:
            artifacts["validation-acceptance"] = {"schema": "code-corpus-v1",
                                                  "extraFiles": [], "extraFragments": [],
                                                  "transformationAcceptances": []}
        entries = []
        for role, value in artifacts.items():
            name = role + ".json"
            raw = fx.canon(value)
            (self.corpus / name).write_bytes(raw)
            entries.append({"role": role, "path": "runs/corpus-fixture/" + name,
                            "byteSha256": fx.sha(raw), "canonicalSha256": digest(fx.canon(value))})
        by_role = {entry["role"]: entry for entry in entries}
        input_pins = {
            role: {"relativePath": by_role[role]["path"],
                   "bytes": len((self.corpus / (role + ".json")).read_bytes()),
                   "byteSha256": by_role[role]["byteSha256"],
                   "canonicalSha256": by_role[role]["canonicalSha256"]}
            for role in (inputs.SOURCE_MANIFEST, inputs.SPLIT, inputs.EXTRACT)
        }
        membership = corpus_index.build_final_test_membership(manifest, split, extract, input_pins)
        raw = fx.canon(membership)
        name = "final-test-membership.json"
        (self.corpus / name).write_bytes(raw)
        entries.append({"role": inputs.FINAL_TEST_MEMBERSHIP, "path": "runs/corpus-fixture/" + name,
                        "byteSha256": fx.sha(raw), "canonicalSha256": digest(fx.canon(membership))})
        return nodes, entries

    def run_prepare(self, entries):
        manifest_path = self.root / "inputs.json"
        manifest_path.write_bytes(fx.canon({"schema": "code-retrieval-inputs-v1",
                                            "corpusRunRoot": "runs/corpus-fixture",
                                            "artifacts": entries}))
        loaded = inputs.load(manifest_path, artifact_root=self.root)
        config_loaded = config_module.load(CONFIG_PATH)
        result = prepare.run(loaded=loaded, config_loaded=config_loaded, run_id=self.run_id)
        self.addCleanup(lambda: shutil.rmtree(result["directory"], ignore_errors=True))
        return result

    def test_sufficient_reviewed_corpus_is_admitted_through_the_stage(self):
        # Every canonical seed file carries an accepted nonidentical
        # COMMENT_FORMAT variant, so the recorded interpretation is enforced
        # through inputs.load -> prepare.run, not just in pack notes.
        nodes = self.build_corpus()[0]
        value, derived_rows = self.validation_payload(nodes)
        _, entries = self.write_inputs(extra_fragments=derived_rows)
        result = self.run_prepare(entries + self.declare_validation(value))
        report = result["payload"]["validationQueries"]
        self.assertEqual(report["status"], validation.ADMITTED)
        for language in self.LANGUAGES:
            row = report["languages"][language]
            self.assertEqual(row["distinctSeedFiles"], self.SEEDS)
            self.assertEqual(row["nonidenticalVariantFiles"], self.SEEDS)
            self.assertEqual(row["seedFilesWithoutNonidenticalVariant"], [])
            self.assertEqual(len(row["queryScoredReceipts"]), 2 * self.SEEDS)
            self.assertEqual(row["galleryFileCount"], self.VALIDATION_FILES)
            # Descriptive only: the fixture's 40 distractors per seed always
            # cover files 0..40, so the cross-seed scored union stays at 41
            # while the per-query receipts remain the scoring authority.
            self.assertEqual(row["descriptiveScoredUnionFileCount"], 2 * self.SEEDS + 1)
            # Descriptive only: each query declares 51 gallery files and
            # compares 41 (parent + 40 distractors), so the union of
            # UNKNOWN exclusions across queries is the same 10 files here.
            self.assertEqual(row["descriptiveUnknownUnionFileCount"],
                             self.VALIDATION_FILES - (1 + self.DISTRACTORS))
            self.assertEqual(row["distractors"], 2 * self.SEEDS * self.DISTRACTORS)
            self.assertEqual(row["strataCounts"],
                             {"COMMENT_FORMAT": self.SEEDS, "EXACT_COPY": self.SEEDS})
            self.assertEqual(report["strataOmissions"][language], ["AST_BOUND_RENAME"])
            self.assertEqual(report["dialectLimits"][language], [language])
            self.assertEqual(report["tsxCoverage"][language]["galleryTsxFileCount"], 0)
        self.assertEqual(report["distinctGalleryFiles"], len(self.LANGUAGES) * self.VALIDATION_FILES)
        # Validation admission is not training admission and not publication.
        self.assertEqual(result["payload"]["status"], "BLOCKED_INSUFFICIENT_REVIEWED_TRAIN_LABELS")
        self.assertFalse(result["payload"]["CanAuthorizePublication"])

    def test_validation_readiness_requires_a_variant_for_every_seed(self):
        nodes = self.build_corpus()[0]
        value, derived_rows = self.validation_payload(nodes, variant_coverage="one-missing")
        _, entries = self.write_inputs(extra_fragments=derived_rows)
        result = self.run_prepare(entries + self.declare_validation(value))
        report = result["payload"]["validationQueries"]
        self.assertEqual(report["status"], validation.BLOCKER)
        for language in self.LANGUAGES:
            row = report["languages"][language]
            first_val_file = self.node_by_file(nodes, [node["row"]["fileId"] for node in nodes
                                                       if node["row"]["language"] == language
                                                       and node["row"]["partition"] == "validation"][0])["row"]["fileId"]
            self.assertIn("NONIDENTICAL_VARIANT_UNCOVERED", row["issues"])
            self.assertEqual(row["seedFilesWithoutNonidenticalVariant"], [first_val_file])
            self.assertEqual(row["nonidenticalVariantFiles"], self.SEEDS - 1)

    def test_exact_only_seed_corpus_cannot_clear_validation_readiness(self):
        nodes = self.build_corpus()[0]
        value, _ = self.validation_payload(nodes, variant_coverage="none")
        _, entries = self.write_inputs()
        result = self.run_prepare(entries + self.declare_validation(value))
        report = result["payload"]["validationQueries"]
        self.assertEqual(report["status"], validation.BLOCKER)
        for language in self.LANGUAGES:
            row = report["languages"][language]
            self.assertIn("NONIDENTICAL_VARIANT_UNCOVERED", row["issues"])
            self.assertEqual(row["nonidenticalVariantFiles"], 0)
            self.assertEqual(len(row["seedFilesWithoutNonidenticalVariant"]), self.SEEDS)

    def declare_validation(self, value):
        raw = fx.canon(value)
        (self.corpus / "retrieval-validation-corpus.json").write_bytes(raw)
        return [{"role": "retrieval-validation-corpus",
                 "path": "runs/corpus-fixture/retrieval-validation-corpus.json",
                 "byteSha256": fx.sha(raw), "canonicalSha256": digest(fx.canon(value))}]

    def test_transformation_acceptance_role_cannot_clear_the_gate(self):
        _, entries = self.write_inputs(validation_acceptance=True)
        result = self.run_prepare(entries)
        report = result["payload"]["validationQueries"]
        self.assertEqual(report["status"], validation.BLOCKER)
        self.assertEqual(report["reason"], "no reviewed validation corpus artifact is declared")

    def test_validation_artifact_with_the_wrong_schema_is_refused_at_load(self):
        _, entries = self.write_inputs()
        entries += self.declare_validation({"schema": "code-corpus-v1", "languages": {}})
        with self.assertRaisesRegex(ValueError, "unexpected schema for role retrieval-validation-corpus"):
            self.run_prepare(entries)


if __name__ == "__main__":
    unittest.main()
