from __future__ import annotations

import json
import struct
from typing import BinaryIO

from .contracts import Envelope, WorkerError


MAXIMUM_FRAME_BYTES = 1024 * 1024


def read_frame(handle: BinaryIO) -> Envelope:
    prefix = _read_exact(handle, 4)
    (length,) = struct.unpack("<I", prefix)
    if not 0 < length <= MAXIMUM_FRAME_BYTES:
        raise WorkerError("IPC_FRAME_TOO_LARGE", f"Rejected frame length {length}.", False)
    return Envelope.from_dict(json.loads(_read_exact(handle, length).decode("utf-8")))


def write_frame(handle: BinaryIO, envelope: Envelope) -> None:
    body = json.dumps(envelope.to_dict(), ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    if len(body) > MAXIMUM_FRAME_BYTES:
        raise WorkerError("IPC_FRAME_TOO_LARGE", f"Rejected frame length {len(body)}.", False)
    handle.write(struct.pack("<I", len(body)))
    handle.write(body)
    handle.flush()


def _read_exact(handle: BinaryIO, count: int) -> bytes:
    output = bytearray()
    while len(output) < count:
        chunk = handle.read(count - len(output))
        if not chunk:
            raise EOFError
        output.extend(chunk)
    return bytes(output)

