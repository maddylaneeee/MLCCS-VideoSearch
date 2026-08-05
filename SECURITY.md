# Security policy

## Supported version

Only the latest stable `v1.x` release is supported with security fixes.

## Reporting a vulnerability

Use GitHub private vulnerability reporting for this repository. Do not open a public issue containing an exploit, private media, local paths, queries, transcripts, OCR text, logs, credentials, or signing material. Include the affected version, impact, minimum reproduction, and a safe proof of concept.

## Release integrity

Windows binaries are not Authenticode-signed. Each official release publishes `SHA256SUMS`; the online installer and updater also embed the production P-256 public key and reject a Manifest unless its SHA-256/P1363 signature is valid. Components are verified by archive size/SHA-256 and by every extracted file hash.

The production private key is never stored in Git, CI, GitHub Actions, build machines, distribution infrastructure, or the web server. It is used only on the approved secure signing workstation from encrypted PKCS#8 storage.

## Local-data boundary

Qdrant binds to a dynamic loopback port and uses a per-install API key protected for the current Windows user. Paths and source text remain in SQLite; Qdrant payloads contain only opaque IDs, times, and model/algorithm versions. v1.0.0 contains no telemetry or diagnostics upload.
