# Beautification pass

## Direction

Make this feel like a quiet, compact Windows utility: smaller typography, tighter spacing, neutral surfaces, and color used deliberately for interaction and status. Keep C# / WPF and the existing native taskbar integration. Mac is a future project; this pass should establish reusable visual rules without introducing a cross-platform framework or rewriting the application.

Implemented on 2026-09-12. The design targets below remain the reference; implementation and verification status are recorded at the end.

## Current issues found in the code

- `TimerWindow` combines task and status, session and Today, a grip, Start/Stop, and Settings in five fixed columns. Its 336 × 40 device-independent-unit footprint is also hardcoded in `Placement`.
- The timer uses dark blue-gray surfaces, a colored border, mint running text, and orange warnings. The task picker uses white and black; Settings uses another background and text palette. Brushes and control styles are scattered across files.
- Settings starts at 600 × 710 with 24-unit outer margins, another 16 units inside tabs, a 25-unit heading, and 34–36-unit controls.
- The task picker is 440 units wide with a large heading, repeated help/status text, and five stacked action buttons below the results.

## 1. Establish shared visual rules

- Centralize colors, text styles, spacing, and button/input styles in WPF resources used by the timer, picker, and settings.
- Provide System, Light, and Dark appearance, defaulting to System. System follows the Windows app appearance setting and responds to changes while running; all app surfaces use the same semantic palette. Windows high-contrast colors take precedence over the saved appearance choice.
- Use neutral backgrounds and borders, one accent for selection/focus, amber for pending/offline/review, and red for failures. Ordinary stopped text stays neutral. Avoid large colored button fills for routine controls.
- Use Segoe UI consistently, approximately 12–13 units for normal text, 11–12 for secondary text, and 18 for dialog headings. Reduce the timer from 21 to approximately 14–16 units with fixed-width digits.
- Use spacing tokens of 4, 8, 12, and 16 units: 4 between adjacent controls, 8–12 between groups, and 12–16 around dialogs. Aim for 26–28-unit controls while retaining clear focus and usable hit areas.
- Replace mixed Unicode/emoji-style controls with consistent monochrome vector icons. Keep tooltips and accessible action names.

Sizes are initial design targets in WPF units at 100% scaling, subject to visual checks rather than hard limits that clip content.

## 2. Add taskbar presentation presets

| Preset | Always visible | Initial target |
| --- | --- | --- |
| Minimal | Session time, Start/Stop, state indicator | 125–155 × 30–32 |
| Compact — default | Truncated task name, session time, Start/Stop, state indicator | 225–260 × 30–32 |
| Detailed | Task name, explicit state text, session time, Today total, Start/Stop | 300–330 × 36–40 |

- Compact and Minimal use one row. Detailed uses two rows with restrained typography. The size targets above cover durations through `23h 59m 59s` and include the drag handle and all persistent controls; longer durations may require additional width as described below.
- Use unit-based duration notation throughout the timer and its details, including Today: `1w 2d 3h 04m 05s`, replacing `HH:MM:SS`.
- Always show minutes and seconds with two digits: `00m 05s`, `04m 05s`, `1h 04m 05s`. Omit leading zero weeks/days/hours; once a larger unit is present, retain intervening zero units (for example, `1d 0h 00m 05s`). Leave weeks, days, and hours unpadded.
- Use fixed-width/tabular digits. Reserve a time column wide enough for `23h 59m 59s`, right-align the duration, and keep Start/Stop fixed beside it. Shorter strings leave unused space within that column, so seconds, minutes, and the appearance of hours do not move adjacent controls.
- For longer durations, measure the full string and increase the time column only when needed. Keep the task-name column at its preset width, truncating the name with an ellipsis. Validate a safe position for the complete proposed window size before applying growth; do not clip the time or silently drop units. If no gap fits, use the no-gap behavior below. Retain the expanded width until the next stopped task selection or presentation change to avoid repeated resizing.
- Proposed elapsed-duration arithmetic is 60 seconds per minute, 60 minutes per hour, 24 hours per day, and 7 days per week; these represent elapsed time rather than calendar or configured workday lengths. The user requested ClickUp-style notation; equivalence to ClickUp's day/week conversion settings has not been established. Keep the conversion rules centralized so that convention can be changed independently of the layout.
- Remove the permanent gear button; Settings remains in the existing right-click and tray menus. Keep a small, consistent drag handle in each preset.
- Click the task name to open the picker. In Minimal, clicking the time opens the picker. Start/Stop has its own distinct hit target in every preset.
- Add a compact current-session section to the task picker containing the full task name, explicit state text, session time, and Today. Also expose these in the timer tooltip. Add a tray-menu Choose task action so this surface is reachable using the keyboard through the notification area; focus search when it opens.
- Use distinct monochrome state symbols for running, stopped, pending, offline, and review, with accessible names and amber emphasis for uncertainty. Clicking the state indicator opens the picker with the current-session explanation focused. When relevant, that section offers Retry connection and Accept ClickUp state, retaining the existing confirmation for abandoning pending recovery. Never make uncertainty hover-only.
- Add a presentation selector with an inline preview under Display settings. Default new and existing settings without a saved preset to Compact; keep Detailed available for users who want the current information density.
- Save presentation separately from Taskbar/Floating placement mode. Both modes use the chosen presentation initially; separate preferences can wait until there is a demonstrated need.
- Recalculate the safe taskbar gap when the preset changes. Preserve the preferred position where possible. If no gap fits, hide the taskbar overlay, notify once when entering that condition, and retain the tray icon so the user can choose a smaller presentation or Floating in Settings. Do not switch presentation or placement mode automatically. Resume the overlay when a safe gap becomes available; timing continues throughout.

## 3. Tighten the picker and settings

Task picker:

- Target approximately 360–400 units wide, with 10–12 units of padding and compact 26–30-unit result rows.
- Put search first, followed by results. Reduce the heading and replace repeated explanatory paragraphs with short contextual hints.
- Make selection keyboard-first: arrows to navigate, Enter to choose, Escape to dismiss. Retain double-click selection and a compact explicit selection action.
- Consolidate secondary actions into a small footer or overflow menu. Keep Search workspace explicit and task creation visible when search text is present, with the destination list named.
- Show search progress, partial results, and creation failures when relevant. Preserve typed text on failure and the existing selected-task/filtering behavior.

Settings:

- Target approximately 520 × 580 initially, resizable with scrollable tab content so longer account/list names remain usable.
- Replace the oversized introductory heading with a simple Settings title; reduce nested margins and label gaps.
- Keep account, display/startup, and ignored-status sections. Put Appearance and Presentation together in Display, with compact previews.
- Preserve independent display application, unsaved account drafts, saved-key indications, list selection during refresh, and all recovery-related restrictions.

## 4. Implement in reviewable stages

1. **Visual baseline and resources:** capture current views and idle memory/CPU, then apply shared typography, palettes, and control states. Review light/dark examples for timer, picker, and settings.
2. **Timer presets and geometry:** add presentation settings with safe defaults; update the shared `TimingMath.Format` duration formatter; build preset layouts; parameterize `Placement.Find` and `Placement.Floating`; pass the active dimensions through `WindowPositioner`. Measure the proposed layout, validate placement using those dimensions, then apply the layout and native bounds together before the next visible render. Never enlarge the visible window before validating its new footprint.
3. **Picker and settings density:** reorganize controls, reduce spacing, and add the appearance/presentation controls and preview.
4. **Verification and delivery:** run existing checks plus targeted preset persistence and placement coverage; visually inspect each preset and publish an updated Windows build with before/after captures.

Keep presentation code separate from `TimerCoordinator` and ClickUp operations. Use the existing service boundaries; avoid a broad architecture refactor. Document the color roles, dimensions, icons, and state behavior for a future native Mac implementation. Defer Mac packaging, menu-bar behavior, and platform-specific storage to that project.

## Acceptance checks

- Default Compact targets no more than 260 × 32 units for durations through `23h 59m 59s`: about 23% narrower and 20% shorter than the current 336 × 40 target. Validate this with the actual font and controls; if it cannot fit legibly, revise the target explicitly rather than shrinking the font below the planned range. Longer durations follow the measured-growth rule above.
- All surfaces share the chosen palette and typography, including hover, focus, selection, disabled, and error states.
- Start/Stop, choosing tasks, settings, dragging, and recovery remain reachable in every preset by the appropriate pointer/keyboard path.
- Check running, stopped, no task, loading, pending stop, offline, and review states; long names, unavailable totals, and long elapsed durations must not overlap controls.
- Verify duration formatting at zero, minute/hour/day/week rollovers, and multiple weeks. Examples: `00m 00s`, `59m 59s`, `1h 00m 00s`, `1d 0h 00m 00s`, and `1w 2d 3h 04m 05s`. Confirm ordinary ticking does not shift adjacent controls and that unavailable Today totals still display `—`.
- Verify 100%, 125%, 150%, and 200% scaling, taskbar gaps, saved positions, popup screen-edge placement, and resizing between presets. Record unavailable physical monitor/DPI checks as deferred.
- Preset/theme choices survive restart; older settings load safely. Changing presentation during timing does not create, stop, or alter a ClickUp entry.
- Existing timing, account, filtering, and placement checks pass. Use fixtures for timer-state previews so visual review does not write time to ClickUp.
- Compare idle CPU, memory, and responsiveness against the baseline under the same conditions. Keep the current native runtime and avoid introducing polling or animation solely for appearance.

## Implementation and verification record — 2026-09-12

- Implemented shared light/dark/system palettes, compact native WPF controls, monochrome vector state/action icons, and the three timer presets. The settings preview uses the actual `TimerStrip` control.
- Implemented padded duration formatting, fixed normal-duration columns, retained long-duration expansion, preset persistence/defaults, and parameterized taskbar/floating placement.
- Implemented denser settings and task picker, consolidated secondary actions, keyboard selection, accessible session details, and relevant recovery actions. Recovery details are a read-only text field so keyboard users can focus and copy them.
- Reduced the timer's visual tick from five times per second to once per second. Cache state icons and duration measurements until their shape/state changes. No new runtime framework, animation loop, or background poll was added.
- 146 application checks and 56 placement checks pass. New checks cover migration, independent display saving with an account draft, unchanged running entries after appearance changes, duration boundaries, width retention/reset, and preset geometry at 100–200% scale.
- Rendered actual WPF controls in Light and Dark, including timer state/long-duration examples and scale previews at 100%, 125%, 150%, and 200%. Reviewed the old live Settings window and new isolated native settings/timer fixtures. Verified mouse/keyboard preset selection, the actual floating footprint, popup theme, recovery focus, and Escape dismissal. No test time was written to ClickUp.
- Physical DPI changes, monitor reconnection, actual taskbar resizing under changing occupancy, and high-contrast switching still need hardware/desktop smoke checks. The existing lock/sleep delivery checks also remain open.
- A ten-second spot sample measured the old long-running process at about 626 MiB / 0.55 CPU-seconds and the new isolated offline fixture at about 302 MiB / 1.08 CPU-seconds. These differ in lifetime and account state and are **not a controlled performance comparison**; equivalent-state, longer-duration profiling remains deferred.
- The release is staged in `artifacts/beautification` and installed in the original `artifacts/phase2` location after normal shutdown. The existing launcher and startup path remain valid.
- Fixed the default WPF context-menu icon gutter with compact, themed templates for the app's flat action menus. Light and Dark menu renders are included in the visual verification output.
