# Signed update acceptance sample

`sample-update.zip` is intentionally not an application release. `manifest.json` is signed with the acceptance-only external private key whose public half is embedded in source. Use it to test canonical manifest verification, archive signature/hash rejection and tampering. Replace the public key and regenerate the sample with an externally stored production key before stable publication.
