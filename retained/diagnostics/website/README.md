# Website diagnostics handoff

`source/videosearch_diagnostics_plugin.py` and `tests/test_videosearch_diagnostics_plugin.py` mirror the validated files in the independent lixinchen.ca checkout. `videosearch-diagnostics-upgrade.zip` is generated from the live `routes.json` by the `lixinchen-upgrade-zip` workflow and is the only deployable artifact.

This macOS stage does not deploy to production. Windows Codex backs up the current plugin/routes, deploys the upgrade only after application acceptance, validates real Defender/storage behavior, and uses `rollback/README.md` if necessary.

