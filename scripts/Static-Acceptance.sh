#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd "$(dirname "$0")/.." && pwd -P)"
artifact_root="$project_root/artifacts/static-acceptance"
mkdir -p "$artifact_root"
venv_root="$project_root/artifacts/static-venv"
if [[ ! -x "$venv_root/bin/python" ]]; then
  python3 -m venv "$venv_root"
fi
if ! "$venv_root/bin/python" -c 'import jsonschema, numpy, pypinyin, requests' >/dev/null 2>&1; then
  "$venv_root/bin/python" -m pip install --disable-pip-version-check \
    'jsonschema==4.25.1' 'numpy==2.2.6' 'pypinyin==0.54.0' 'requests==2.32.4'
fi
"$venv_root/bin/python" -m compileall -q "$project_root/worker" "$project_root/tests/python"
"$venv_root/bin/python" -m unittest discover -s "$project_root/tests/python" -v 2>&1 | tee "$artifact_root/python-tests.log"
dotnet test "$project_root/tests/MLCCS.VideoSearch.Core.Tests/MLCCS.VideoSearch.Core.Tests.csproj" -c Release 2>&1 | tee "$artifact_root/dotnet-tests.log"
{
  dotnet build "$project_root/src/MLCCS.VideoSearch.Core/MLCCS.VideoSearch.Core.csproj" -c Release
  dotnet build "$project_root/src/MLCCS.VideoSearch.Agent/MLCCS.VideoSearch.Agent.csproj" -c Release -p:EnableWindowsTargeting=true
  dotnet build "$project_root/src/MLCCS.VideoSearch.Updater/MLCCS.VideoSearch.Updater.csproj" -c Release -p:EnableWindowsTargeting=true
  dotnet build "$project_root/tools/MLCCS.VideoSearch.SigningTool/MLCCS.VideoSearch.SigningTool.csproj" -c Release
  dotnet restore "$project_root/src/MLCCS.VideoSearch.UI/MLCCS.VideoSearch.UI.csproj" -p:EnableWindowsTargeting=true
  find "$project_root/src/MLCCS.VideoSearch.UI" -name '*.xaml' -print0 | xargs -0 -n1 xmllint --noout
  echo 'WINDOWS_ONLY: WinUI XAML compiler is a Windows executable and was not run on macOS.'
} 2>&1 | tee "$artifact_root/cross-platform-build.log"
pwsh -NoProfile -Command "Get-ChildItem '$project_root/scripts' -Filter '*.ps1' | ForEach-Object { [scriptblock]::Create((Get-Content -Raw \$_.FullName)) | Out-Null }" 2>&1 | tee "$artifact_root/powershell-parse.log"
python3 "$project_root/scripts/validate_repository.py" 2>&1 | tee "$artifact_root/repository-validation.log"
