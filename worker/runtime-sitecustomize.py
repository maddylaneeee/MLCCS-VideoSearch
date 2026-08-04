"""Add only the MLCCS worker package that belongs to this installation."""
from __future__ import annotations

from pathlib import Path
import sys


runtime_root = Path(__file__).resolve().parents[2]
candidates = [runtime_root.parent]
candidates.extend(parent / "current" / "worker" for parent in runtime_root.parents)
for candidate in candidates:
    if (candidate / "mlccs_worker" / "__init__.py").is_file():
        value = str(candidate)
        if value not in sys.path:
            sys.path.insert(0, value)
        break
