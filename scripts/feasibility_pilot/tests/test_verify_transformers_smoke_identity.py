import hashlib
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

from scripts.feasibility_pilot import env_setup
from scripts.feasibility_pilot.verify_transformers_5_17_smoke import main as smoke_main, run_smoke


class VerifyPinnedModelIdentityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        data = {"pytorch_model.bin": b"fixture weights", "model.safetensors": b"converted fixture", "config.json": b"{}"}
        hashes = {name: hashlib.sha256(raw).hexdigest() for name, raw in data.items()}
        for name, raw in data.items():
            (self.root / name).write_bytes(raw)
        self.trusted = {"modelId": env_setup.MODEL_ID, "modelRevision": env_setup.MODEL_REVISION,
                        "fileHashes": hashes, "boundTokenizerAndConfigFiles": ["config.json"]}
        pinned = patch.object(env_setup, "PINNED_BIN_SHA256", hashes["pytorch_model.bin"])
        pinned.start()
        self.addCleanup(pinned.stop)

    def verify(self):
        return env_setup.verify_pinned_model_identity(self.root, trusted=self.trusted)

    def test_bound_cache_and_verified_bin_fallback(self):
        self.assertTrue(self.verify()["useSafetensors"])
        (self.root / "model.safetensors").unlink()
        self.assertFalse(self.verify()["useSafetensors"])

    def test_tampered_files_fail(self):
        for name in self.trusted["fileHashes"]:
            with self.subTest(name=name):
                path = self.root / name
                original = path.read_bytes()
                path.write_bytes(b"tampered")
                with self.assertRaises(ValueError):
                    self.verify()
                path.write_bytes(original)

    def test_untrusted_safetensors_fail(self):
        del self.trusted["fileHashes"]["model.safetensors"]
        with self.assertRaises(ValueError):
            self.verify()

    def test_missing_cache_fails_smoke(self):
        with patch("scripts.feasibility_pilot.verify_transformers_5_17_smoke.MODEL_DIR", self.root / "missing"):
            self.assertEqual(smoke_main(), 1)

    def test_identity_failure_prevents_any_loader_call(self):
        loaders = Mock()
        with patch.object(env_setup, "verify_pinned_model_identity", side_effect=ValueError("bad identity")), \
             patch.dict("sys.modules", {"torch": loaders, "transformers": loaders}):
            with self.assertRaises(ValueError):
                run_smoke(self.root)
        self.assertEqual(loaders.mock_calls, [])


if __name__ == "__main__":
    unittest.main()
