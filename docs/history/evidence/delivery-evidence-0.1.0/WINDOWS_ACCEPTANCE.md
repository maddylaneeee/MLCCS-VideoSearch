# Windows centralized acceptance

Record every command, build/test log, screenshot, machine specification, Windows build, GPU/driver, start/end time and outcome. Use a fixed multilingual dataset containing Chinese/English names, proper nouns, homophones, audio-only, silent video and OCR subtitles. Do not perform repetitive button-by-button tests during implementation; run this centralized sequence after the overall compilation gate.

## A. Clean-machine portable start

- Expand the release in Windows Sandbox/new account/isolated directory without Python, Git, FFmpeg or Visual Studio.
- Complete first run and verify real-byte dependency/model progress, pause/resume/range continuation, cancellation and mirror retry.
- Simulate network loss, hash mismatch and insufficient disk; partial files must not become available.
- Confirm the installed release never resolves developer tools or system Python.

## B. Capability gates

In Sandbox/no CUDA, speech must be disabled in normal and Advanced UI, while filename/visual CPU and supported OCR work. On the CUDA host, confirm driver detection, CUDA priority and VRAM recommendation: <4 GiB tiny/base/small; 4–5.9 medium int8_float16; 6–9.9 large-v3-turbo int8_float16; ≥10 large-v3-turbo float16; ≥12 allows large-v3 high quality. Forced over-budget selection requires risk confirmation. OOM reduces batch with bounded retries or stops safely.

## C. Initial indexing and resilience

- Verify the hours/days warning before start.
- During first index, browse/open/details work; Search remains visible and disabled with reason.
- Check total %, stage, file, complete/fail count, throughput and ETA against database counts.
- Close UI: Agent and Worker continue; tray pause/resume/safe exit/open UI all work.
- Crash/restart Agent and Worker, reboot Windows, disconnect/reconnect a library and confirm checkpoint recovery without duplicates.
- Run a 30-minute high-load observation and a 72-hour background endurance test. Record CPU/GPU/RAM/VRAM/disk, temperatures, power transitions, throughput and errors.

## D. Index correctness

- Query SQLite/Qdrant sample assets to prove multiple 2–8 second visual windows, threshold 27 behavior, long-scene subdivision and representative frames.
- Verify Whisper original segment and word timestamps, 8–30 second semantic windows and hit mapping to originals.
- Verify OCR original/normalized text, confidence, time and boxes.
- Rescan add/change/move/delete/duplicate/corrupt/unsupported/offline/reparse-loop cases; no duplicate segment IDs or false deletion of offline media.

## E. Search metrics and interactions

For All and every single source, validate correct source isolation, thumbnail, filename, time, context, library and contribution explanation. Test built-in player seek, context, transcript, speed/fullscreen; system default open, containing folder, copy path and details. Expand grouped adjacent hits.

Compute and save Recall@10, MRR, proper-name phonetic Recall@10 off/on, warm p50/p95 and cold-start latency. Required: Recall@10 ≥ .80, MRR ≥ .65, phonetic improvement ≥ 20 percentage points, warm p95 ≤ 1.5 seconds.

## F. UI/accessibility

Using computer use or equivalent, save screenshots of first run, download, initial index, library grid/list/context/detail, search filters/results, player seek, settings, Advanced risk prompt, tray, crash feedback and update prompt. Test 100/150/200%, narrow/maximized, high contrast, keyboard and Narrator. Fail text clipping, overlap, missing scroll, broken focus order, unlabeled status/error or hover-only critical actions. Confirm Windows-native design, not macOS imitation.

## G. Diagnostics end to end

Create controlled UI/Agent/Worker crashes. Next start must identify each new crash, permit description/preview/local save/upload/refusal, and not repeat a refused report. Confirm redacted payload contains no media path, username, query, transcript, OCR or secret. Without explicit upload there is no full-report request. Validate begin/chunk/complete retries/hashes, Defender result and exact MLCCS path. Turn off Help Improve and confirm anonymous events stop. Verify 24-hour temporary and 180-day completed retention with controlled timestamps.

## H. Signed updater

In test channel verify detection without silent install/restart; Update now/Later/Skip; disabled automatic check; Range resume; altered manifest/ZIP rejection; disk/file-use error; safe UI/Agent/Worker shutdown; database compatibility; failed health probe rollback. Only after all pass may stable `latest.json` be published.

## I. Website deployment

After application acceptance, back up current plugin/routes/static update paths, deploy the minimal diagnostics upgrade, reload/restart, verify health and unrelated routes, perform a real upload to the documented path, then upload and verify versioned update artifacts. Publish `latest.json` last. Exercise rollback once.

## Completion evidence

Return final portable ZIP/source snapshot hashes, full acceptance report, raw logs/performance records/screenshots, defect/fix log, dependency/model/license inventory, website deploy/rollback packages, signed update publication record and known limits/unexecuted items. Any unexecuted 30-minute, 72-hour, CUDA, clean-machine, UI, production website or stable update item remains explicitly not passed.

