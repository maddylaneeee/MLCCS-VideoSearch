# Build on Windows 10/11

These instructions start from a clean x64 Windows 10/11 machine with no Python, Git, FFmpeg, Visual Studio or .NET SDK. Run commands from an ordinary PowerShell window unless a command explicitly triggers elevation.

## 1. Prepare an isolated source tree

Use a clean checkout of the frozen Release Candidate. On MLCCS, keep source, tools, caches and outputs under an isolated `R:\MLCCS-VideoSearch-v1` directory. Record the commit SHA before building.

## 2. Install the build toolchain

Open PowerShell in the extracted directory:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
& .\scripts\Bootstrap-Windows.ps1
```

The script installs the pinned .NET 10 SDK, Visual Studio 2022 Build Tools managed desktop workload, and Windows 11 SDK 10.0.26100 through winget. These are developer-machine tools and must not become release prerequisites.

Close PowerShell, open a fresh PowerShell, then confirm:

```powershell
dotnet --version
Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.*' -ErrorAction Stop
```

## 3. Assemble the private runtime

`worker/manifests/dependencies.lock.json` pins the CPython embeddable runtime and all 143 resolved artifacts by exact URL, byte size and SHA-256. The bundled GPUtil wheel is pure Python and was built from the pinned 1.4.0 source. Run:

```powershell
& .\scripts\Build-PrivateRuntime.ps1
```

Do not install system Python. The result is `worker\python\python.exe`. Verify offline imports:

```powershell
& .\worker\python\python.exe -c "import torch, open_clip, faster_whisper, paddleocr, requests; print('private runtime ok')"
```

CUDA Toolkit is not required. Torch carries its user-mode CUDA dependencies; a compatible NVIDIA driver is required for all indexing and search. Video decoding uses the locked PyAV runtime and playback uses Windows media controls.

## 4. Overall compilation gate

Write the long log to `artifacts\logs`:

```powershell
& .\scripts\Build-Windows.ps1 -Configuration Release
```

This restores and compiles UI, Agent, Updater, Core, XAML/WinUI resources and the signing tool, then runs platform-independent tests. Treat failures as a batch: fix compilation, WinUI, process startup, IPC, private runtime or release generation blockers before manual UI acceptance.

## 5. Expected outputs

- Core/UI/Agent/Updater: `src\*\bin\Release\...`
- Test TRX: `tests\MLCCS.VideoSearch.Core.Tests\TestResults\core.trx`
- Logs: `artifacts\logs\build-windows-*.log`
- Private runtime: `worker\python`

No successful macOS build or `EnableWindowsTargeting` result counts as a Windows runtime pass.
