from __future__ import annotations

from dataclasses import asdict, dataclass, field
from datetime import UTC, datetime
from typing import Any
from uuid import UUID, uuid4


PROTOCOL_VERSION = "1.0"


class WorkerError(RuntimeError):
    def __init__(self, code: str, summary: str, recoverable: bool = True) -> None:
        super().__init__(summary)
        self.code = code
        self.summary = summary
        self.recoverable = recoverable


@dataclass(frozen=True)
class Envelope:
    protocolVersion: str
    requestId: str
    taskId: str | None
    timestampUtc: str
    kind: str
    stage: str
    progress: float
    recoverable: bool
    error: dict[str, str] | None = None
    payload: dict[str, Any] | None = None

    @classmethod
    def create(cls, kind: str, payload: dict[str, Any] | None = None, task_id: UUID | None = None) -> "Envelope":
        return cls(PROTOCOL_VERSION, str(uuid4()), str(task_id) if task_id else None,
                   datetime.now(UTC).isoformat(), kind, "idle", 0.0, True, payload=payload)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "Envelope":
        envelope = cls(**value)
        envelope.validate()
        return envelope

    def validate(self) -> None:
        if self.protocolVersion != PROTOCOL_VERSION:
            raise WorkerError("IPC_PROTOCOL_INCOMPATIBLE", "Worker and Agent protocol versions differ.", False)
        UUID(self.requestId)
        if self.taskId:
            UUID(self.taskId)
        if not self.kind or not self.stage or not 0 <= self.progress <= 1:
            raise WorkerError("IPC_INVALID_ENVELOPE", "IPC envelope is incomplete.", False)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

