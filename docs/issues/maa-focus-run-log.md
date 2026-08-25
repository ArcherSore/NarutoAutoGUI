---
title: 在 GUI 运行日志中展示 MaaNOP focus 日志
status: implemented-awaiting-interactive-validation
---

## Problem Statement

NarutoAutoGUI 当前的“运行日志”主要展示 GUI、Worker、PID、IPC、RDP 和 Agent 等诊断信息。
MaaNOP 在正常执行过程中已经通过 MaaFramework Pipeline 的 `focus` 声明了面向普通用户的任务日志，
但 Worker 没有订阅 `MaaTasker.Callback`，这些消息没有进入 Log Entry、Named Pipe 或 GUI。

普通用户需要看到 MaaNOP 作者明确选择的任务进度，例如“开始领取”“成功领取”和“无需领取”。
NarutoAutoGUI 是启动器，不是调试器；诊断信息应继续写入日志文件，但不得进入 GUI 运行日志列表。

现有 Log Entry 传输还有两个影响可见性的正确性问题：

- 日志 cursor 没有按 Worker Instance 隔离。Worker Instance 变化后，新 Worker 从较小 sequence 开始的日志
  可能被旧 cursor 当作重复项丢弃。
- 实时 `log.entry` 出现 sequence gap 时，GUI 会直接推进 cursor，没有通过 `log.getSince` 补取缺失项，
  导致缺失日志可能永久不可见。

## Solution

NarutoAutoWorker 以 `MaaTasker.Callback` 作为唯一接入 Seam，只投影 MaaNOP 明确声明的字符串 `focus`：

- 根据 Callback 的 `Message` 在 `Details` 根对象的 `focus` 字典中精确匹配。
- 只接受非空字符串模板，并使用 `Details` 根对象中的标量字段替换占位符。
- 将渲染结果写成现有 WorkerLogEntry，`Source` 固定为 `maanop.run`，`Level` 固定为 `INFO`。
- 继续复用 WorkerLogBuffer、`log.entry`、`log.getSince`、sequence、截断和 Run/Plan Item 关联字段。

GUI 运行日志列表只消费 `Source == "maanop.run"` 的 WorkerLogEntry。
GUI 自身和其他 Worker diagnostic 继续通过 AppLogger 或 MaaFramework 原生日志写入文件，但不进入运行日志列表。

WorkerCoordinator 将日志 cursor 与 Worker Instance 关联。
同一 Worker Instance 重连时保留 cursor；Worker Instance 变化时，在恢复日志前重置 cursor。
实时事件出现 sequence gap 时，不越过缺口推进 cursor，而是从最后连续 sequence 调用 `log.getSince`，
直到追平已观察到的最高 sequence。

## User Stories

1. As a NarutoAutoGUI user, I want to see MaaNOP-authored task messages,
   so that I understand what the automation is doing.
2. As a NarutoAutoGUI user, I want static Pipeline focus messages to appear,
   so that meaningful task progress is visible.
3. As a NarutoAutoGUI user, I want Agent-generated dynamic focus messages to appear,
   so that dynamic progress is not hidden.
4. As a NarutoAutoGUI user, I want focus placeholders to be rendered,
   so that I see readable text instead of template tokens.
5. As a NarutoAutoGUI user, I want run messages in sequence order, so that the execution timeline is understandable.
6. As a NarutoAutoGUI user, I want each run message at most once,
   so that reconnect and recovery do not create duplicates.
7. As a NarutoAutoGUI user, I want dropped realtime entries recovered automatically,
   so that transient backpressure does not hide progress.
8. As a NarutoAutoGUI user, I want logs from a new Worker Instance to appear normally,
   so that an old cursor cannot suppress them.
9. As a NarutoAutoGUI user, I want PID, IPC, RDP, HWND and job ID diagnostics excluded,
   so that the run log stays readable.
10. As a NarutoAutoGUI user, I want GUI and Worker diagnostics excluded regardless of level,
    so that INFO is not mistaken for user intent.
11. As a NarutoAutoGUI user, I want malformed Callback data to leave the Run unaffected,
    so that logging cannot stop automation.
12. As a MaaNOP author, I want existing focus declarations to control the GUI log,
    so that no launcher-specific logging contract is needed.
13. As a MaaNOP author, I want only the Callback message's matching focus key displayed,
    so that unrelated events add no noise.
14. As a maintainer, I want raw Callback traffic excluded from IPC,
    so that recognition and action events cannot flood the log channel.
15. As a maintainer, I want all diagnostics retained in files,
    so that removing them from the GUI does not reduce supportability.
16. As a maintainer, I want the current WorkerLogEntry protocol reused, so that there is no second logging protocol.
17. As a maintainer, I want Run State to remain authoritative,
    so that Callback text, stdout and stderr cannot change outcomes.
18. As a maintainer, I want recovery to cover all WorkerLogEntry sources,
    so that GUI filtering does not break sequence continuity.
19. As a maintainer, I want recovery not to block the Pipe read loop,
    so that the request response can still be consumed.
20. As a maintainer, I want the Child Session and RDP baseline unchanged,
    so that logging cannot destabilize launch or cleanup.

## Implementation Decisions

### MaaNOP focus ingestion

- `MaaTasker.Callback` is the only MaaNOP/MaaFramework log ingestion Seam.
- Subscribe after MaaTasker creation and before appending the Maa task.
- Unsubscribe within the same per-Run MaaTasker lifetime.
- The Callback Adapter must return quickly and catch every parsing or projection failure.
- A logging failure must not affect MaaJobStatus, Run State, cancellation or cleanup.
- The focus projection may be an internal pure Implementation.
- Do not introduce a public logging Interface solely for this feature.

### String focus projection

- Parse `Details` as a JSON object. A missing or non-object root produces no user-facing entry.
- Read the root `focus` property only when it is a JSON object.
- Look up the Callback `Message` as an exact, ordinal property name.
- A missing or mismatched key produces no user-facing entry.
- Accept only focus values that are non-empty JSON strings.
- Treat the accepted string as literal template text.
- Do not load files, fetch URLs, translate keys or interpret Markdown.
- Replace a placeholder only when it names a top-level `Details` string, number or boolean property.
- String replacements use their content; number and boolean replacements use invariant JSON scalar text.
- Unknown placeholders and placeholders targeting null, object or array values remain unchanged.
- A rendered result that is empty or whitespace-only produces no user-facing entry.
- Missing focus, unmatched messages and unsupported focus shapes are normal filtering outcomes
  and produce no diagnostic noise.
- Invalid JSON or an unexpected projection exception may write one diagnostic Warning at most once per Run.

### WorkerLogEntry output

- Every accepted focus Callback produces exactly one existing WorkerLogEntry.
- `Source` is exactly `maanop.run`.
- `Level` is exactly `INFO`.
- `Message` is the rendered focus text.
- Existing Worker logging supplies sequence, UTC timestamp, truncation metadata, RunId, PlanItemId and TaskName.
- Existing UTF-8 single-entry truncation and WorkerLogBuffer capacity limits remain authoritative.
- Callback names, focus text, Agent stdout and Agent stderr are never Run State evidence.

### Protocol constraints

- Do not add `Audience`, `Kind` or equivalent classification fields.
- Do not change protocol or snapshot versions.
- Do not add another event, request, cursor, buffer, store or logging protocol.
- Do not forward raw Callback `Message` or `Details` through IPC.
- Do not tail, parse, copy, merge or stream MaaFramework `maa.log`.

### GUI run-log routing

- GUI routing uses exact source equality with `maanop.run`.
- Prefix matching and severity-based classification are not permitted.
- Populate the GUI list from structured WorkerLogEntry values.
- Preserve the WorkerLogEntry timestamp when adding a visible row.
- AppLogger EntryWritten events must not populate the GUI run-log collection.
- Other Worker sources must not populate the GUI run-log collection.
- GUI and Worker diagnostics continue to use their existing file logging paths.
- Worker entries may continue to be mirrored to diagnostic files.
- Existing GUI list capacity, ordering, scrolling and visual severity behavior remain unchanged where possible.

### Worker Instance cursor

- Associate the last contiguous log sequence with a WorkerInstanceId.
- Reconnecting to the same Worker Instance retains the last contiguous sequence.
- Applying a different Worker Instance resets the cursor to zero before recovery.
- The reset must not alter Child Session admission or Worker identity validation.
- Recovery results for a Worker Instance that is no longer active must be ignored.

### Sequence gap recovery

- Sequence continuity covers every WorkerLogEntry source, including entries filtered out of the GUI.
- An entry at or below the last contiguous sequence is a duplicate and is not published.
- An entry at exactly the next sequence is published and advances the cursor.
- An entry above the next sequence records the highest observed sequence but is not published immediately.
- A gap event must not advance the cursor across the missing range.
- Schedule `log.getSince` from the last contiguous sequence.
- Recovery is single-flight for the active Worker Instance.
- The Pipe envelope read loop must remain able to consume recovery responses.
- Apply recovered entries in ascending sequence order and publish each entry at most once.
- Events received during recovery update the highest observed target.
- Continue or restart recovery until the cursor reaches the highest observed target.
- If `log.getSince` reports a true eviction gap, record the missing range in diagnostic files.
- For a true eviction gap, resume immediately before the first retained sequence and continue applying retained entries.
- Do not add an eviction warning to the GUI run log.
- Keep existing `log.getSince` request and response schemas.
- Keep recovery pages within existing Pipe frame and documented response-budget constraints.

### Baseline and documentation

- Do not modify Child Session creation, RDP ActiveX, WTS or Task Scheduler COM behavior.
- Do not modify Worker admission, identity validation, launch or cleanup behavior.
- Do not modify MaaNOP or MFAAvalonia.
- Update capability and status documentation after the implementation is verified.
- Update roadmap documentation only if direction or priority changes.

## Testing Decisions

- Tests assert observable behavior, not private helper names, JSON library calls, locks or field layout.
- The primary Worker test seam is one Callback input through the focus Adapter to zero or one emitted Worker log record.
- Cover a matching non-empty string focus producing one `INFO` entry from `maanop.run`.
- Cover exact Callback message matching.
- Cover missing focus, mismatched keys, empty strings and whitespace-only strings.
- Cover object focus and every other non-string value producing no user-facing entry.
- Cover string, number and boolean placeholder replacement.
- Cover unknown, null, object and array placeholders remaining unchanged.
- Cover Chinese text and Unicode without corruption.
- Cover malformed JSON without an exception escaping the Callback Adapter.
- Cover invalid-payload diagnostic rate limiting to at most once per Run.
- Cover RunId, PlanItemId and TaskName association through the existing log path.
- Cover oversized rendered text inheriting the existing UTF-8 truncation behavior.
- Cover concurrent accepted Callbacks producing unique, monotonically sequenced entries.
- The primary Coordinator test seam is Worker log events plus scripted `log.getSince` responses
  observed through LogReceived.
- Cover duplicate suppression and normal in-order advancement.
- Cover a sequence jump followed by ordered, exactly-once recovery.
- Cover several gap events during one recovery flight and eventual catch-up to the highest observed sequence.
- Cover an event arriving around the final recovery page so that it cannot be stranded.
- Cover a true buffer-eviction gap resuming from the first retained entry.
- Cover cancellation, disconnect and Worker Instance replacement during recovery.
- Cover same-instance reconnect retaining the cursor.
- Cover different-instance connection resetting the cursor to zero.
- Cover diagnostic entries advancing continuity without entering the GUI collection.
- The primary GUI test seam is a structured WorkerLogEntry and the resulting run-log collection.
- Cover `maanop.run` entering the GUI once with its original timestamp and message.
- Cover every other Worker source remaining absent from the GUI while still reaching diagnostic files.
- Cover AppLogger EntryWritten remaining absent from the GUI while still reaching diagnostic files.
- Use the existing automated self-test runner and test script as prior art where applicable.
- Add the smallest focused automated test surface needed for Worker projection without exposing a production Interface.
- Run a Release build and the existing automated test script.
- Perform one real MaaNOP Run containing a static string focus and an Agent-generated dynamic string focus.
- Verify both focus messages appear in the GUI and diagnostic lines do not.
- Verify diagnostics remain available in existing log files.
- Verify controlled event loss recovers focus entries without duplicates.
- Verify a replacement Worker Instance accepts its new sequence values.
- Reuse the established Child Session/RDP baseline and report interactive validation only when actually executed.
- Check every affected handwritten code line against the repository's 120-character limit.

## Out of Scope

- Any MaaFramework Callback source other than `MaaTasker.Callback`.
- Raw Callback forwarding or persistence.
- Object-form focus and `display` channels.
- Toast, notification, dialog or modal presentation.
- Markdown rendering or internationalization resolution.
- Loading focus content from files or URLs.
- A new log classification field or protocol.
- A second run-log event, request, cursor, buffer or store.
- A diagnostic-log view, toggle, filter panel or search UI.
- Showing AppLogger, Worker, Agent, IPC, PID, RDP, HWND or job ID diagnostics in the GUI run-log list.
- Tailing, parsing, copying, rotating or merging MaaFramework `maa.log`.
- Changing Run State from Callback, focus, stdout or stderr.
- Synthesizing user-facing lifecycle messages outside MaaNOP focus.
- Modifying MaaNOP resources, Agent code or MFAAvalonia.
- Modifying Child Session, RDP, WTS, Task Scheduler, admission, identity, launch or cleanup baselines.
- A durable per-Run user-log database or historical Run browser.
- General logging-framework refactoring.

## Further Notes

- Current MaaNOP v1.3.0 resources use string focus, including templates with top-level values such as `{name}`.
- Dynamic Agent messages already use Pipeline override focus,
  so the same Callback Seam covers static and dynamic messages.
- NarutoAutoGUI is a launcher, not a debugger. GUI “运行日志”只表示 MaaNOP user-facing Run Log。
- `maanop.run` is reserved as an exact source value for this behavior.
- Diagnostic persistence remains under existing ownership: AppLogger files for NarutoAutoGUI diagnostics
  and MaaFramework files for native diagnostics.
- A true ring-buffer eviction can make old entries irrecoverable.
  Record it in diagnostic files and resume from retained entries.
- Completion requires evidence for focus projection, GUI filtering, same-instance recovery,
  different-instance reset and unchanged baseline.
