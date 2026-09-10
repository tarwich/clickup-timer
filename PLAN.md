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

Acceptance: setup succeeds with a valid key, reports invalid credentials clearly, and restores configuration after restart without storing the key in plaintext application files.

## Phase 3 — Timer display and controls

- [ ] Display the selected task name and a large `HH:MM:SS` current-session timer.
- [ ] Display a smaller personal total for the selected task today.
- [ ] Show explicit running/stopped text and icons alongside color; distinguish unconfirmed operations and connection failures.
- [ ] Provide Start/Stop, task picker, settings, and an open-in-ClickUp action.
- [ ] Open the task picker upward from taskbar mode.
- [ ] Retain the completed session duration after Stop until the next Start or task selection.
- [ ] Keep controls readable and usable in both taskbar and floating modes at different display scales.

Acceptance: the user can clearly identify the selected task, elapsed session time, and whether time is running or stopped at a glance.

## Phase 4 — Task selection, search, and creation

- [ ] Show eight most recently selected tasks followed by preferred-list tasks, and persist MRU order across restarts.
- [ ] Include everyone's accessible tasks and subtasks, not just the user's assignments.
- [ ] Filter task names and IDs within the cached preferred list first.
- [ ] Provide an explicit **Search workspace** action with paginated loading and progressive results.
- [ ] Distinguish incomplete searches from searches with no matches.
- [ ] Add ignored-status checkboxes in settings, grouped by list as needed; initially exclude done/closed statuses.
- [ ] Apply status exclusions to search and MRU while keeping the actively timed task visible.
- [ ] Offer **Create task in [preferred list]**, prefilled with the search text as the task title.
- [ ] Create an unassigned task using the list's initial status, then select it using the normal task-switching behavior.
- [ ] Preserve entered text and show a useful error when task creation fails.

Acceptance: the user can quickly return to recent tasks, search the preferred list before expanding the scope, exclude unwanted statuses, and create a task in the configured list.

## Phase 5 — ClickUp timing and recovery

- [ ] Start and stop time entries directly through ClickUp's API.
- [ ] When switching while running, stop the old task before starting the new one. Switching while stopped stays stopped.
- [ ] Stop on screen lock, sleep, or explicit Quit; require manual Start when the user returns.
- [ ] Require connectivity to start, switch, or create tasks.
- [ ] Serialize timer operations and prevent duplicate clicks.
- [ ] Reconcile uncertain API responses before retrying writes.
- [ ] Read ClickUp's running entry on startup, resume, before changes, and every 15 seconds; reflect changes made in ClickUp itself.
- [ ] Update elapsed time each second from timestamps rather than accumulated UI ticks.
- [ ] Calculate today's personal task total in the Windows local timezone without double-counting the running entry.
- [ ] Persist requested stop timestamps before network calls.
- [ ] Clearly mark unsuccessful stops as unconfirmed and reconcile the specific entry on reconnection.
- [ ] Correct delayed stops to their recorded stop time where possible, never stop an unrelated replacement timer, and surface conflicting external edits for review.
- [ ] Preserve recoverable state across crashes and respect API rate limits.

Acceptance: successful operations match ClickUp, task transfers do not overlap, and network failures remain visible and recoverable. Unexpected power loss cannot guarantee an immediate server-side stop.

## Phase 6 — Verification and delivery

- [ ] Test start/stop, rapid clicks, task transfers, and partial API failures.
- [ ] Test external timer changes, pending-stop recovery, restart recovery, and midnight totals.
- [ ] Test MRU persistence, paginated search, status exclusions, and preferred-list changes.
- [ ] Test invalid credentials and task-creation failures.
- [ ] Verify lock, sleep, and Quit stop timing and returning does not restart it.
- [ ] Repeat visibility acceptance checks with the completed UI in taskbar and floating modes.
- [ ] Package a self-contained Windows build matching this PC's architecture.
- [ ] Write setup and usage instructions, including credential entry and known visibility/recovery limits.
- [ ] Complete a live smoke test after the user enters their key in the app: select/create a task, log a short session, and verify the entry in ClickUp.
- [ ] Deliver the executable, source, and setup instructions.

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

- [x] Fix preferred-list refresh overwriting the current selection. Preserve saved and in-progress selections, retain missing selections with an explanation, and pass all 56 Phase 2 checks.
