# Diagnostics plugin deployment and operations

Plugin ID: `videosearch-diagnostics:v1`. Route: `POST /api/mlccs-videosearch/diagnostics` with `/begin`, `/chunk`, `/complete`, `/event`.

## Safety contract

Reports use 512 KiB chunks, max 25 MiB and 64 chunks. Begin tokens expire after 30 minutes. Per-IP and installation-hash rate limits apply. Complete verifies exact size and SHA-256, rejects traversal, symlinks, encrypted archives, executables, unapproved types, >250 MiB expansion and >200:1 per-file ratio, then invokes Defender before retention. Incomplete uploads expire after 24 hours; completed reports expire after 180 days.

The application redacts usernames, absolute paths, queries, transcripts, OCR text, bearer tokens and key/value secrets before upload. Full reports require an explicit click after preview. Anonymous events use an allowlist and cannot contain content fields.

## Build the minimal upgrade

The macOS handoff contains `website/videosearch-diagnostics-upgrade.zip`, generated from the live lixinchen.ca `routes.json` as a backend-only `package_type=upgrade`. It contains only:

```text
deploy/backend/manifest.json
deploy/backend/plugins/videosearch_diagnostics_plugin.py
```

On Windows, first rerun plugin tests. Then use the current MLCCSDemo deployer documented in the active lixinchen.ca workspace. Back up current route/config/plugin and static update directories before deployment. Never use a retired legacy website workspace.

## Verify and inspect

After backend reload/restart, verify `/api/health` and unrelated routes, then run the client upload acceptance. Final storage is:

```text
D:\lixinchenca-videosearch\diagnostics\YYYY\MM\DD\<report-id>\
  metadata.json
  user-note.txt
  scan-result.json
  report.zip
```

Given report ID `vs-YYYYMMDD-<24 hex>`, inspect directly in PowerShell:

```powershell
$id = 'vs-20260728-000000000000000000000000'
$hit = Get-ChildItem 'D:\lixinchenca-videosearch\diagnostics\20*\*\*' -Directory -Filter $id -ErrorAction Stop | Select-Object -First 1
Get-Content (Join-Path $hit.FullName 'metadata.json')
Get-Content (Join-Path $hit.FullName 'scan-result.json')
Get-ChildItem $hit.FullName
```

Never extract `report.zip` into the application or website directory. Extract only into an isolated analyst directory after Defender confirmation.

## Rollback

Restore the backed-up plugin and `routes.json`, reload/restart, verify health and an unrelated route. Keep report storage untouched unless retention policy explicitly requires deletion.
