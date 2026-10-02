"""Download Serena's pinned language-server dependencies without opening the project."""
import os
from pathlib import Path

root = Path(__file__).resolve().parents[2]
os.environ["SERENA_HOME"] = str(root / ".agent-tools/serena-home")

from solidlsp.settings import SolidLSPSettings
from solidlsp.language_servers.csharp_language_server import CSharpLanguageServer
from solidlsp.language_servers.typescript_language_server import TypeScriptLanguageServer

settings = SolidLSPSettings(solidlsp_dir=os.environ["SERENA_HOME"])
custom = SolidLSPSettings.CustomLSSettings({})
CSharpLanguageServer.DependencyProvider(
    custom, CSharpLanguageServer.ls_resources_dir(settings), settings, str(root / "asblock-backend")
)
TypeScriptLanguageServer.DependencyProvider(custom, TypeScriptLanguageServer.ls_resources_dir(settings)).create_launch_command()
print("Language-server dependencies provisioned. No server or project verification started.")
