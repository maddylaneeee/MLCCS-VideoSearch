# Signed update publishing

Stable manifest URL: `https://lixinchen.ca/downloads/mlccs-videosearch/stable/latest.json`. Version files live under `stable/<version>/`.

The source contains only an acceptance public key. The matching acceptance private key remains outside the project in macOS `Desktop/temp` and is excluded from handoff. Before stable release, generate a production P-256 key in controlled storage, replace the embedded public key, rebuild and repeat update acceptance. Never put a private key in source, logs, scripts, ZIPs or FileShare.

## Create a signed version

```powershell
& .\scripts\New-SignedUpdate.ps1 `
  -Archive .\artifacts\portable\MLCCS-VideoSearch-0.1.0-win-x64.zip `
  -ExternalPrivateKey 'X:\secure\mlccs-videosearch-production-private.pem' `
  -Version 0.1.0 `
  -MinimumVersion 0.1.0 `
  -DownloadUrl 'https://lixinchen.ca/downloads/mlccs-videosearch/stable/0.1.0/MLCCS-VideoSearch-0.1.0-win-x64.zip'
```

The manifest contains protocol/app/minimum versions, HTTPS URL, size, SHA-256, archive signature, UTC publication time, notes, mandatory flag and manifest signature.

## Two-phase publication

1. Upload the versioned ZIP, manifest/signature and release notes under `stable/<version>/`.
2. From an external client, verify HTTPS, byte size, SHA-256, signature and HTTP Range resume.
3. Exercise test-channel update, tamper rejection, insufficient disk, file occupancy, safe process exit, database compatibility and health-check rollback.
4. Only then atomically replace `stable/latest.json` with the verified manifest.

The app only checks automatically by default. It never silently installs/restarts. The prompt offers Update now, Later and Skip this version. Disabling checks prevents proactive network checks.

## Rollback

If download metadata is wrong, atomically restore the prior `latest.json`; never overwrite a versioned directory in place. If the package is faulty, publish a new higher version after fixing it. Client-side updater retains `previous`, promotes staging atomically and restores it when the new UI health probe fails.

