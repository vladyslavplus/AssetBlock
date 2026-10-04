"""Patch isolated node-gyp so nested MSVS generator paths can be created."""

from pathlib import Path

NEEDLE = """            base_temp_dir = \"\" if IsCygwin() else os.path.dirname(filename)
            # Pick temporary file.
            tmp_fd, self.tmp_path = tempfile.mkstemp("""

PATCH = """            base_temp_dir = \"\" if IsCygwin() else os.path.dirname(filename)
            if base_temp_dir:
                os.makedirs(base_temp_dir, exist_ok=True)
            # Pick temporary file.
            tmp_fd, self.tmp_path = tempfile.mkstemp("""


def patch_node_gyp_mkdir(prefix: Path) -> bool:
    target = (
        prefix
        / "node_modules"
        / "node-gyp"
        / "gyp"
        / "pylib"
        / "gyp"
        / "common.py"
    )
    if not target.exists():
        # node-gyp may live under the parsers package instead.
        matches = list(prefix.rglob("node-gyp/gyp/pylib/gyp/common.py"))
        if not matches:
            return False
        target = matches[0]
    text = target.read_text(encoding="utf-8")
    if "os.makedirs(base_temp_dir, exist_ok=True)" in text:
        return True
    if NEEDLE not in text:
        return False
    target.write_text(text.replace(NEEDLE, PATCH, 1), encoding="utf-8")
    return True
