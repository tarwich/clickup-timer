# ClickUp Timer for Windows

A compact Windows timer in the unused part of the existing taskbar, with optional floating mode. **Start and Stop now log real time to ClickUp.**

## Run and connect

Launch `artifacts/phase2/ClickUpTimer.exe` or `./Run-Timer.ps1`. The existing launch path is retained for upgrades. Keep the published folder together; it includes the .NET runtime.

1. Open Settings using the gear button.
2. Paste your personal API key. Workspaces and lists load automatically. Saved keys display an explicit indicator without exposing the secret.
3. Choose a workspace. Search the full Space / Folder / List path or list ID, select a preferred list, then Save settings. The selected list stays visible while results load, and broad result sets are capped to keep Settings responsive.
4. Click the task name on the timer to choose a task. Type to filter names and IDs locally; use **Search workspace** to load wider results progressively.
5. Press Start to log time. Stop retains the completed duration. Selecting another task while running finishes the old entry before starting the new one. Selecting while stopped stays stopped.

Eight recent tasks are saved across restarts. **Create task in [preferred list]** uses the search text as the title and creates a real, unassigned task with the list's initial status. Creation failures retain your text; after an uncertain connection failure, refresh and check whether the task was created before retrying.

Settings → **Ignored statuses** lists checkboxes grouped by list. Checked statuses are hidden from recent tasks and searches; the current task stays visible. Done/closed status types are initially hidden. Save settings or Apply status filters stores changes. Save account changes before loading statuses for a different workspace.

## Timing and recovery

- The large time is the current or completed session. **Today** is your personal total for the selected task in the Windows local timezone.
- Running/stopped states include text and icons. Orange pending/offline/review states indicate uncertainty; hover over the timer for details.
- The app reconciles with ClickUp every 15 seconds and before changes. Changes made in ClickUp are reflected locally. Elapsed time comes from timestamps, not accumulated UI ticks.
- Lock, sleep, and Quit record a stop cutoff before sending a request. Returning does not automatically start a new entry. If offline, recovery completes when the app is open and connectivity returns.
- Uncertain starts are matched using a unique session description, never automatically resent. Stop checks the expected current entry, uses ClickUp's dedicated Stop endpoint, then corrects the saved cutoff when needed. A stop made in ClickUp is accepted with its recorded duration. The check and Stop are separate requests, so simultaneous external changes can still race.
- If the entry was edited elsewhere, recovery pauses for review. Right-click → Open task in ClickUp to inspect it. **Accept ClickUp state…** explicitly abandons the pending local request and accepts the current server record. Use Retry ClickUp connection for ordinary network failures.
- API rate-limit responses back off for one minute. Account/workspace/key changes are blocked while a running or unresolved timer would be stranded; preferred-list and display changes remain available.

Unexpected power loss cannot record a stop cutoff. A server timer may continue until the app returns or you stop it in ClickUp. Quit while offline leaves its stop request for the next launch. ClickUp's API does not provide atomic conditional updates; simultaneous edits in another client can race with a read/write sequence. Conflicting records detected during recovery are left for review.

## Display and startup

- **Taskbar:** uses an existing free gap and reserves no extra desktop space. Drag the dotted grip to move within safe gaps.
- **Floating:** a movable topmost window; drag the grip in either direction.
- **Monitor:** choose a display or automatic primary display. A disconnected saved monitor falls back to an available screen.
- **Launch at sign-in:** initially off. Re-save this option after moving the executable.
- Right-click or use the notification-area icon for Settings, recovery actions, reset position, or Exit. Launching the executable again opens Settings in the running app.

Opening Start does not intentionally hide the timer. It retains its last verified taskbar gap through temporary accessibility failures. If no safe gap exists, use the tray icon to select Floating. Auto-hidden and vertical taskbars are unsupported in taskbar mode. Full-screen apps and Windows security screens may cover the timer.

## Local data

- `%LOCALAPPDATA%\ClickUpTimer\settings.json`: account/list preferences, eight recent tasks, per-list status filters, display/startup preferences; no API key.
- `task-cache.json` in the same directory: account/list-scoped cached tasks.
- `timer-state.json`: selected task, last server entry, and durable pending start/stop requests. Writes are flushed before server mutations.
- Windows Credential Manager generic credential `ClickUpTimer/PersonalApiKey`: the API key for this Windows user.
- `prototype/diagnostics.json`: positioning diagnostics; no credentials or taskbar-button names.

## Build and verification

Install .NET 10 SDK and run `./Build-Timer.ps1`. The script prefers `%LOCALAPPDATA%\Microsoft\dotnet`, runs the check projects, and publishes a self-contained Windows x64 app. Exit the running app normally before rebuilding so it can stop logging.

103 application checks cover account setup, large-workspace list search, persistence, MRU/filtering, task creation, timing API payloads, task transfers, duplicate operations, lost responses, offline/restart stop recovery, external conflicts, rate-limit backoff, and midnight/DST totals. There are also 23 placement geometry checks.

Live task search, status discovery, and user-confirmed task creation work. Read-only timing authentication and current-entry/history queries passed. Live Start/Stop has been clicked and verified against the server. Physical lock/sleep tests remain part of the final smoke test. Monitor disconnect/reconnect and actual display-scale changes also remain deferred hardware checks. Use `./Run-Timer.ps1 -Inspect` to expose a timer taskbar button for desktop automation.

## Code organization

`TimerWindow`, `TaskPickerPanel`, `SettingsWindow`, `PreferredListPicker`, and `StatusSettingsPanel` provide the UI. `TimerCoordinator` owns server timing and recovery; `TimingMath` computes display durations and local-day totals. `ClickUpClient` handles authenticated API operations. `SettingsStore`, `CredentialStore`, and `StartupRegistration` handle persistence. `WindowPositioner`, `Placement`, `Native`, and `TaskbarScanner` handle Windows geometry.

See [PLAN.md](PLAN.md) for phase checkboxes and remaining verification. API behavior follows ClickUp's [time entry documentation](https://developer.clickup.com/reference/getrunningtimeentry), [entry updates](https://developer.clickup.com/reference/updateatimeentry), and [date-range queries](https://developer.clickup.com/reference/gettimeentrieswithinadaterange).
