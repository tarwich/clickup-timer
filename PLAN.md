# ClickUp Timer for Windows

Build a native Windows 11 app that logs time directly to ClickUp. The default display is a compact timer over the unused left portion of the existing taskbar, consuming no additional desktop space. A movable floating mode is also available.

Use C# with WPF on .NET 10. Deliver a self-contained Windows executable, source code, and setup instructions. No hosted backend or ClickUp plugin is required.

## Phase 1 — Prove taskbar visibility

This phase is the prerequisite for building the ClickUp integration.

- [x] Install the .NET 10 SDK and scaffold the native Windows application.
- [x] Build a borderless topmost positioning prototype within the existing taskbar height, initially in its unused left portion.
- [x] Reserve no additional strip at the top or bottom of the desktop.
- [x] Allow dragging and remember placement without covering Start, pinned apps, or system-tray controls.
- [x] Verify visibility beside an ordinary maximized window and recovery after an Explorer restart.
- [x] Test changing taskbar icon occupancy and automated placement geometry at 100–200% scaling, negative monitor origins, and stale off-screen positions.
- [x] Fix Start-menu disappearance and verify visibility by native hit-testing with Start open. Retain verified placement during shell accessibility interruptions, remove full-screen auto-hiding, and use taskbar window ownership.
- [ ] Complete manual taskbar-click checks and repeat Explorer-restart validation with the new ownership relationship.
- [ ] Test actual display-scale changes and monitor disconnect/reconnect; only one display was available during implementation.
- [x] User approved Phase 1 and authorized Phase 2 on 2026-09-10. Remaining hardware and manual checks are deferred; they no longer block Phase 2.

Acceptance: the timer remains visible during ordinary desktop work without shrinking the workspace or obstructing taskbar controls. Full-screen applications and Windows security screens may cover it.

Phase 1 is approved by the user. The unchecked verification items remain documented rather than being represented as completed tests. The current build is `artifacts/phase2/ClickUpTimer.exe`.

## Phase 2 — Application shell and secure setup

- [x] Separate UI, Windows positioning, timer coordination, ClickUp API access, local persistence, and credential storage.
- [x] Add first-run setup to accept and validate a personal ClickUp API key.
- [x] Store the API key in Windows Credential Manager and exclude it from logs and diagnostics.
- [x] Discover accessible workspaces and lists; let the user select a workspace and preferred list.
- [x] Persist the preferred list as the default destination for future task creation (creation remains Phase 4).
- [x] Persist settings and an account/list-scoped task cache locally; refresh the cache after saving setup.
- [x] Add settings for taskbar/floating mode, monitor, and optional launch at sign-in, initially off.
- [x] Implement movable floating mode and persist its position.
- [x] Verify 39 service/credential checks and 23 placement checks, plus live display-mode switching, floating dragging, and saved-position restoration.
- [x] Revise settings flow: auto-connect on key entry/reopening, preserve selectable choices during refresh, and apply display settings independently. Six additional WPF regression checks pass (45 Phase 2 checks total).
- [x] Verify live account setup: the installed update automatically reconnects using the user's saved key, explicitly indicates it is saved, and exposes three lists with both selectors enabled.
- [x] Add save/reopen regressions, including saving a validated key/workspace when list loading fails. All 50 Phase 2 checks pass; the updated build is installed and running.

- [x] Fix preferred-list refresh overwriting the current selection. Preserve saved and in-progress selections, retain missing selections with an explanation, and pass all 56 Phase 2 checks.
- [x] Scale preferred-list selection for large workspaces. Search all lists by multi-word Space / Folder / List path or list ID, pin the current selection, cap rendered results, and verify the picker with 5,000 lists.
- [x] Remove redundant per-Folder list requests. Consume the Lists embedded in ClickUp's Folder and shared-hierarchy responses and preserve complete nested Folder paths.

Acceptance: setup succeeds with a valid key, reports invalid credentials clearly, and restores configuration after restart without storing the key in plaintext application files.

## Phase 3 — Timer display and controls

- [x] Display the selected task name and a large `HH:MM:SS` current-session timer.
- [x] Display a smaller personal total for the selected task today.
- [x] Show explicit running/stopped text and icons alongside color; distinguish unconfirmed operations and connection failures.
- [x] Provide Start/Stop, task picker, settings, and an open-in-ClickUp action.
- [x] Open the task picker upward from taskbar mode.
- [x] Retain the completed session duration after Stop until the next Start or task selection.
- [ ] Keep controls readable and usable in both taskbar and floating modes at different display scales.

Acceptance: the user can clearly identify the selected task, elapsed session time, and whether time is running or stopped at a glance.

Phase 3 progress: installed a local preview with cached-task selection, explicit running/stopped text and icons, retained session duration, settings, and browser task links. Today shows an unavailable marker until Phase 5 supplies real totals; uncertain server operations also await Phase 5. Task switching stops the local preview. Search/MRU/creation remain Phase 4. Verified the taskbar UI and popup selection live; 61 service/UI/timer checks pass. Physical DPI/floating visual checks remain open.

## Phase 4 — Task selection, search, and creation

- [x] Show eight most recently selected tasks followed by preferred-list tasks, and persist MRU order across restarts.
- [x] Include everyone's accessible tasks and subtasks, not just the user's assignments.
- [x] Filter task names and IDs within the cached preferred list first.
- [x] Provide an explicit **Search workspace** action with paginated loading and progressive results.
- [x] Distinguish incomplete searches from searches with no matches.
- [x] Add ignored-status checkboxes in settings, grouped by list as needed; initially exclude done/closed statuses.
- [x] Apply status exclusions to search and MRU while keeping the actively timed task visible.
- [x] Offer **Create task in [preferred list]**, prefilled with the search text as the task title.
- [x] Create an unassigned task using the list's initial status, then select it using the normal task-switching behavior.
- [x] Preserve entered text and show a useful error when task creation fails.

Acceptance: the user can quickly return to recent tasks, search the preferred list before expanding the scope, exclude unwanted statuses, and create a task in the configured list.

User confirmed live task creation works. Phase 4 implemented: eight saved recent tasks, local-first filtering, explicit progressive workspace search, list-specific ignored statuses, and unassigned task creation using the entered search text. Verified local and workspace search and status discovery live. Creation requests and failure/duplicate-click behavior use fixtures; no live test tasks were created. All 77 application checks pass. Timing remains a local preview until Phase 5.

## Phase 5 — ClickUp timing and recovery

- [x] Start and stop time entries directly through ClickUp's API.
- [x] When switching while running, stop the old task before starting the new one. Switching while stopped stays stopped.
- [x] Stop on screen lock, sleep, or explicit Quit; require manual Start when the user returns.
- [x] Require connectivity to start, switch, or create tasks.
- [x] Serialize timer operations and prevent duplicate clicks.
- [x] Reconcile uncertain API responses before retrying writes.
- [x] Read ClickUp's running entry on startup, resume, before changes, and every 15 seconds; reflect changes made in ClickUp itself.
- [x] Update elapsed time each second from timestamps rather than accumulated UI ticks.
- [x] Calculate today's personal task total in the Windows local timezone without double-counting the running entry.
- [x] Persist requested stop timestamps before network calls.
- [x] Clearly mark unsuccessful stops as unconfirmed and reconcile the specific entry on reconnection.
- [x] Correct delayed stops to their recorded stop time where possible, never stop an unrelated replacement timer, and surface conflicting external edits for review.
- [x] Preserve recoverable state across crashes and respect API rate limits.

Acceptance: successful operations match ClickUp, task transfers do not overlap, and network failures remain visible and recoverable. Unexpected power loss cannot guarantee an immediate server-side stop.

Phase 5 implementation: server-backed entries, durable start/stop intent, entry-specific stop updates, serialized changes, 15-second reconciliation, local-day totals, and lock/sleep/quit cutoff capture. All 96 application checks pass, including uncertain responses, restart recovery, conflicting replacement timers, rapid clicks, and DST boundaries. Live read-only authentication/current-entry/history requests passed. Live Start/Stop and actual lock/sleep validation remain Phase 6 checks; no test time was written to the user's tasks. ClickUp provides no conditional-write transaction, so concurrent external edits between a read and write cannot be made atomic.

- [x] Fix external-stop reconciliation: accept the server duration, clear stale pending stops, and use the dedicated Stop endpoint after checking the expected running entry. All 98 application checks pass.
- [x] Reproduce and fix live Stop failure caused by missing task metadata on the singular-entry endpoint. Verify actual UI Start and Stop against the server, and keep Stop available while syncing. All 99 checks pass.
- [x] Final live UI verification passed twice: Start then Stop on Blah recorded 21.897 seconds and 11.637 seconds. Both sessions were confirmed stopped via the server, with matching local duration and no pending operation.
- [x] User confirmed Start and Stop work correctly; reviewed for commit.

## Phase 6 — Verification and delivery

- [ ] Test start/stop, rapid clicks, task transfers, and partial API failures.
- [ ] Test external timer changes, pending-stop recovery, restart recovery, and midnight totals.
- [ ] Test MRU persistence, paginated search, status exclusions, and preferred-list changes.
- [ ] Test invalid credentials and task-creation failures.
- [ ] Verify lock, sleep, and Quit stop timing and returning does not restart it.
- [ ] Repeat visibility acceptance checks with the completed UI in taskbar and floating modes.
- [ ] Package a self-contained Windows build matching this PC's architecture.
- [ ] Write setup and usage instructions, including credential entry and known visibility/recovery limits.
- [x] Complete a live smoke test after the user enters their key in the app: select/create a task, log a short session, and verify the entry in ClickUp.
- [ ] Deliver the executable, source, and setup instructions.

## Beautification pass — 2026-09-12

- [x] Add shared System/Light/Dark resources, smaller typography, tighter dialogs, and monochrome controls while retaining native WPF.
- [x] Add Minimal, Compact (default), and Detailed presentations with a live settings preview and safe size-aware placement.
- [x] Format elapsed durations as `1w 2d 3h 04m 05s`, pad minutes/seconds, and keep ordinary ticking from shifting controls.
- [x] Keep full session details and recovery accessible through the picker, status icon, and tray menu.
- [x] Pass 146 application checks and 56 placement checks; inspect rendered Light/Dark views and isolated live native controls without ClickUp writes.
- [ ] Complete physical DPI/monitor/high-contrast/taskbar occupancy checks and controlled long-duration performance comparison.

See [BEAUTIFICATION_PLAN.md](BEAUTIFICATION_PLAN.md) for the design and detailed verification record.

## Search integration requirements — 2026-09-14

- [x] Implement MCP transport and cached, debounced list/task search; replace hierarchy discovery in Settings and the explicit workspace-search button in the task picker.
- [x] Persist a Current list / Entire workspace toggle and retain local matches while remote results arrive.
- [x] Add browser OAuth with dynamic client registration, PKCE, state validation, and Windows-user-bound encrypted storage; remove API-key entry from Settings.
- [x] Complete live MCP OAuth and inspect actual schemas. MCP search excludes Lists; list discovery uses batched MCP hierarchy pages with local filtering and a ten-minute page cache. Live `Proje` returns both Project lists and caches all three lists. Task list filters use `subcategories`, and results locate lists under `hierarchy.subcategory`.
- [x] Update connection status immediately after MCP-only sign-in, restore existing partial sessions, and keep search counts separate from connection status. 172 application checks pass.
- [ ] Implement separate REST OAuth authorization: the live MCP token is rejected by REST. No API-key fallback exists. Timer operations, task hydration/creation, and status loading remain unavailable until this is complete.
- [ ] Verify live task search and multi-page hierarchy behavior.
- Remote search must originate from a user searching in the list or task picker with a nonempty query. Opening a picker or restoring saved text must not trigger remote search. Any debounce or result pagination must remain attached to that active user search and cancel when the picker closes or its query/account/workspace changes.
- Never call MCP search from startup, timer reconciliation, scheduled/background work, cache refresh, status discovery, account validation, or automatic retries. Fetch known objects and timing data through their REST endpoints; do not use search to resolve known IDs or populate caches in the background.
- Apply item-type and status filters to returned results locally as needed; reuse cached results without additional search calls. Do not silently fall back to a full workspace crawl on a search failure or quota limit.
- [x] Fixture checks verify that opening/closing pickers, restoring settings, blank queries, and connection loading make zero remote search calls. Search is isolated from timing, cache refresh, and status discovery. Debouncing, late-response cancellation, cache isolation, scope persistence, and MCP streamed-response handling are covered.
- API review: the published v2 specification (83 paths) and v3 specification (23 paths) expose no general text-search endpoint. The v3 Search for Docs endpoint has metadata filters but no text query. Saved Views support task text filtering; this does not provide cross-type workspace search.

## References

- [Windows topmost window positioning](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)
- [ClickUp authentication](https://developer.clickup.com/docs/authentication)
- [Get tasks](https://developer.clickup.com/reference/gettasks)
- [Get workspace tasks](https://developer.clickup.com/reference/getfilteredteamtasks)
- [Create task](https://developer.clickup.com/reference/createtask)
- [Start a time entry](https://developer.clickup.com/reference/startatimeentry)
- [Stop a time entry](https://developer.clickup.com/reference/stopatimeentry)
- [Get running time entry](https://developer.clickup.com/reference/getrunningtimeentry)
- [Update a time entry](https://developer.clickup.com/reference/updateatimeentry)
- [Get time entries within a date range](https://developer.clickup.com/reference/gettimeentrieswithinadaterange)
