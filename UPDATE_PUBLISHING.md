# v1.0.0 publication

`release/version.json` is the release version source. Windows/MLCCS or the approved CRC target builds the final immutable components and creates an unsigned `release-manifest.json`. Production signing never occurs on Windows, CI, MLCCS, CRC, FileShare, or the server.

On the release Mac, sign with:

```bash
./scripts/Sign-ReleaseManifest.sh artifacts/release/1.0.0/release-manifest.unsigned.json \
  artifacts/release/1.0.0/release-manifest.json
```

The script reads the encrypted PKCS#8 password directly from macOS Keychain and pipes it to the signing tool over standard input. It never places the password in arguments, environment variables, logs, Git, or chat. The tool uses ECDSA P-256/SHA-256 with fixed-width P1363 signatures and immediately self-verifies the result.

Publish in this order:

1. Back up current web metadata.
2. Upload immutable component archives to `/docs/mlccs-video-search/1.0.0/`.
3. Upload `MLCCS-VideoSearch-Online-Setup-1.0.0.exe`.
4. Upload the signed versioned `release-manifest.json`.
5. Re-download installer/Manifest from the public URL and verify size, SHA-256, Manifest signature, byte ranges, and every component HEAD.
6. Merge the release PR after required CI succeeds; create and push tag `v1.0.0`.
7. Create the GitHub Release with the installer, signed Manifest, `SHA256SUMS`, SBOM, and third-party license inventory.
8. Only after the GitHub Release is healthy, atomically publish the same signed document as stable `latest.json`.
9. Verify README, GitHub, lixinchen.ca, setup, and the app update channel all identify `1.0.0` and identical hashes.

Never overwrite the published `1.0.0` directory or tag. To mitigate a release issue, roll back `latest.json` first; retain Feedback payloads only as historical, non-default artifacts.
