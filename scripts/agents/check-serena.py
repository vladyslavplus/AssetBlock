"""Opt-in MCP retrieval smoke check. No onboarding or application verification."""
import asyncio
import json
import re
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

root = Path(__file__).resolve().parents[2]
expected = {
    "initial_instructions", "get_current_config", "get_symbols_overview",
    "find_symbol", "find_referencing_symbols", "search_for_pattern",
}


def text(result):
    if result.isError:
        raise RuntimeError(str(result.content))
    return "\n".join(item.text for item in result.content if item.type == "text")


def check_planning_modes(manual, config):
    manual_modes = re.findall(r'<mode name="([^"]+)">', manual)
    active_modes = re.findall(r"^Active modes: (.*)$", config, re.MULTILINE)
    assert manual_modes == ["planning"], f"Unexpected manual modes: {manual_modes}"
    assert len(active_modes) == 1, f"Expected one active modes line: {active_modes}"
    modes = [mode.strip() for mode in active_modes[0].split(",")]
    assert modes == ["planning"], f"Unexpected active modes: {modes}"


async def main():
    # Guard against the former substring check accepting extra active modes.
    planning_manual = '<mode name="planning">'
    check_planning_modes(planning_manual, "Active modes: planning")
    for extra_mode in ("interactive", "editing"):
        for manual, config in (
            (planning_manual, f"Active modes: planning, {extra_mode}"),
            (f'{planning_manual}\n<mode name="{extra_mode}">', "Active modes: planning"),
        ):
            try:
                check_planning_modes(manual, config)
            except AssertionError:
                pass
            else:
                raise AssertionError(f"Accepted extra mode: {extra_mode}")
    print("Passed exact planning-only mode checks: 1 accepted, 4 rejected.")
    output = root / "artifacts/agent-tools-checks"
    output.mkdir(parents=True, exist_ok=True)
    server = StdioServerParameters(
        command="node", args=[str(root / "scripts/agents/start-serena.mjs")], cwd=str(root),
    )
    with (output / "serena-smoke.log").open("w", encoding="utf-8") as log:
        async with stdio_client(server, errlog=log) as (reader, writer):
            async with ClientSession(reader, writer) as session:
                await session.initialize()
                tools = await session.list_tools()
                assert {tool.name for tool in tools.tools} == expected
                manual = text(await session.call_tool("initial_instructions", {}))
                config = text(await session.call_tool("get_current_config", {}))
                assert "Active project: AssetBlock" in config
                check_planning_modes(manual, config)
                assert "Language server status: ready" in config
                cases = [
                    ("asblock-backend/AssetBlock.Application/UseCases/Assets/GetAssets/GetAssetsQueryHandler.cs", "GetAssetsQueryHandler"),
                    ("asblock-frontend/lib/catalog/asset-detail-query.ts", "similarAssetsQueryOptions"),
                ]
                for file, name in cases:
                    overview = json.loads(text(await session.call_tool("get_symbols_overview", {"relative_path": file, "depth": 1, "max_answer_chars": 4000})))
                    assert name in json.dumps(overview)
                    symbols = json.loads(text(await session.call_tool("find_symbol", {
                        "relative_path": file, "name_path_pattern": name, "include_body": False,
                        "max_matches": 1, "max_answer_chars": 4000,
                    })))
                    assert len(symbols) == 1 and symbols[0]["relative_path"] == file
                references = json.loads(text(await session.call_tool("find_referencing_symbols", {
                    "relative_path": cases[1][0], "name_path": cases[1][1], "max_answer_chars": 6000,
                })))
                assert any(path.replace("\\", "/").endswith("components/assets/similar-assets-block.tsx") for path in references)
                print("Passed MCP startup, 6 tools, planning-only modes, C#/TypeScript overview/find_symbol and UI references.")


asyncio.run(main())
