# Creates a local Python environment for the Songs screen.
# Run once from the repository root:
#   powershell -ExecutionPolicy Bypass -File tools\setup-transcription.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$venv = Join-Path $root ".venv-transcription"
$python = Join-Path $venv "Scripts\python.exe"

if (-not (Test-Path $python)) {
    python -m venv $venv
}

& $python -m pip install --upgrade pip
& $python -m pip install -r (Join-Path $PSScriptRoot "requirements-transcription.txt")
& $python -m pip install --no-deps "basic-pitch>=0.4.0,<0.5"

Write-Host ""
Write-Host "Ready. The app looks for .venv-transcription automatically."
Write-Host "Python: $python"
