$ErrorActionPreference = 'Stop'
$agentRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $agentRoot
try {
    if (-not (Test-Path '.agent-tools/serena/Scripts/python.exe')) {
        python -m venv .agent-tools/serena
        if ($LASTEXITCODE -ne 0) { throw 'Python venv creation failed.' }
    }
    & .agent-tools/serena/Scripts/python.exe -m pip install --disable-pip-version-check -r scripts/agents/serena-requirements.txt
    if ($LASTEXITCODE -ne 0) { throw 'Serena installation failed.' }
    pnpm --dir scripts/agents install --frozen-lockfile --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw 'Repomix installation failed.' }
    & .agent-tools/serena/Scripts/python.exe scripts/agents/provision-language-servers.py
    if ($LASTEXITCODE -ne 0) { throw 'Language-server provisioning failed.' }
    & .agent-tools/serena/Scripts/python.exe scripts/agents/configure-serena.py
    if ($LASTEXITCODE -ne 0) { throw 'Serena mode configuration failed.' }
    node scripts/agents/configure-clients.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Client configuration failed.' }
} finally {
    Pop-Location
}
