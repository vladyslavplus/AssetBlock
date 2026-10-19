import copy
import json
import unittest
from pathlib import Path

from scripts.code_retrieval import config as config_module

CONFIG_PATH = Path(__file__).resolve().parents[1] / "config" / "training-config.json"


class ConfigTests(unittest.TestCase):
    def setUp(self):
        self.raw = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))

    def mutated(self, mutate):
        value = copy.deepcopy(self.raw)
        mutate(value)
        return value

    def test_frozen_config_loads_and_freezes_plan_bounds(self):
        loaded = config_module.load(CONFIG_PATH)
        config = loaded["config"]
        self.assertEqual(config["protocolRevision"], "code-retrieval-protocol-v1")
        self.assertEqual(config["seed"], 42)
        self.assertEqual(config["admission"]["minAnchorsPerLanguage"], 20)
        self.assertEqual(config["admission"]["maxAnchorsPerLanguage"], 60)
        self.assertEqual(config["admission"]["minNonIdenticalPositiveAnchorsPerLanguage"], 10)
        self.assertEqual(config["admission"]["minNegativesPerAnchor"], 2)
        self.assertEqual(config["proposal"]["anchorsPerLanguage"], 30)
        self.assertEqual(config["proposal"]["negativesPerAnchor"], 2)
        self.assertEqual(config["training"]["maxTokens"], 256)
        self.assertEqual(config["training"]["framingTokens"], 4)
        self.assertEqual(config["training"]["embeddingDimensions"], 768)
        self.assertEqual(config["training"]["trainableEncoderBlocks"], 2)
        self.assertEqual(config["training"]["maxAdmittedTriplets"], 300)
        self.assertEqual(config["finalTest"], {"sealed": True, "scored": False, "tuning": False})
        self.assertEqual(loaded["byteSha256"], config_module.digest(CONFIG_PATH.read_bytes()))
        self.assertEqual(loaded["protocolSha256"], config_module.PROTOCOL_SHA256)

    def test_seed_is_frozen(self):
        with self.assertRaisesRegex(ValueError, "seed"):
            config_module.validate(self.mutated(lambda c: c.update(seed=7)))

    def test_supported_languages_are_frozen(self):
        with self.assertRaisesRegex(ValueError, "languages"):
            config_module.validate(self.mutated(lambda c: c.update(languages=["python"])))

    def test_protocol_revision_is_required(self):
        with self.assertRaisesRegex(ValueError, "protocol revision"):
            config_module.validate(self.mutated(lambda c: c.pop("protocolRevision")))

    def test_protocol_bounds_cannot_be_weakened(self):
        cases = [
            lambda c: c["admission"].update(minAnchorsPerLanguage=3),
            lambda c: c["admission"].update(maxAnchorsPerLanguage=500),
            lambda c: c["admission"].update(minNegativesPerAnchor=1),
            lambda c: c["admission"].update(minNonIdenticalPositiveAnchorsPerLanguage=0),
            lambda c: c["proposal"].update(anchorsPerLanguage=1000),
            lambda c: c["training"].update(optimizer="SGD"),
            lambda c: c["training"].update(maxTokens=512),
            lambda c: c["training"].update(framingTokens=8),
            lambda c: c["training"].update(embeddingDimensions=1024),
            lambda c: c["training"].update(trainableEncoderBlocks=24),
            lambda c: c["training"].update(maxAdmittedTriplets=9999),
            lambda c: c["finalTest"].update(scored=True),
            lambda c: c["admission"].update(positiveTransformations=["MAGIC"]),
        ]
        for mutate in cases:
            with self.subTest(mutate=mutate):
                with self.assertRaises(ValueError):
                    config_module.validate(self.mutated(mutate))

    def test_rehashing_a_weaker_config_is_not_approval(self):
        value = self.mutated(lambda c: c.update(protocolRevision="code-retrieval-protocol-v2"))
        value["admission"]["minAnchorsPerLanguage"] = 1
        # The digests are internally consistent; the protocol identity is not approved.
        self.assertIsInstance(config_module.digest(config_module.canonical(value)), str)
        with self.assertRaises(ValueError):
            config_module.validate(value)

    def test_unsupported_transformation_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "protocol|transformation"):
            config_module.validate(self.mutated(
                lambda c: c["admission"].update(positiveTransformations=["MAGIC"])))


if __name__ == "__main__":
    unittest.main()
