# Rollback

Before deployment, copy the active `videosearch_diagnostics_plugin.py` and `backend/config/routes.json` into a timestamped backup outside the package. To roll back, restore both files together through the current MLCCSDemo deployment workflow, reload/restart, and verify `/api/health` plus an unrelated existing route. Do not remove `D:\lixinchenca-videosearch\diagnostics`; it is retained independently under the documented policy.

