# UI specification

The UI is native WinUI 3 on Windows App SDK 2.3.1 and follows Windows 10/11 interaction patterns. It uses a standard title bar, Mica where supported, NavigationView, CommandBar, InfoBar, ContentDialog, ListView/GridView, Expander, Tooltip and TeachingTip. macOS logic influences only shallow navigation, stable commands and progressive disclosure; there are no macOS window buttons, menu styling, gestures or shortcuts.

## Top-level pages

- Search: persistent AutoSuggestBox, clear/query icon, source selector, recent searches, filters, sort, timing/result count and result explanations.
- Library: grid/compact list toggle, add/rescan commands, concise cards, right-click actions and a detailed in-app panel.
- Index jobs: total percent, stage, file, completed/failed counts, throughput, ETA, pause/resume/cancel and failure confirmation.
- Settings: General/Startup, Libraries, Index content, Models/Quality, Performance/Background, Storage/Cache, Search/Phonetic, Privacy, Update, Diagnostics and About.

Navigation is never deeper than two levels. Cards show 16:9 thumbnail, at most two filename lines, duration, format and status badge; path/codec/resolution remain in details. Single click previews/details, double click or explicit command opens the system default application. Context menu: details, default open, containing folder, reindex, exclude and copy path.

## First run

The fixed sequence is privacy → hardware → data/model locations → libraries → index content → model recommendation → verified downloads → initial-index summary. “Help MLCCS improve” defaults on for anonymous low-frequency events. Full-log consent always defaults false and is never persisted as a blanket consent.

Hardware language is “节省资源 / 推荐 / 更高质量”. Technical model names, quantization and batch size are under Advanced. No-CUDA speech is disabled with a reason. Over-VRAM Whisper selection on CUDA requires a ContentDialog describing OOM, system lag and task-failure risk.

Download UI reports total/current file, real bytes, total bytes, speed, ETA, retry and mirror; supports pause/resume/cancel. Index summary warns that work can take hours/days and survives window close.

## Player

The packaged libVLC backend supports accurate seek, surrounding context, highlighted hit, transcript/subtitles, volume, speed and full screen. Decode failure offers system-default open and does not mutate index state.

## Accessibility and keyboard

All controls have programmatic names; status, progress and error summaries use live regions where appropriate. Core flow is keyboard-complete with logical focus order. Suggested accelerators: Ctrl+L/F focus search, Ctrl+, settings, Ctrl+O default open, Ctrl+Shift+O containing folder, Space play/pause, arrows seek, Esc close panel. Never rely on hover alone.

Validate at 100/150/200%, narrow/maximized windows and high contrast. Text must wrap or scroll without overlap/truncation. Narrator must announce labels, selected source, task stage/percent and remediation.

