"""Keep this checkout's isolated Serena runtime focused on read-only retrieval."""
import os
from pathlib import Path

root = Path(__file__).resolve().parents[2]
os.environ["SERENA_HOME"] = str(root / ".agent-tools/serena-home")

from serena.config.serena_config import SerenaConfig
from serena.util.yaml import load_yaml, save_yaml

config = SerenaConfig.from_config_file()
path = config.config_file_path
data = load_yaml(path)
data["base_modes"] = []
data["default_modes"] = ["planning"]
save_yaml(path, data)
print("Isolated Serena modes configured: planning; no global client settings changed.")
