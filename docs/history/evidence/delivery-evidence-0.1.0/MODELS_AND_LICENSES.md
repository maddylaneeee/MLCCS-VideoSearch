# Models, dependencies, licenses and hashes

The authoritative machine-readable locks are:

- `worker/manifests/models.lock.json`: 15 model repositories pinned to immutable commits and 79 required files with byte size and SHA-256.
- `worker/manifests/dependencies.lock.json`: CPython 3.12.10 embed x64, get-pip and the fully resolved Windows CPython 3.12 wheel graph.
- `worker/requirements.hashed.txt`: every Python package version with `--hash=sha256`.
- `worker/vendor/gputil-1.4.0-py3-none-any.whl`: reproducible pure-Python wheel needed because upstream publishes only source.

No manifest uses a mutable `latest` revision. Download completion alone is insufficient; an artifact becomes available only after exact byte count and SHA-256 pass and the partial file is atomically renamed.

## Model families

| Purpose | Pinned family | Dimension | License |
|---|---|---:|---|
| Standard visual | `xlm-roberta-base-ViT-B-32 / laion5b_s13b_b90k` | 512 | MIT |
| High-quality visual | `xlm-roberta-large-ViT-H-14 / frozen_laion5b_s13b_b90k` | 1024 | MIT |
| Chinese text | BGE small/base/large zh v1.5 | 512 / 768 / 1024 | MIT |
| Speech | faster-whisper tiny/base/small/medium/large-v3-turbo/large-v3 CT2 | n/a | MIT |
| OCR | PP-OCRv5 mobile/server detection and recognition | n/a | Apache-2.0 |
| Vector storage | Qdrant Edge Python 0.6.0 | model-dependent | Apache-2.0 |

The model lock records each repository commit, every required file and total disk bytes. Changing an embedding model or dimension requires a new collection and a modality-specific rebuild; it must never silently reuse incompatible vectors.

## Major runtime licenses

CPython is PSF/Python-2.0. PyTorch, OpenCLIP, faster-whisper, BGE glue, Sentence Transformers and Qdrant Edge use their upstream open-source licenses recorded in wheel metadata and the lock. PaddleOCR/PaddlePaddle use Apache-2.0. FFmpeg build configuration and libVLC redistribution notices must be copied into the final `licenses` directory on Windows; select a compatible FFmpeg build and document whether LGPL or GPL components are enabled. Model cards and upstream license files are downloaded alongside models when part of the pinned runtime file set.

Before final distribution, Windows Codex must generate a third-party notices inventory from the installed private environment and resolve every `SEE-PACKAGE-METADATA` entry. Missing or incompatible licensing is a release blocker.

