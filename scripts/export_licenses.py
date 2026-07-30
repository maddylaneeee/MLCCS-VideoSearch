from __future__ import annotations

import csv
import importlib.metadata
import sys


writer = csv.writer(sys.stdout, lineterminator="\n")
writer.writerow(("name", "version", "license"))
for distribution in sorted(importlib.metadata.distributions(), key=lambda item: (item.metadata.get("Name") or "").lower()):
    metadata = distribution.metadata
    license_value = metadata.get("License-Expression") or metadata.get("License") or "SEE-PACKAGE-METADATA"
    writer.writerow((metadata.get("Name") or "UNKNOWN", distribution.version, license_value))
