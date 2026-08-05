# Contributing

Use a focused branch and pull request. Public behavior must be complete, documented, and covered by a test; do not add experimental controls, hidden network calls, CPU/non-NVIDIA fallback, image indexing, or diagnostics/telemetry surfaces to the stable product.

Before opening a PR on Windows:

```powershell
.\scripts\Build-Windows.ps1 -Configuration Release
```

The build treats warnings as errors and runs repository/schema validation plus C# and Python tests. Release-affecting changes must also preserve deterministic Manifest serialization, P1363 verification, path-traversal rejection, SQLite Outbox replay, and update rollback. Never commit private keys, passwords, access tokens, personal media, absolute user paths, transcripts, OCR text, or full logs.

Protocol/schema versions are independent of the product version and change only for an actual compatibility break.
