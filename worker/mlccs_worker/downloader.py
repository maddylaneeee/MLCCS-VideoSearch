from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import time

import requests

from .contracts import WorkerError


@dataclass(frozen=True)
class Artifact:
    id: str
    filename: str
    installPath: str
    size: int
    sha256: str
    primaryUrl: str
    fallbackUrls: list[str]


class DownloadManager:
    def __init__(self, destination: Path) -> None:
        self.destination = destination
        self.destination.mkdir(parents=True, exist_ok=True)

    def download(self, artifact: Artifact, progress: Callable[[int, int, float], None], cancelled: Callable[[], bool]) -> Path:
        final = self.destination / artifact.installPath
        final.parent.mkdir(parents=True, exist_ok=True)
        partial = final.with_suffix(final.suffix + ".partial")
        if final.exists() and self._verify(final, artifact):
            return final
        urls = [artifact.primaryUrl, *artifact.fallbackUrls]
        errors: list[str] = []
        for url in urls:
            try:
                self._download_url(url, partial, artifact, progress, cancelled)
                if not self._verify(partial, artifact):
                    raise WorkerError("MODEL_HASH_MISMATCH", f"Hash or size mismatch for {artifact.id}.")
                os.replace(partial, final)
                return final
            except (requests.RequestException, OSError, WorkerError) as error:
                errors.append(f"{url}: {error}")
        raise WorkerError("MODEL_DOWNLOAD_FAILED", "; ".join(errors))

    @staticmethod
    def _download_url(url: str, partial: Path, artifact: Artifact,
                      progress: Callable[[int, int, float], None], cancelled: Callable[[], bool]) -> None:
        downloaded = partial.stat().st_size if partial.exists() else 0
        headers = {"Range": f"bytes={downloaded}-"} if downloaded else {}
        started = time.monotonic()
        with requests.get(url, headers=headers, stream=True, timeout=(15, 60), allow_redirects=True) as response:
            response.raise_for_status()
            if downloaded and response.status_code != 206:
                downloaded = 0
            mode = "ab" if downloaded else "wb"
            with partial.open(mode) as handle:
                for chunk in response.iter_content(512 * 1024):
                    if cancelled():
                        raise WorkerError("DOWNLOAD_CANCELLED", "Download cancelled by user.")
                    if not chunk:
                        continue
                    handle.write(chunk)
                    downloaded += len(chunk)
                    progress(downloaded, artifact.size, downloaded / max(0.001, time.monotonic() - started))
                handle.flush()
                os.fsync(handle.fileno())

    @staticmethod
    def _verify(path: Path, artifact: Artifact) -> bool:
        if path.stat().st_size != artifact.size:
            return False
        digest = hashlib.sha256()
        with path.open("rb") as handle:
            for chunk in iter(lambda: handle.read(1024 * 1024), b""):
                digest.update(chunk)
        return digest.hexdigest() == artifact.sha256


def load_manifest(path: Path) -> list[Artifact]:
    document = json.loads(path.read_text(encoding="utf-8"))
    return [Artifact(**{key: item[key] for key in Artifact.__annotations__}) for item in document["artifacts"]]
