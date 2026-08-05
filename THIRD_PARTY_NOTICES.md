# Third-party and media notices

The root [MIT License](LICENSE) applies to the original MLCCS Video Search source code and project documentation authored for this repository.

It does not relicense third-party dependencies, models, libraries, tools, or binaries. Those components remain governed by their respective upstream licenses and notices. Exact dependency and model provenance is recorded in:

- `worker/manifests/dependencies.lock.json`
- `worker/manifests/models.lock.json`
- `worker/manifests/model-sources.json`
- `MODELS_AND_LICENSES.md`

The vendored `worker/vendor/gputil-1.4.0-py3-none-any.whl` remains subject to its upstream license. Python packages, model files, Qdrant and other runtime components downloaded or assembled for a binary release are distributed with the notices and obligations recorded for their exact locked artifacts.

The sample video under `samples/` is demonstration media. It is not licensed for reuse under the MIT License unless a separate notice explicitly says otherwise.

Nothing in the root MIT License grants rights to third-party trademarks, model weights, datasets, or media beyond the rights granted by their respective owners.
