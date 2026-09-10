# ClickUp Timer for Windows

Phase 1 is user-approved. Phase 2 adds secure ClickUp setup, saved preferences, and floating mode. Phase 3 adds a task picker and timer controls preview. **The timer is still a local demo and does not log time to ClickUp yet.**

## Run and connect

Launch `artifacts/phase2/ClickUpTimer.exe`, or run `./Run-Timer.ps1`. Keep the published folder together; it includes the .NET runtime.

1. Open the gear button on the timer (first-run setup opens automatically).
2. Paste your personal API key in **ClickUp account**. Workspaces and lists load automatically after you finish typing. A saved key reconnects automatically when Settings opens; **Refresh connection and lists** retries manually.
3. Choose a workspace and preferred list. Labels include space/folder paths, and accessible shared lists are included.
4. Click **Save settings**. The key is saved to Windows Credential Manager and the preferred list's tasks are cached in the background.

To replace the key, enter a new one and connect before saving. Leave the field blank to retain the saved key. Cancel and failed connections leave the saved setup unchanged. Display settings can be saved offline without reconnecting an existing account. The preferred list is the saved destination for future task creation; task selection and creation are available; real logging remains Phase 5.

Available workspace/list choices remain selectable during refresh. The Display tab's **Apply display settings** button works independently of account loading and keeps any unsaved key in the open window.

The key field explicitly shows **API key saved on this PC** when a credential exists; it never displays the secret. Lists appear progressively as locations load. Saving a verified key and workspace does not require a preferred list, so a failed list request cannot discard account setup. Reopening Settings automatically retries discovery. Launching the executable again opens Settings in the existing app.

## Display and startup

- **Taskbar:** uses a free gap in the existing bar and reserves no extra space. Drag the dotted grip to reposition within available gaps.
- **Floating:** a movable topmost window. Drag the dotted grip in either direction; position is remembered.
- **Monitor:** choose a display or automatic primary display. If the saved monitor disappears, an available display is used.
- **Launch at sign-in:** initially off. Enabling it registers this executable for your Windows user; disabling it removes the registration. Re-save the option after moving the executable.
- Right-click the timer or use its notification-area icon for Settings, Reset position, or Exit.

Opening Start does not intentionally hide the timer. The taskbar-owned window retains its last verified gap through temporary accessibility failures when the reserved geometry is unchanged. Fresh controls override cached geometry. If no verified gap exists, use the tray icon to switch to Floating. Auto-hidden and vertical taskbars are unsupported in taskbar mode. Full-screen applications may cover the timer.

## Local data

- `%LOCALAPPDATA%\ClickUpTimer\settings.json`: preferences, account identity, workspace, and preferred list; no API key.
- `%LOCALAPPDATA%\ClickUpTimer\task-cache.json`: task IDs, names, statuses, and fetch time, scoped to account/workspace/list.
- Windows Credential Manager generic credential `ClickUpTimer/PersonalApiKey`: the key, persisted for this Windows user on this machine.
- `%LOCALAPPDATA%\ClickUpTimer\prototype\diagnostics.json`: overwritten positioning snapshot; no credentials or taskbar-button names.

Phase 1 placement is imported when no new settings exist. Corrupt settings produce a visible warning and safe defaults. Saving writes valid settings in their place.

## Build and verification

Install the .NET 10 SDK and run `./Build-Timer.ps1`. It prefers `%LOCALAPPDATA%\Microsoft\dotnet`, runs both check projects, and publishes a self-contained Windows x64 build to `artifacts/phase2`. Close the app before rebuilding. `./Run-Timer.ps1 -Inspect` adds a taskbar button so desktop automation can find the timer; normal mode omits it. Use the Timer scripts for current work; the older Phase 1 artifacts/scripts are retained for reference.

- 23 placement checks pass: gaps, occupied controls, stale geometry, floating bounds, negative monitor origins, and 100–200% scaling.
- 77 application checks pass: user/workspace/list discovery, shared and folder lists, cache pagination and isolation, corrupt settings, safe network errors, Credential Manager round-trip/replacement/cleanup using a dedicated test credential, and WPF settings-flow/save/reopen regressions, including selection preservation during progressive list loading.
- Live UI checks passed for layout, switching between modes, two-axis floating dragging, saved placement restoration across restart, and startup remaining off.
- Phase 1 Start-menu ownership/geometry behavior is preserved in the extracted positioning controller.
- Live validation passed after the user saved their key: the installed app reconnects automatically, shows the saved-key indicator, and discovers three lists with both selectors enabled. Automated tests use isolated fixtures and test credentials.
- Physical monitor disconnect/reconnect and actual Windows display-scale changes remain deferred hardware checks under the user-approved Phase 1 plan.

## Code organization

`TimerWindow` and `SettingsWindow` provide the UI. `WindowPositioner`, `Placement`, `Native`, and `TaskbarScanner` handle Windows geometry. `TimerCoordinator` owns demo timing. `ClickUpClient` handles authenticated reads. `SettingsStore`, `CredentialStore`, and `StartupRegistration` handle persistence. `AppServices` coordinates saved settings and cache refreshes.

See `PLAN.md` for phase checkboxes and remaining work.


## Phase 3 preview

Click the task name to open the preferred-list picker above the timer. Double-click a task or select it and choose **Use selected task**. Start begins a fresh local session; Stop retains its duration. Selecting another task resets and stops the preview. The picker and right-click menu can open the selected task in ClickUp. **Today: —** means the personal ClickUp total is not yet available. Preview sessions are not saved or logged to ClickUp. Real logging, totals, and recovery remain Phase 5.


## Phase 4 task selection

The picker lists up to eight recent tasks before preferred-list tasks. Type to filter names and IDs locally; **Search workspace** explicitly expands to accessible tasks and subtasks across the workspace. Results appear as pages load and remain labeled incomplete until loading succeeds. Each time the picker opens it starts with local results again.

The search text also supplies the title for **Create task in [preferred list]**. Creation makes a real, unassigned ClickUp task with the list's default status and selects it for the local timer preview. Failures retain your text. If a request loses its connection, refresh and check whether creation succeeded before retrying.

Settings → **Ignored statuses** loads statuses grouped by list. Checked statuses are hidden from recent tasks and searches; the current task remains visible. Done/closed types are initially hidden. **Save settings** or **Apply status filters** stores changes. Save account changes before loading statuses for a different workspace.

Recent selections and per-list status filters are stored in settings.json. Automated checks cover their persistence, filtering, task-creation payloads, and failed/duplicate creation requests. Live creation validation remains for the final integration smoke test.
