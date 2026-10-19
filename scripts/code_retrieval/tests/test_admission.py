import tempfile
import unittest
from pathlib import Path

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_analysis.splits import normalized_hash
from scripts.code_retrieval import admission
from scripts.code_retrieval.paths import RETRIEVAL_ROOT
from scripts.code_retrieval.tests import _support as fx

LANGUAGE = "python"


def review(pairs, *, reviewer="Pair reviewer", reviewed_on="2026-10-01"):
    return {"status": "REVIEWED", "rubric": fx.RUBRIC, "reviewer": reviewer, "reviewedOn": reviewed_on,
            "rationale": "Authored pair assertion.", "fragmentPairs": list(pairs)}


class AdmissionTests(unittest.TestCase):
    def setUp(self):
        RETRIEVAL_ROOT.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=RETRIEVAL_ROOT)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.counter = 0

    def scene(self, *, grant=None, held_family=None, held_normalized_value=None, held_normalized_body=None,
              same_as_query_negative=False, same_as_query_positive=False, duplicate_negative=False,
              triplets=None, extra_fragments=(), pair_reviews=(), cap=None, min_anchors=0, min_non_identical=0,
              positive=None, negatives=None, extra_nodes=()):
        q_body = fx.python_body("q")
        p_body = fx.python_body("q") if same_as_query_positive else fx.python_body("p")
        n2_body = fx.python_body("q") if same_as_query_negative else (
            fx.python_body("n1") if duplicate_negative else fx.python_body("n2"))
        nodes = [
            fx.node("fixture", "q.py", LANGUAGE, "train", "fam-train", q_body),
            fx.node("fixture", "p.py", LANGUAGE, "train", "fam-train", p_body),
            fx.node("fixture", "n1.py", LANGUAGE, "train", "fam-train", fx.python_body("n1")),
            fx.node("fixture", "n2.py", LANGUAGE, "train", "fam-train", n2_body),
            fx.node("fixture", "ft.py", LANGUAGE, "final-test", "fam-ft", fx.python_body("ft")),
        ]
        if held_family is not None:
            nodes.append(fx.node("fixture", "h.py", LANGUAGE, "validation", held_family, fx.python_body("h")))
        elif held_normalized_value is not None or held_normalized_body is not None:
            nodes.append(fx.node("fixture", "h.py", LANGUAGE, "validation", "fam-held",
                                 held_normalized_body or fx.python_body("h"),
                                 normalized=held_normalized_value))
        nodes += list(extra_nodes)
        self.counter += 1
        sources_root = self.root / ("sources-%d" % self.counter)
        fx.write_sources(sources_root, nodes)
        q, p = fx.fragment(nodes[0]), fx.fragment(nodes[1])
        n1, n2 = fx.fragment(nodes[2]), fx.fragment(nodes[3])
        manifest, split, extract = fx.corpus(nodes, [q, p, n1, n2, *extra_fragments],
                                             sources=[fx.source("fixture",
                                                                [(node["row"]["path"], node["row"]["sha256"], LANGUAGE)
                                                                 for node in nodes], grants=grant)])
        rows = triplets if triplets is not None else [fx.triplet(LANGUAGE, q, positive or p, negatives or [n1, n2])]
        index = fx.index(manifest, split, extract, rows, pair_reviews=pair_reviews,
                         extra_fragments=extra_fragments)
        config = fx.retrieval_config()
        config["admission"]["minAnchorsPerLanguage"] = min_anchors
        config["admission"]["minNonIdenticalPositiveAnchorsPerLanguage"] = min_non_identical
        if cap is not None:
            config["training"]["maxAdmittedTriplets"] = cap
        return {"index": index, "config": config, "sources": manifest["sources"], "sources_root": sources_root,
                "manifest": manifest, "split": split, "extract": extract,
                "q": q, "p": p, "n1": n1, "n2": n2, "nodes": nodes}

    def evaluate(self, scene):
        return admission.evaluate(scene["index"], scene["config"], sources=scene["sources"],
                                  sources_root=scene["sources_root"])

    # -- admission of reviewed volume ------------------------------------
    def test_one_reviewed_anchor_is_admitted(self):
        result = self.evaluate(self.scene(min_anchors=0))
        self.assertEqual(result["status"], admission.ADMITTED)
        self.assertEqual(result["languages"][LANGUAGE]["distinctAnchorFiles"], 1)
        self.assertEqual(result["languages"][LANGUAGE]["distinctNonIdenticalPositiveAnchorFiles"], 1)

    def test_insufficient_reviewed_volume_is_a_blocker(self):
        result = self.evaluate(self.scene(min_anchors=2))
        self.assertEqual(result["status"], admission.INSUFFICIENT)
        self.assertIn("INSUFFICIENT_ANCHOR_FILES", result["languages"][LANGUAGE]["issues"])

    def test_byte_identical_positive_does_not_count_as_non_identical(self):
        result = self.evaluate(self.scene(min_anchors=0, min_non_identical=1, same_as_query_positive=True))
        self.assertEqual(result["languages"][LANGUAGE]["distinctNonIdenticalPositiveAnchorFiles"], 0)
        self.assertIn("INSUFFICIENT_NON_IDENTICAL_POSITIVES", result["languages"][LANGUAGE]["issues"])

    def test_total_triplet_cap_is_enforced(self):
        result = self.evaluate(self.scene(min_anchors=0, cap=0))
        self.assertEqual(result["status"], admission.INSUFFICIENT)
        self.assertTrue(result["totals"]["capExceeded"])
        self.assertIn(admission.TOTAL_CAP, result["languages"]["_totals"]["issues"])

    # -- disjointness ----------------------------------------------------
    def test_positive_reused_as_negative_is_rejected(self):
        scene = self.scene(min_anchors=0)
        scene["index"]["triplets"][0]["negatives"][0] = fx.pair("DISSIMILAR", scene["q"], scene["p"])
        result = self.evaluate(scene)
        self.assertIn(admission.NON_DISJOINT, {row["reason"] for row in result["excluded"]})

    def test_negative_parents_must_be_distinct(self):
        scene = self.scene(min_anchors=0)
        n1a = fx.fragment(scene["nodes"][2], 0, 20)
        n1b = fx.fragment(scene["nodes"][2], 20, len(scene["nodes"][2]["body"]))
        scene["index"]["fragments"] += [n1a, n1b]
        scene["index"]["triplets"] = [fx.triplet(LANGUAGE, scene["q"], scene["p"], [n1a, n1b])]
        result = self.evaluate(scene)
        self.assertIn(admission.SHARED_NEGATIVE_PARENT, {row["reason"] for row in result["excluded"]})

    def test_duplicate_negative_content_is_rejected(self):
        result = self.evaluate(self.scene(min_anchors=0, duplicate_negative=True))
        self.assertIn(admission.DUPLICATE_NEGATIVE, {row["reason"] for row in result["excluded"]})

    def test_negative_identical_to_anchor_is_rejected(self):
        result = self.evaluate(self.scene(min_anchors=0, same_as_query_negative=True))
        self.assertIn(admission.IDENTICAL_NEGATIVE, {row["reason"] for row in result["excluded"]})

    # -- derived positives ----------------------------------------------
    def test_unreviewed_derived_positive_is_rejected(self):
        scene = self.scene(min_anchors=0)
        derived = fx.derived_positive(scene["nodes"][1], scene["p"], review=False)
        scene["index"]["triplets"][0] = fx.triplet(LANGUAGE, scene["q"], derived, [scene["n1"], scene["n2"]])
        scene["index"]["fragments"].append(derived)
        result = self.evaluate(scene)
        self.assertIn(admission.DERIVED_UNVERIFIED, {row["reason"] for row in result["excluded"]})

    def test_accepted_derived_positive_is_admitted(self):
        scene = self.scene(min_anchors=0)
        derived = fx.derived_positive(scene["nodes"][1], scene["p"], review=True)
        scene["index"]["triplets"][0] = fx.triplet(LANGUAGE, scene["q"], derived, [scene["n1"], scene["n2"]])
        scene["index"]["fragments"].append(derived)
        result = self.evaluate(scene)
        self.assertEqual(result["status"], admission.ADMITTED)
        self.assertEqual(result["languages"][LANGUAGE]["distinctNonIdenticalPositiveAnchorFiles"], 1)

    def test_tampered_derived_bytes_are_rejected(self):
        scene = self.scene(min_anchors=0)
        derived = fx.derived_positive(scene["nodes"][1], scene["p"], review=True)
        derived["bodyBase64"] = "ZmFrZQ=="
        scene["index"]["triplets"][0] = fx.triplet(LANGUAGE, scene["q"], derived, [scene["n1"], scene["n2"]])
        scene["index"]["fragments"].append(derived)
        result = self.evaluate(scene)
        self.assertIn(admission.DERIVED_UNVERIFIED, {row["reason"] for row in result["excluded"]})

    def test_derived_held_out_content_overlap_is_rejected(self):
        base = self.scene(min_anchors=0)
        output = base["nodes"][1]["body"] + b"# derived\n"
        held = normalized_hash(output, LANGUAGE)
        scene = self.scene(min_anchors=0, held_normalized_value=held)
        derived = fx.derived_positive(scene["nodes"][1], scene["p"], review=True, output=output)
        scene["index"]["triplets"][0] = fx.triplet(LANGUAGE, scene["q"], derived, [scene["n1"], scene["n2"]])
        scene["index"]["fragments"].append(derived)
        result = self.evaluate(scene)
        self.assertIn(admission.DERIVED_UNVERIFIED, {row["reason"] for row in result["excluded"]})

    # -- reconciliation with reviewed pair assertions --------------------
    def test_pair_triplet_contradiction_is_quarantined(self):
        base = self.scene(min_anchors=0)
        contradiction = review([fx.pair("DISSIMILAR", base["q"], base["p"])])
        scene = self.scene(min_anchors=0, pair_reviews=[contradiction])
        result = self.evaluate(scene)
        self.assertIn(admission.CONTRADICTORY_PAIR, {row["reason"] for row in result["excluded"]})

    def test_explicit_unknown_assertion_is_quarantined(self):
        base = self.scene(min_anchors=0)
        unknown = review([fx.pair("UNKNOWN", base["q"], base["n1"])])
        scene = self.scene(min_anchors=0, pair_reviews=[unknown])
        result = self.evaluate(scene)
        self.assertIn(admission.UNKNOWN_ENDPOINT, {row["reason"] for row in result["excluded"]})

    # -- cross-record reconciliation (symmetry-aware unordered pairs) ----
    def test_contradicting_triplets_are_quarantined_together(self):
        scene = self.scene(min_anchors=0)
        q, p, n1, n2 = scene["q"], scene["p"], scene["n1"], scene["n2"]
        first = fx.triplet(LANGUAGE, q, n1, [p, n2])
        second = fx.triplet(LANGUAGE, p, q, [n1, n2])
        scene["index"]["triplets"] = [first, second]
        result = self.evaluate(scene)
        reasons = {row["queryFragmentId"]: row["reason"] for row in result["excluded"]}
        self.assertEqual(reasons.get(q["fragmentId"]), admission.CONTRADICTORY_PAIR)
        self.assertEqual(reasons.get(p["fragmentId"]), admission.CONTRADICTORY_PAIR)
        self.assertEqual(result["languages"][LANGUAGE]["reviewedAnchors"], 0)

    def test_reverse_orientation_pair_contradiction_is_quarantined(self):
        base = self.scene(min_anchors=0)
        reverse = review([fx.pair("DISSIMILAR", base["p"], base["q"])])
        scene = self.scene(min_anchors=0, pair_reviews=[reverse])
        result = self.evaluate(scene)
        self.assertIn(admission.CONTRADICTORY_PAIR, {row["reason"] for row in result["excluded"]})

    def test_negative_copying_positive_bytes_is_rejected(self):
        twin_node = fx.node("fixture", "twin.py", LANGUAGE, "train", "fam-train", fx.python_body("p"))
        twin = fx.fragment(twin_node)
        scene = self.scene(min_anchors=0, extra_nodes=[twin_node], extra_fragments=[twin])
        scene["index"]["triplets"] = [fx.triplet(LANGUAGE, scene["q"], scene["p"], [twin, scene["n2"]])]
        self.assertEqual(twin["sha256"], scene["p"]["sha256"])
        result = self.evaluate(scene)
        self.assertIn(admission.NON_DISJOINT, {row["reason"] for row in result["excluded"]})

    # -- canonical original-file anchor identity -------------------------
    def test_alias_fragments_count_anchors_by_canonical_file(self):
        scene = self.scene(min_anchors=2)
        q_node = scene["nodes"][0]
        alias = {**q_node["row"], "fileId": "alias-anchor"}
        alias_fragment = fx.fragment(q_node, fragment_id="alias-anchor-fragment",
                                     patch={"fileId": "alias-anchor"})
        scene["index"] = fx.index(
            scene["manifest"], scene["split"], scene["extract"],
            [fx.triplet(LANGUAGE, scene["q"], scene["p"], [scene["n1"], scene["n2"]]),
             fx.triplet(LANGUAGE, alias_fragment, scene["p"], [scene["n1"], scene["n2"]])],
            extra_files=[alias], extra_fragments=[alias_fragment])
        self.assertEqual(scene["index"]["fileAliases"], {"alias-anchor": q_node["row"]["fileId"]})
        result = self.evaluate(scene)
        language = result["languages"][LANGUAGE]
        self.assertEqual(language["reviewedAnchors"], 2)
        self.assertEqual(language["distinctAnchorFiles"], 1)
        self.assertEqual(result["status"], admission.INSUFFICIENT)
        self.assertIn("INSUFFICIENT_ANCHOR_FILES", language["issues"])

    # -- provenance ------------------------------------------------------
    def test_missing_review_date_is_rejected(self):
        scene = self.scene(min_anchors=0)
        scene["index"]["triplets"][0]["reviewedOn"] = None
        result = self.evaluate(scene)
        self.assertIn(admission.MISSING_PROVENANCE, {row["reason"] for row in result["excluded"]})

    def test_historical_triplet_is_accepted_from_dated_pairs(self):
        base = self.scene(min_anchors=0)
        q, p, n1, n2 = base["q"], base["p"], base["n1"], base["n2"]
        scene = self.scene(min_anchors=0, pair_reviews=[review([fx.pair("SIMILAR", q, p),
                                                               fx.pair("DISSIMILAR", q, n1),
                                                               fx.pair("DISSIMILAR", q, n2)])])
        scene["index"]["triplets"][0]["reviewedOn"] = None
        result = self.evaluate(scene)
        self.assertEqual(result["status"], admission.ADMITTED)

    # -- purpose / held-out / source integrity ---------------------------
    def test_purpose_denial_excludes_triplet(self):
        result = self.evaluate(self.scene(min_anchors=0, grant={"trainingAllowed": False}))
        self.assertIn(admission.INELIGIBLE_PURPOSE, {row["reason"] for row in result["excluded"]})

    def test_held_out_family_overlap_raises(self):
        scene = self.scene(min_anchors=0)
        scene["index"]["lineage"]["splitFiles"]["synthetic-held"] = {
            "fileId": "synthetic-held", "familyId": "fam-train", "partition": "validation",
            "language": LANGUAGE, "normalizedSha256": "0" * 64}
        with self.assertRaisesRegex(ValueError, "held-out"):
            self.evaluate(scene)

    def test_held_out_content_overlap_raises(self):
        with self.assertRaisesRegex(ValueError, "held-out"):
            self.evaluate(self.scene(min_anchors=0, held_normalized_body=fx.python_body("q")))

    def test_appended_source_bytes_are_detected(self):
        scene = self.scene(min_anchors=0)
        target = scene["sources_root"] / "fixture" / "q.py"
        target.write_bytes(target.read_bytes() + b"\n# appended\n")
        result = self.evaluate(scene)
        self.assertIn(admission.ENDPOINT_UNRESOLVABLE, {row["reason"] for row in result["excluded"]})


if __name__ == "__main__":
    unittest.main()
