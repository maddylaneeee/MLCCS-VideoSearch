# Portable x64 release

Prerequisites: `Build-Windows.ps1` and `Build-PrivateRuntime.ps1` have passed; private FFmpeg/ffprobe and libVLC payloads are present and verified; no signing private key exists anywhere under the project directory.

```powershell
Set-Location 'D:\MLCCSapps\MLCCS-VideoSearch-FromScratch'
& .\scripts\New-PortableRelease.ps1 -Version 0.1.0
```

The script publishes self-contained x64 UI, Agent and Updater, copies the private worker/runtime and frozen schemas, writes `version.json`, then creates:

```text
artifacts\portable\MLCCS-VideoSearch-0.1.0-win-x64.zip
artifacts\portable\MLCCS-VideoSearch-0.1.0-win-x64.zip.sha256
```

The ZIP must contain no source, models, user media, cache, logs, credentials or build tools. Runtime models are downloaded on first run from `models.lock.json` with byte progress, pause/resume, HTTP Range continuation, mirror fallback and SHA-256 verification.

Before release, expand the ZIP in a clean directory and verify that it does not resolve system Python, Git, FFmpeg or development DLLs. Perform the clean-machine checks in `WINDOWS_ACCEPTANCE.md`. A release is not accepted merely because ZIP creation succeeds.

