from __future__ import annotations

from dataclasses import asdict
from pathlib import Path
from typing import Any

from .capabilities import detect, recommend_whisper, require_speech
from .contracts import Envelope, WorkerError
from .media import probe


class WorkerPipeline:
    def __init__(self, data_root: Path, tools_root: Path, models_root: Path) -> None:
        self.data_root = data_root
        self.tools_root = tools_root
        self.models_root = models_root

    def dispatch(self, request: Envelope) -> Envelope:
        try:
            payload = request.payload or {}
            result = self._execute(request.kind, payload)
            return Envelope(request.protocolVersion, request.requestId, request.taskId, request.timestampUtc,
                            f"{request.kind}.response", "completed", 1.0, True, payload=result)
        except WorkerError as error:
            return Envelope(request.protocolVersion, request.requestId, request.taskId, request.timestampUtc,
                            "error", "failed", 0.0, error.recoverable,
                            error={"code": error.code, "summary": error.summary})

    def _execute(self, kind: str, payload: dict[str, Any]) -> dict[str, Any]:
        if kind == "worker.health":
            return {"workerVersion": "0.1.0", "protocolVersion": "1.0"}
        if kind == "hardware.detect":
            capability = detect(str(self.data_root))
            return {**capability.json(), "whisperRecommendation": recommend_whisper(capability.vram_bytes)}
        if kind == "media.probe":
            return asdict(probe(Path(payload["path"]), self.tools_root / "ffprobe.exe"))
        if kind == "speech.index":
            capability = detect(str(self.data_root))
            require_speech(capability)
            return self._speech(Path(payload["path"]), payload)
        if kind == "visual.index":
            return self._visual(Path(payload["path"]), payload)
        if kind == "ocr.index":
            return self._ocr(Path(payload["path"]), payload)
        raise WorkerError("IPC_UNKNOWN_REQUEST", f"Unknown worker request {kind}.", False)

    def _speech(self, path: Path, payload: dict[str, Any]) -> dict[str, Any]:
        from faster_whisper import WhisperModel
        model_name = str(payload.get("model", "large-v3-turbo"))
        compute_type = str(payload.get("computeType", "float16"))
        model = WhisperModel(str(self.models_root / model_name), device="cuda", compute_type=compute_type, local_files_only=True)
        segments, info = model.transcribe(str(path), vad_filter=True, word_timestamps=True, beam_size=5)
        output = []
        for segment in segments:
            output.append({"id": segment.id, "startMs": round(segment.start * 1000), "endMs": round(segment.end * 1000),
                           "text": segment.text, "words": [{"text": w.word, "startMs": round(w.start * 1000),
                           "endMs": round(w.end * 1000), "probability": w.probability} for w in (segment.words or [])]})
        return {"language": info.language, "segments": output, "model": model_name, "computeType": compute_type}

    def _visual(self, path: Path, payload: dict[str, Any]) -> dict[str, Any]:
        import open_clip
        import torch
        model_name = str(payload.get("model", "xlm-roberta-base-ViT-B-32"))
        checkpoint = str(self.models_root / str(payload["checkpointFile"]))
        device = "cuda" if torch.cuda.is_available() else "cpu"
        model, _, preprocess = open_clip.create_model_and_transforms(model_name, pretrained=checkpoint, device=device)
        return {"model": model_name, "device": device, "status": "ready", "algorithmVersion": 1}

    def _ocr(self, path: Path, payload: dict[str, Any]) -> dict[str, Any]:
        from paddleocr import PaddleOCR
        engine = PaddleOCR(model_dir=str(self.models_root / str(payload.get("model", "pp-ocrv5-mobile"))),
                           use_doc_orientation_classify=False, use_doc_unwarping=False, use_textline_orientation=False)
        result = engine.predict(str(path))
        return {"observations": [item.json for item in result], "algorithmVersion": 1}

