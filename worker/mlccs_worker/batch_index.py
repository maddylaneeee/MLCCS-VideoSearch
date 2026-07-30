from __future__ import annotations

import argparse
from datetime import UTC, datetime
import hashlib
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
from queue import Empty, Queue
import sqlite3
import time
from typing import Any
from uuid import uuid4


MEDIA_EXTENSIONS = {".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".webm"}
MODEL_NAME = "xlm-roberta-base-ViT-B-32"
MODEL_VERSION = "openclip-standard-506d40eb"


def _utc() -> str:
    return datetime.now(UTC).isoformat()


def _storage_type(path: Path) -> str:
    value = str(path)
    if value.startswith("\\\\"):
        return "remote"
    if os.name != "nt":
        return "local"
    try:
        import ctypes
        root = Path(value).anchor
        # DRIVE_REMOTE = 4. Unknown/removable media use the conservative local policy.
        return "remote" if ctypes.windll.kernel32.GetDriveTypeW(root) == 4 else "local"
    except (AttributeError, OSError):
        return "local"


def _atomic_json(path: Path, value: dict[str, Any]) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    for attempt in range(30):
        try:
            os.replace(temporary, path)
            return
        except PermissionError:
            if attempt == 29:
                raise
            time.sleep(0.1)


def _media_info(path: Path) -> tuple[float, int | None, int | None, str | None]:
    import av
    with av.open(str(path), metadata_errors="ignore") as container:
        stream = next((item for item in container.streams if item.type == "video"), None)
        if stream is None:
            return 0.0, None, None, None
        if stream.duration is not None and stream.time_base is not None:
            duration = float(stream.duration * stream.time_base)
        else:
            duration = float(container.duration or 0) / 1_000_000
        codec = getattr(getattr(stream, "codec_context", None), "name", None)
        return duration, int(stream.width or 0) or None, int(stream.height or 0) or None, codec


def _database(path: Path) -> sqlite3.Connection:
    connection = sqlite3.connect(path)
    connection.execute("PRAGMA journal_mode=WAL")
    connection.execute("PRAGMA synchronous=NORMAL")
    connection.executescript("""
        CREATE TABLE IF NOT EXISTS real_index_runs(
          id TEXT PRIMARY KEY, library_root TEXT NOT NULL, started_utc TEXT NOT NULL,
          completed_utc TEXT, status TEXT NOT NULL, device TEXT NOT NULL,
          model_version TEXT NOT NULL, files_total INTEGER NOT NULL, files_completed INTEGER NOT NULL DEFAULT 0,
          frames_indexed INTEGER NOT NULL DEFAULT 0, error TEXT
        );
        CREATE TABLE IF NOT EXISTS real_visual_frames(
          id TEXT PRIMARY KEY, run_id TEXT NOT NULL, media_path TEXT NOT NULL,
          timestamp_ms INTEGER NOT NULL, embedding_f16 BLOB NOT NULL, thumbnail_path TEXT NOT NULL,
          model_version TEXT NOT NULL, device TEXT NOT NULL,
          UNIQUE(media_path,timestamp_ms,model_version)
        );
        CREATE INDEX IF NOT EXISTS ix_real_visual_media_time ON real_visual_frames(media_path,timestamp_ms);
        CREATE TABLE IF NOT EXISTS media_assets(
          media_path TEXT PRIMARY KEY, library_root TEXT NOT NULL, name TEXT NOT NULL,
          extension TEXT NOT NULL, size_bytes INTEGER NOT NULL, modified_utc TEXT NOT NULL,
          duration_ms INTEGER NOT NULL, width INTEGER, height INTEGER, codec TEXT,
          status TEXT NOT NULL, thumbnail_path TEXT, error TEXT, last_indexed_utc TEXT,
          visual_version TEXT, speech_version TEXT, ocr_version TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_media_assets_library ON media_assets(library_root,name);
        CREATE TABLE IF NOT EXISTS transcript_segments_live(
          id TEXT PRIMARY KEY, media_path TEXT NOT NULL, start_ms INTEGER NOT NULL,
          end_ms INTEGER NOT NULL, text TEXT NOT NULL, normalized_text TEXT NOT NULL,
          words_json TEXT NOT NULL, model_version TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_transcript_live_media_time ON transcript_segments_live(media_path,start_ms);
        CREATE TABLE IF NOT EXISTS ocr_observations_live(
          id TEXT PRIMARY KEY, media_path TEXT NOT NULL, timestamp_ms INTEGER NOT NULL,
          text TEXT NOT NULL, normalized_text TEXT NOT NULL, confidence REAL NOT NULL,
          boxes_json TEXT NOT NULL, model_version TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_ocr_live_media_time ON ocr_observations_live(media_path,timestamp_ms);
    """)
    existing_columns = {row[1] for row in connection.execute("PRAGMA table_info(media_assets)")}
    for column in ("visual_version", "speech_version", "ocr_version"):
        if column not in existing_columns:
            connection.execute(f"ALTER TABLE media_assets ADD COLUMN {column} TEXT")
    connection.commit()
    return connection


def run(library: Path, data_root: Path, models_root: Path, interval_seconds: float, batch_size: int,
        decoder_workers: int, extra_models_root: Path, speech_enabled: bool, speech_model: str,
        ocr_enabled: bool, resource_policy: str) -> None:
    import av
    import cv2
    import numpy as np
    import open_clip
    import psutil
    import torch

    data_root.mkdir(parents=True, exist_ok=True)
    status_path = data_root / "real-index-status.json"
    pause_path = data_root / "real-index.pause"
    cancel_path = data_root / "real-index.cancel"
    checkpoint = models_root / "openclip-standard" / "open_clip_pytorch_model.bin"
    if not library.is_dir():
        raise RuntimeError(f"媒体库不可用: {library}")
    if not checkpoint.is_file():
        raise RuntimeError(f"锁定视觉模型尚未安装: {checkpoint}")
    lock_path = data_root / "real-index.lock"
    try:
        lock_handle = lock_path.open("x", encoding="utf-8")
    except FileExistsError:
        try:
            owner = int(lock_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            owner = 0
        if owner and psutil.pid_exists(owner):
            raise RuntimeError(f"真实索引已经由进程 {owner} 运行")
        lock_path.unlink(missing_ok=True)
        lock_handle = lock_path.open("x", encoding="utf-8")
    lock_handle.write(str(os.getpid()))
    lock_handle.flush()
    try:
        psutil.Process().nice(psutil.HIGH_PRIORITY_CLASS)
    except (psutil.AccessDenied, AttributeError):
        pass
    torch.set_num_threads(max(1, os.cpu_count() or 1))
    cuda_available = bool(torch.cuda.is_available())
    if cuda_available:
        torch.backends.cudnn.benchmark = True
    device = "cuda:0" if cuda_available else "cpu"
    gpu_name = torch.cuda.get_device_name(0) if cuda_available else "CPU"
    vram_gib = torch.cuda.get_device_properties(0).total_memory / (1024 ** 3) if cuda_available else 0
    logical_processors = max(1, os.cpu_count() or 1)
    available_memory = int(psutil.virtual_memory().available)
    storage_type = _storage_type(library)
    if decoder_workers <= 0:
        if resource_policy == "efficiency":
            desired_workers = max(1, logical_processors // 6)
        elif resource_policy == "balanced":
            desired_workers = max(2, logical_processors // 3)
        elif storage_type == "remote":
            # Random video seeks are latency-bound on SMB/network drives. More
            # outstanding files eventually reduce throughput through seek contention.
            desired_workers = max(2, logical_processors // 2)
        else:
            # Leave a small scheduling reserve for the GPU feeder/UI while using
            # the remaining logical processors to feed local-storage decode.
            scheduling_reserve = max(2, logical_processors // 6)
            desired_workers = max(4, logical_processors - scheduling_reserve)
        memory_limited_workers = max(1, available_memory // (512 * 1024 * 1024))
        decoder_workers = min(16, desired_workers, memory_limited_workers)
    model, _, _ = open_clip.create_model_and_transforms(
        MODEL_NAME, pretrained=str(checkpoint), device=device)
    if cuda_available:
        model.half()
    model.eval()
    if batch_size <= 0:
        if cuda_available:
            free_vram, _ = torch.cuda.mem_get_info()
            # Size the initial batch from live free VRAM, then halve automatically on an actual OOM.
            batch_budget = int(free_vram * (0.35 if resource_policy == "efficiency" else 0.50))
            estimated_bytes_per_image = 8 * 1024 * 1024
            estimated = max(8, min(128, batch_budget // estimated_bytes_per_image))
            batch_size = 2 ** max(3, int(estimated).bit_length() - 1)
        else:
            batch_size = max(2, min(16, logical_processors // 2))

    files = sorted(path for path in library.rglob("*") if path.is_file() and path.suffix.lower() in MEDIA_EXTENSIONS)
    media_info = {str(path): _media_info(path) for path in files}
    durations = {path: info[0] for path, info in media_info.items()}
    total_seconds = sum(durations.values())
    run_id = str(uuid4())
    library_key = hashlib.sha256(str(library).encode("utf-8")).hexdigest()[:16]
    thumbnails = data_root / "thumbnails" / library_key
    thumbnails.mkdir(parents=True, exist_ok=True)
    db = _database(data_root / "catalog.db")
    db.execute("INSERT INTO real_index_runs(id,library_root,started_utc,status,device,model_version,files_total) VALUES(?,?,?,?,?,?,?)",
               (run_id, str(library), _utc(), "Running", f"{device} / {gpu_name}", MODEL_VERSION, len(files)))
    for media_path in files:
        stat = media_path.stat()
        modified_utc = datetime.fromtimestamp(stat.st_mtime, UTC).isoformat()
        existing = db.execute(
            """SELECT size_bytes,modified_utc,status,thumbnail_path,last_indexed_utc,
                      visual_version,speech_version,ocr_version
               FROM media_assets WHERE media_path=?""", (str(media_path),)
        ).fetchone()
        changed = existing is None or int(existing[0]) != stat.st_size or existing[1] != modified_utc
        if changed and existing is not None:
            db.execute("DELETE FROM real_visual_frames WHERE media_path=?", (str(media_path),))
            db.execute("DELETE FROM transcript_segments_live WHERE media_path=?", (str(media_path),))
            db.execute("DELETE FROM ocr_observations_live WHERE media_path=?", (str(media_path),))
        status_value = "Queued" if changed else existing[2]
        thumbnail_value = None if changed else existing[3]
        indexed_value = None if changed else existing[4]
        visual_version = None if changed else existing[5]
        speech_version = None if changed else existing[6]
        ocr_version = None if changed else existing[7]
        db.execute("""
            INSERT INTO media_assets(media_path,library_root,name,extension,size_bytes,modified_utc,duration_ms,
                                     width,height,codec,status,thumbnail_path,error,last_indexed_utc,
                                     visual_version,speech_version,ocr_version)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(media_path) DO UPDATE SET
              library_root=excluded.library_root,name=excluded.name,extension=excluded.extension,
              size_bytes=excluded.size_bytes,modified_utc=excluded.modified_utc,
              duration_ms=excluded.duration_ms,width=excluded.width,height=excluded.height,codec=excluded.codec,
              status=excluded.status,thumbnail_path=excluded.thumbnail_path,error=NULL,
              last_indexed_utc=excluded.last_indexed_utc,visual_version=excluded.visual_version,
              speech_version=excluded.speech_version,ocr_version=excluded.ocr_version
        """, (str(media_path), str(library), media_path.name, media_path.suffix.lower(), stat.st_size,
              modified_utc, round(durations[str(media_path)] * 1000),
              media_info[str(media_path)][1], media_info[str(media_path)][2], media_info[str(media_path)][3],
              status_value, thumbnail_value, None, indexed_value,
              visual_version, speech_version, ocr_version))
    existing_paths = {str(path) for path in files}
    for (known_path,) in db.execute("SELECT media_path FROM media_assets WHERE library_root=?", (str(library),)).fetchall():
        if known_path not in existing_paths:
            db.execute("UPDATE media_assets SET status='Missing' WHERE media_path=?", (known_path,))
    db.commit()
    started = time.monotonic()
    started_utc = _utc()
    initial_frames = int(db.execute("SELECT COUNT(*) FROM real_visual_frames WHERE model_version=?", (MODEL_VERSION,)).fetchone()[0])
    frames_indexed = initial_frames
    resumes: dict[str, float] = {}
    processed: dict[str, float] = {}
    completed_files = 0
    errors: list[str] = []
    last_status_write = 0.0
    for media_path in files:
        previous = db.execute("SELECT MAX(timestamp_ms) FROM real_visual_frames WHERE media_path=? AND model_version=?",
                              (str(media_path), MODEL_VERSION)).fetchone()[0]
        resume = (int(previous) / 1000.0 + interval_seconds) if previous is not None else 0.0
        resumes[str(media_path)] = resume
        processed[str(media_path)] = min(durations[str(media_path)], resume)

    def status(stage: str, current: Path | None, error: str | None = None) -> None:
        nonlocal last_status_write
        elapsed = max(0.001, time.monotonic() - started)
        processed_seconds = sum(processed.values())
        progress = processed_seconds / total_seconds if total_seconds else 0.0
        _atomic_json(status_path, {
            "truthful": True, "runId": run_id, "status": stage, "startedUtc": started_utc,
            "library": str(library), "currentFile": str(current) if current else None,
            "filesTotal": len(files), "framesIndexed": frames_indexed,
            "mediaSecondsProcessed": round(processed_seconds, 3), "mediaSecondsTotal": round(total_seconds, 3),
            "progress": min(1.0, progress), "framesPerSecond": round((frames_indexed - initial_frames) / elapsed, 3),
            "device": device, "gpu": gpu_name, "cuda": str(torch.version.cuda) if cuda_available else None,
            "cpuThreads": torch.get_num_threads(), "batchSize": batch_size, "decoderWorkers": decoder_workers,
            "resourcePolicy": resource_policy, "samplingMode": "parallel-seek",
            "storageType": storage_type,
            "filesCompleted": completed_files,
            "filesFailed": len(errors),
            "etaSeconds": round((total_seconds - processed_seconds) / max(0.001, processed_seconds / elapsed), 1)
                if processed_seconds > 0 and processed_seconds < total_seconds else 0,
            "model": MODEL_NAME, "modelVersion": MODEL_VERSION, "error": error, "updatedUtc": _utc()
        })
        last_status_write = time.monotonic()

    def wait_if_paused() -> None:
        while pause_path.exists():
            if cancel_path.exists():
                return
            time.sleep(0.25)

    status("LoadingModel", None)
    try:
        work: Queue[tuple[str, Path, int, Any | None, str | None]] = Queue(
            maxsize=max(16, batch_size * 4))

        def decode_media(media_path: Path) -> None:
            duration = durations[str(media_path)]
            next_sample = resumes[str(media_path)]
            try:
                if next_sample >= max(0.0, duration - interval_seconds):
                    work.put(("done", media_path, round(duration * 1000), None, None))
                    return
                media_key = hashlib.sha256(str(media_path).encode("utf-8")).hexdigest()[:16]
                media_thumbnails = thumbnails / media_key
                media_thumbnails.mkdir(parents=True, exist_ok=True)
                last_timestamp_ms = -1
                clip_mean = np.asarray((0.48145466, 0.4578275, 0.40821073), dtype=np.float32)
                clip_std = np.asarray((0.26862954, 0.26130258, 0.27577711), dtype=np.float32)

                def emit(frame: Any, timestamp: float) -> None:
                    nonlocal last_timestamp_ms
                    timestamp_ms = round(timestamp * 1000)
                    if timestamp_ms <= last_timestamp_ms:
                        return
                    rgb = frame.to_ndarray(format="rgb24")
                    height, width = rgb.shape[:2]
                    scale = 224.0 / max(1, min(height, width))
                    resized_width = max(224, round(width * scale))
                    resized_height = max(224, round(height * scale))
                    interpolation = cv2.INTER_AREA if scale < 1.0 else cv2.INTER_CUBIC
                    resized = cv2.resize(rgb, (resized_width, resized_height),
                                         interpolation=interpolation)
                    left = max(0, (resized_width - 224) // 2)
                    top = max(0, (resized_height - 224) // 2)
                    crop = resized[top:top + 224, left:left + 224].astype(np.float32)
                    crop = (crop / 255.0 - clip_mean) / clip_std
                    tensor = torch.from_numpy(np.ascontiguousarray(crop)).permute(2, 0, 1)
                    preview = cv2.resize(rgb, (320, 180), interpolation=cv2.INTER_AREA)
                    thumb = media_thumbnails / f"{timestamp_ms:012d}.jpg"
                    cv2.imwrite(str(thumb), cv2.cvtColor(preview, cv2.COLOR_RGB2BGR),
                                [cv2.IMWRITE_JPEG_QUALITY, 78])
                    last_timestamp_ms = timestamp_ms
                    work.put(("frame", media_path, timestamp_ms, (tensor, str(thumb)), None))

                def sequential_from(start_at: float) -> None:
                    target = start_at
                    with av.open(str(media_path), metadata_errors="ignore") as container:
                        stream = next((item for item in container.streams if item.type == "video"), None)
                        if stream is None:
                            return
                        stream.thread_type = "AUTO"
                        stream.thread_count = max(1, logical_processors // decoder_workers)
                        if target > 0:
                            container.seek(round(max(0.0, target - 1.0) * 1_000_000),
                                           backward=True, any_frame=False, stream=None)
                        for frame in container.decode(stream):
                            if cancel_path.exists():
                                return
                            wait_if_paused()
                            timestamp = float((frame.pts or 0) * frame.time_base)
                            if timestamp + 0.001 < target:
                                continue
                            emit(frame, timestamp)
                            while target <= timestamp:
                                target += interval_seconds

                if interval_seconds >= 2.0 and duration >= interval_seconds * 4:
                    try:
                        with av.open(str(media_path), metadata_errors="ignore") as container:
                            stream = next((item for item in container.streams if item.type == "video"), None)
                            if stream is None:
                                work.put(("done", media_path, round(duration * 1000), None, None))
                                return
                            stream.thread_type = "AUTO"
                            stream.thread_count = max(1, logical_processors // decoder_workers)
                            while next_sample < duration:
                                if cancel_path.exists():
                                    work.put(("cancelled", media_path, 0, None, None)); return
                                wait_if_paused()
                                if cancel_path.exists():
                                    work.put(("cancelled", media_path, 0, None, None)); return
                                container.seek(round(max(0.0, next_sample - 1.0) * 1_000_000),
                                               backward=True, any_frame=False, stream=None)
                                selected = None
                                selected_timestamp = 0.0
                                for frame in container.decode(stream):
                                    timestamp = float((frame.pts or 0) * frame.time_base)
                                    if timestamp + 0.001 >= next_sample:
                                        selected = frame
                                        selected_timestamp = timestamp
                                        break
                                if selected is None:
                                    break
                                emit(selected, selected_timestamp)
                                next_sample += interval_seconds
                    except Exception:
                        # Damaged indices and uncommon containers may not support repeated seeking.
                        # Continue from the last successful target with the reliable sequential path.
                        sequential_from(next_sample)
                else:
                    sequential_from(next_sample)
                work.put(("done", media_path, round(duration * 1000), None, None))
            except Exception as error:
                work.put(("error", media_path, 0, None, str(error)))

        batch: list[tuple[Path, int, Any, str]] = []
        terminated_files = 0

        def flush() -> None:
            nonlocal frames_indexed, batch_size
            if not batch:
                return
            tensor = torch.stack([item[2] for item in batch])

            def encode(candidate: Any) -> Any:
                nonlocal batch_size
                cpu_candidate = candidate
                try:
                    if cuda_available:
                        device_candidate = cpu_candidate.pin_memory().to(device, non_blocking=True)
                        with torch.inference_mode(), torch.autocast("cuda", dtype=torch.float16):
                            return model.encode_image(device_candidate, normalize=True).to(
                                dtype=torch.float16).cpu().numpy()
                    with torch.inference_mode():
                        return model.encode_image(cpu_candidate.to(device), normalize=True).to(
                            dtype=torch.float16).cpu().numpy()
                except torch.OutOfMemoryError:
                    if len(cpu_candidate) <= 1:
                        raise
                    torch.cuda.empty_cache()
                    midpoint = len(cpu_candidate) // 2
                    batch_size = max(1, min(batch_size, midpoint))
                    return np.concatenate((
                        encode(cpu_candidate[:midpoint]),
                        encode(cpu_candidate[midpoint:])
                    ))

            vectors = encode(tensor)
            rows = []
            for (media_path, timestamp_ms, _, thumb), vector in zip(batch, vectors, strict=True):
                deterministic_id = hashlib.sha256(f"{media_path}|{timestamp_ms}|{MODEL_VERSION}".encode("utf-8")).hexdigest()
                rows.append((deterministic_id, run_id, str(media_path), timestamp_ms,
                             np.asarray(vector, dtype=np.float16).tobytes(), thumb, MODEL_VERSION, device))
            db.executemany("INSERT OR REPLACE INTO real_visual_frames VALUES(?,?,?,?,?,?,?,?)", rows)
            for media_path, _, _, _ in batch:
                first_thumb = next((row[5] for row in rows if row[2] == str(media_path)), None)
                db.execute("""
                    UPDATE media_assets SET status=CASE WHEN status='Indexed' THEN 'Indexed' ELSE 'Indexing' END,
                      thumbnail_path=COALESCE(thumbnail_path,?),last_indexed_utc=?
                    WHERE media_path=?
                """, (first_thumb, _utc(), str(media_path)))
            frames_indexed += len(rows)
            db.execute("UPDATE real_index_runs SET frames_indexed=? WHERE id=?", (frames_indexed, run_id))
            db.commit(); batch.clear()

        with ThreadPoolExecutor(max_workers=decoder_workers, thread_name_prefix="media-decode") as executor:
            futures = [executor.submit(decode_media, media_path) for media_path in files]
            while terminated_files < len(files):
                try:
                    kind, media_path, timestamp_ms, image, message = work.get(timeout=0.2)
                except Empty:
                    if batch:
                        flush()
                    status("Paused" if pause_path.exists() else "Indexing", None)
                    continue
                if kind == "frame" and image is not None:
                    processed[str(media_path)] = max(processed[str(media_path)], timestamp_ms / 1000.0)
                    tensor, thumb = image
                    batch.append((media_path, timestamp_ms, tensor, thumb))
                    if len(batch) >= batch_size:
                        flush(); status("Indexing", media_path)
                    elif time.monotonic() - last_status_write >= 1.0:
                        status("Indexing", media_path)
                else:
                    terminated_files += 1
                    if kind == "done":
                        completed_files += 1
                        processed[str(media_path)] = durations[str(media_path)]
                        db.execute("""UPDATE media_assets SET status='Indexed',error=NULL,last_indexed_utc=?,
                                      visual_version=? WHERE media_path=?""",
                                   (_utc(), MODEL_VERSION, str(media_path)))
                        db.execute("UPDATE real_index_runs SET files_completed=? WHERE id=?", (completed_files, run_id)); db.commit()
                    elif kind == "error":
                        errors.append(f"{media_path}: {message}")
                        db.execute("UPDATE media_assets SET status='Failed',error=? WHERE media_path=?",
                                   (message, str(media_path))); db.commit()
            flush()
            for future in futures:
                future.result()
        if cancel_path.exists():
            raise KeyboardInterrupt
        if cuda_available:
            torch.cuda.synchronize()
        if speech_enabled:
            if not cuda_available:
                raise RuntimeError("CAPABILITY_SPEECH_REQUIRES_CUDA: 语音索引需要受支持的 CUDA GPU")
            del model
            torch.cuda.empty_cache()
            status("LoadingSpeechModel", None)
            from faster_whisper import WhisperModel
            speech_root = extra_models_root / speech_model
            if not speech_root.is_dir():
                raise RuntimeError(f"锁定语音模型尚未安装: {speech_root}")
            compute_type = "int8_float16" if vram_gib < 10 else "float16"
            whisper = WhisperModel(str(speech_root), device="cuda", compute_type=compute_type,
                                   local_files_only=True)
            speech_files = [media_path for media_path in files if db.execute(
                "SELECT speech_version FROM media_assets WHERE media_path=?", (str(media_path),)
            ).fetchone()[0] != speech_model]
            for media_index, media_path in enumerate(speech_files, 1):
                if cancel_path.exists():
                    raise KeyboardInterrupt
                wait_if_paused()
                status("Speech", media_path)
                segments, _ = whisper.transcribe(str(media_path), vad_filter=True,
                                                 word_timestamps=True, beam_size=5)
                rows = []
                for segment in segments:
                    start_ms = round(segment.start * 1000)
                    end_ms = round(segment.end * 1000)
                    text = segment.text.strip()
                    if not text:
                        continue
                    segment_id = hashlib.sha256(
                        f"{media_path}|{start_ms}|{end_ms}|{speech_model}".encode("utf-8")
                    ).hexdigest()
                    words = [{"text": word.word, "startMs": round(word.start * 1000),
                              "endMs": round(word.end * 1000), "probability": word.probability}
                             for word in (segment.words or [])]
                    rows.append((segment_id, str(media_path), start_ms, end_ms, text,
                                 text.casefold(), json.dumps(words, ensure_ascii=False), speech_model))
                db.execute("DELETE FROM transcript_segments_live WHERE media_path=? AND model_version=?",
                           (str(media_path), speech_model))
                db.executemany("INSERT OR REPLACE INTO transcript_segments_live VALUES(?,?,?,?,?,?,?,?)", rows)
                db.execute("UPDATE media_assets SET speech_version=?,last_indexed_utc=? WHERE media_path=?",
                           (speech_model, _utc(), str(media_path)))
                db.commit()
            del whisper
            torch.cuda.empty_cache()
        if ocr_enabled:
            status("LoadingOcrModel", None)
            import inspect
            import paddleocr._common_args as paddleocr_common
            from paddlex.inference import PaddlePredictorOption as PaddleXOption

            # PaddleOCR 3.1 passes model_name positionally, while newer PaddleX exposes a
            # keyword-only constructor. Keep the locked runtime interoperable without
            # modifying third-party site-packages in place.
            if not any(parameter.kind is inspect.Parameter.POSITIONAL_OR_KEYWORD
                       for parameter in inspect.signature(PaddleXOption).parameters.values()):
                def compatible_predictor_option(_model_name: str | None = None, **kwargs: Any) -> Any:
                    return PaddleXOption(**kwargs)
                paddleocr_common.PaddlePredictorOption = compatible_predictor_option
            from paddleocr import PaddleOCR
            detection_root = extra_models_root / "ppocrv5-mobile-det"
            recognition_root = extra_models_root / "ppocrv5-mobile-rec"
            if not detection_root.is_dir() or not recognition_root.is_dir():
                raise RuntimeError("锁定 OCR 检测或识别模型尚未安装")
            ocr = PaddleOCR(
                text_detection_model_name="PP-OCRv5_mobile_det",
                text_detection_model_dir=str(detection_root),
                text_recognition_model_name="PP-OCRv5_mobile_rec",
                text_recognition_model_dir=str(recognition_root),
                text_recognition_batch_size=max(4, min(32, logical_processors * 2)),
                use_doc_orientation_classify=False, use_doc_unwarping=False,
                use_textline_orientation=False, lang="ch"
            )
            ocr_version = "ppocrv5-mobile"
            ocr_batch_size = max(4, min(32, logical_processors * 2))
            ocr_stride = max(1, round(max(8.0, interval_seconds * 2) / interval_seconds))
            ocr_files = [media_path for media_path in files if db.execute(
                "SELECT ocr_version FROM media_assets WHERE media_path=?", (str(media_path),)
            ).fetchone()[0] != ocr_version]
            for media_path in ocr_files:
                db.execute("DELETE FROM ocr_observations_live WHERE media_path=?", (str(media_path),))
                frame_rows = db.execute(
                    """SELECT timestamp_ms,thumbnail_path FROM real_visual_frames
                       WHERE media_path=? AND model_version=? ORDER BY timestamp_ms""",
                    (str(media_path), MODEL_VERSION)
                ).fetchall()[::ocr_stride]
                for offset in range(0, len(frame_rows), ocr_batch_size):
                    if cancel_path.exists():
                        raise KeyboardInterrupt
                    wait_if_paused()
                    status("OCR", media_path)
                    chunk = frame_rows[offset:offset + ocr_batch_size]
                    predictions = list(ocr.predict([str(row[1]) for row in chunk]))
                    observations = []
                    for (timestamp_ms, _), prediction in zip(chunk, predictions, strict=True):
                        value = prediction.json
                        if callable(value):
                            value = value()
                        if isinstance(value, str):
                            value = json.loads(value)
                        result = value.get("res", value) if isinstance(value, dict) else {}
                        texts = result.get("rec_texts", [])
                        scores = result.get("rec_scores", [])
                        boxes = result.get("rec_boxes", [])
                        for text_index, text in enumerate(texts):
                            normalized = str(text).strip()
                            if not normalized:
                                continue
                            confidence = float(scores[text_index]) if text_index < len(scores) else 0.0
                            box = boxes[text_index].tolist() if text_index < len(boxes) and hasattr(
                                boxes[text_index], "tolist") else (
                                boxes[text_index] if text_index < len(boxes) else [])
                            observation_id = hashlib.sha256(
                                f"{media_path}|{timestamp_ms}|{text_index}|{normalized}".encode("utf-8")
                            ).hexdigest()
                            observations.append((observation_id, str(media_path), int(timestamp_ms), normalized,
                                                 normalized.casefold(), confidence,
                                                 json.dumps(box, ensure_ascii=False), ocr_version))
                    if observations:
                        db.executemany("INSERT OR REPLACE INTO ocr_observations_live VALUES(?,?,?,?,?,?,?,?)",
                                       observations)
                    db.commit()
                db.execute("UPDATE media_assets SET ocr_version=?,last_indexed_utc=? WHERE media_path=?",
                           (ocr_version, _utc(), str(media_path)))
                db.commit()
        db.execute("UPDATE real_index_runs SET status='Completed',completed_utc=? WHERE id=?", (_utc(), run_id))
        db.commit()
        warning = (f"{len(errors)} 个媒体文件无法处理：" + "; ".join(errors[:10])) if errors else None
        status("Completed", None, warning)
    except KeyboardInterrupt:
        db.execute("UPDATE real_index_runs SET status='Cancelled',completed_utc=? WHERE id=?", (_utc(), run_id))
        db.commit(); status("Cancelled", None)
    except Exception as error:
        db.execute("UPDATE real_index_runs SET status='Failed',completed_utc=?,error=? WHERE id=?", (_utc(), str(error), run_id))
        db.commit(); status("Failed", None, error=str(error))
        raise
    finally:
        db.close()
        lock_handle.close()
        lock_path.unlink(missing_ok=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="MLCCS Video Search 真实 CUDA 视觉索引器")
    parser.add_argument("--library", type=Path, required=True)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--models-root", type=Path, required=True)
    parser.add_argument("--interval-seconds", type=float, default=4.0)
    parser.add_argument("--batch-size", type=int, default=0, help="0 表示按显存自动选择")
    parser.add_argument("--decoder-workers", type=int, default=0, help="0 表示按逻辑处理器数量自动选择")
    parser.add_argument("--extra-models-root", type=Path)
    parser.add_argument("--speech-enabled", action="store_true")
    parser.add_argument("--speech-model", default="whisper-medium")
    parser.add_argument("--ocr-enabled", action="store_true")
    parser.add_argument("--resource-policy", default="adaptive-full",
                        choices=("efficiency", "balanced", "adaptive-full"))
    args = parser.parse_args()
    run(args.library, args.data_root, args.models_root, args.interval_seconds, args.batch_size,
        args.decoder_workers, args.extra_models_root or args.models_root, args.speech_enabled,
        args.speech_model, args.ocr_enabled, args.resource_policy)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
