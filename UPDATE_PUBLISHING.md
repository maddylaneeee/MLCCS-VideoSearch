# v1.0.0 publication

`release/version.json` is the release version source. A clean Windows release workstation builds the final immutable components and creates an unsigned `release-manifest.json`. Production signing never occurs on Windows, CI, or a distribution server.

On the release Mac, sign with:

```bash
./scripts/Sign-ReleaseManifest.sh artifacts/release/1.0.0/release-manifest.unsigned.json \
  artifacts/release/1.0.0/release-manifest.json
```

The script reads the encrypted PKCS#8 password from the release workstation key store and pipes it to the signing tool over standard input. It never places the password in arguments, environment variables, logs, Git, or chat. The tool uses ECDSA P-256/SHA-256 with fixed-width P1363 signatures and immediately self-verifies the result.

Publish in this order:

1. Back up current web metadata.
2. Upload immutable component archives and the online installer to the versioned distribution directory.
3. Upload the signed versioned `release-manifest.json`, `SHA256SUMS`, SBOM, and third-party license inventory.
4. Re-download the installer and Manifest from the public URL and verify size, SHA-256, Manifest signature, byte ranges, and every component HEAD response.
5. Merge the release PR after required CI succeeds; create and push tag `v1.0.0`.
6. Create the GitHub Release with the installer, signed Manifest, `SHA256SUMS`, SBOM, and third-party license inventory.
7. Only after the GitHub Release is healthy, atomically publish the same signed document as stable `latest.json`.
8. Verify README, GitHub, the distribution site, setup, and the app update channel all identify `1.0.0` and identical hashes.

Never overwrite the published `1.0.0` directory or tag. To mitigate a release issue, roll back `latest.json` first; retain Feedback payloads only as historical, non-default artifacts.
