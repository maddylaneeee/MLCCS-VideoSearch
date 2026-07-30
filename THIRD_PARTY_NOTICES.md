# Third-party and media notices

The root [MIT License](LICENSE) applies to the original MLCCS Video Search source code and project documentation authored for this repository.

It does not relicense third-party dependencies, models, libraries, tools, or binaries. Those components remain governed by their respective upstream licenses and notices. Exact dependency and model provenance is recorded in:

- `worker/manifests/dependencies.lock.json`
- `worker/manifests/models.lock.json`
- `worker/manifests/model-sources.json`
- `MODELS_AND_LICENSES.md`

The vendored `worker/vendor/gputil-1.4.0-py3-none-any.whl` remains subject to its upstream license. FFmpeg, libVLC, Python packages, model files, and other runtime components downloaded or assembled for a binary release must be distributed with all notices, source offers, linking conditions, and other obligations required by their own licenses. In particular, the applicable FFmpeg obligations depend on the exact build configuration and whether GPL components are enabled.

The sample video under `samples/` and acceptance screenshots under `evidence/` are demonstration and evidentiary media. They are not licensed for reuse under the MIT License unless a separate notice explicitly says otherwise.

Nothing in the root MIT License grants rights to third-party trademarks, model weights, datasets, or media beyond the rights granted by their respective owners.
