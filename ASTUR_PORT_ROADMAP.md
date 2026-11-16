# Astur-to-Tessellation Port Roadmap

This document tracks the work required to turn the current C#/WinUI 3
Tessellation application into a feature-complete Astur-style Windows tiling
window manager. The implementation reference is the Rust project under
[`tmp/astur`](./tmp/astur), principally its README, `crates/astur/src/main.rs`,
`crates/astur/src/layout.rs`, `crates/astur/src/yasb.rs`, and
`crates/astur-config/src/lib.rs`.

The goal is behavioral parity, not a line-for-line translation. The Rust
implementation has thousands of lines of platform-specific code and many
interacting features; porting should preserve user-visible behavior while
using maintainable C# services and tests.

## Status key

- **Done (basic):** a first implementation exists; it is not necessarily at
  Astur parity or live-desktop validated.
- **Remaining:** not implemented or missing important behavior.
- **Deferred:** lower priority than completing and stabilizing the window
  manager core.

## Current C# implementation

The current solution provides a useful foundation:

- WinUI 3 settings UI; JSON preferences under the user's local application data.
- Six layout calculations: dwindle, master, columns, grid, monocle, scrolling.
- Native window and monitor enumeration, tiling, gaps, automatic retiling, and
  original-bounds restoration.
- Tiling-mode toggle (`modifier+T`), workspace switch/move (`modifier+1–9`),
  next/previous focus (`modifier+J/K`), and adjacent window-order swap
  (`modifier+Shift+J/K`) shortcuts, plus directional focus
  (`modifier+arrow keys`) and directional move (`modifier+Shift+arrow keys`).
- Basic master-layout pane sizing (`modifier+H/L`), focused-window promotion
  (`modifier+M`), per-window floating toggle (`modifier+F`), and persisted
  master ratio control in Settings; focused-window close request (`modifier+W`).
- Basic master-layout pane sizing (`modifier+H/L`), focused-window promotion
  (`modifier+M`), and persisted master ratio control in Settings.
- App-managed workspaces that hide inactive windows. These are shared across
  monitors; they are not Windows virtual desktops or Astur's optional
  per-monitor workspace model.
- Optional persisted workspace names, shown in Settings, workspace controls,
  and status-bar pills; unnamed workspaces retain their numbered labels.
- Modifier-drag move/resize and delayed focus-follows-mouse.
- Window glide animation for layout changes, with a configurable 0–250 ms
  duration.
- A basic non-activating status bar per monitor, with active/occupied workspace
  states and click-to-switch, focused-window title, layout/tiling state, clock,
  and Windows appbar work-area reservation.
- Tray-first startup, Settings/Quit menu, graceful cleanup on quit and
  Windows session end, a local crash-recovery journal, and optional
  Start-with-Windows registration.
- Modifier+T toggles tiling while keeping the resident manager and workspaces
  active; Settings' Tile now action explicitly resumes tiling and retiles.

These are **basic ports**, not a claim of full parity. In particular, live
desktop testing has not been completed. The current solution has no automated
test project, and the settings UI is not yet a full Astur settings editor.

## Remaining work, by priority

### P0 — Make the existing manager safe and dependable

#### P0.1 Validate lifecycle and window restoration

- Exercise start, retile, workspace switch, drag, stop, tray quit, sign-out,
  shutdown, and relaunch with real windows.
- Verify that the restore journal is written before a window is moved or hidden,
  that it survives forced process termination, and that recovery never applies a
  saved rectangle to a reused window handle or a different process instance.
- Test restoration after display topology and resolution changes; decide how
  saved coordinates should be handled when the original monitor is absent.
- Make stop/rollback continue attempting restoration for every window after an
  individual failure, then report all failures. Verify that a failed restore
  does not clear recoverable journal entries or leave the manager reporting a
  clean stop.
- Verify Windows session-end cleanup within the time Windows allows, including
  when the UI dispatcher is busy. Keep next-launch recovery as the fallback.
- Test packaged and unpackaged startup registration and confirm Windows' own
  startup-task state is reflected accurately in the UI.
- Cover Explorer/taskbar restarts, app startup failures, and repeated start/stop
  cycles. Ensure hooks, timers, tray icons, and message-loop threads are always
  released.

#### P0.2 Build a testable core

- Add a test project for pure geometry and preference behavior.
- Test every layout with zero/one/many windows, narrow work areas, gaps at their
  limits, negative-coordinate monitors, and odd dimensions.
- Extract/manage state transitions behind testable interfaces where that
  materially improves coverage; keep P/Invoke integration tests separate from
  pure layout tests.
- Validate all supported RIDs and a packaged build in CI or a repeatable local
  build command. Record that packaging can produce a non-blocking missing
  `mspdbcmf.exe` symbols warning in this environment.

#### P0.3 Window discovery and ownership rules

- Port Astur's eligibility rules: owned/tool windows, shell and desktop windows,
  cloaked/UWP windows, minimized and transient windows, styles/classes that
  should not be tiled, elevated windows that cannot be controlled, and
  application-specific exceptions.
- Add explicit "ignore" and "float by default" class/process matching.
- Preserve visible unmanaged/elevated windows on all workspaces where possible.
- Reconcile window create/show/destroy/location events with manager state
  without recursive retile loops or racing animations.

### P1 — Complete the keyboard-driven tiling manager

The reference hotkey descriptions are in `tmp/astur/crates/astur-config/src/lib.rs`
near its hotkey defaults and key documentation; command handling and manager
state are in `tmp/astur/crates/astur/src/main.rs`.

- **Focus and navigation:** refine directional focus/move to match Astur's
  directional scoring and monitor-transition behavior; add
  focus-follow-cursor behavior and workspace-aware focus.
- **Window/order management:** refine neighbor swaps to match Astur's exact
  stack semantics and refine moving by direction across monitors.
- **Layout controls:** extend master sizing controls beyond the current fixed
  5% step and persist a complete master/layout state model. Add
  layout-selection/toggling shortcuts and implement split resizing in dwindle.
- **Hotkey model:** configurable single-key bindings, extra chords, conflict
  reporting, key-up handling, and safe support for Left Alt, either Alt, Win,
  Ctrl+Win, and Caps Lock. Preserve Windows-reserved sequences such as Win+L
  and Ctrl+Alt+Del, and avoid breaking AltGr/text input.
- **Workspace model:** names, icons, configurable key assignments, up to 20
  workspaces, persistence of active workspace, and optionally independent
  workspaces/layouts per monitor. Keep this distinct from native Windows
  virtual desktops unless native desktop integration is deliberately added.
- **Per-monitor layout:** independently choose a layout for each display and
  keep monitor/work-area/DPI state correct when displays are added, removed,
  rotated, or scaled.

#### Scrolling layout parity

The C# scrolling layout now supports configurable column width, workspace-
scoped viewport offsets, adjacent-column focus with automatic viewport reveal,
and H/L/M/R width controls. It remains a basic arrangement. Astur's `layout.rs`
and manager state implement a richer persistent column strip. Remaining
behavior includes:

- Persistent columns, per-column width, stacked windows, and focus/selection
  within a column.
- Add/remove/reorder/consume windows into adjacent columns; moving a column
  independently of moving a window.
- Width and height preset cycling, incremental resize, full-width toggle, and
  configurable viewport centering.
- Horizontal focus/scroll with correct end-of-strip behavior and monitor
  transitions; vertical focus within a column.
- Tabbed columns, tab-bar rendering and interaction, and drag/drop zones for
  creating columns, stacking, resizing, and moving columns.
- Peek/park rules that avoid windows bleeding onto adjacent monitors.
- Per-workspace column persistence beyond viewport offsets, plus live
  multi-monitor validation.

Each behavior should be ported with focused layout/state tests before wiring its
keyboard and mouse input.

### P1 — Astur animation parity

The current C# glide moves actual top-level windows asynchronously. Astur instead
places windows and animates DWM-thumbnail overlays, and includes separate
workspace transitions. Remaining work:

- DWM thumbnail capture/overlay lifecycle, click-through behavior, z-order, DPI,
  monitor transitions, focus opacity, and cleanup when windows disappear.
- Separate window motion styles (off/glide/spring), easing, open/close/resize
  transitions, and configurable durations.
- Workspace slide/spring/fade/off transitions with cancellation and retargeting
  when another shortcut arrives mid-animation.
- Define and test interaction rules for animation versus drag, retiling,
  workspace changes, focus, shutdown, and display changes.
- Keep the current direct-window animation as a safe fallback if overlay
  creation or capture fails.

### P1 — Status bar parity

Astur's bar is not just a window containing workspace buttons. It has a Win32
window per selected monitor, reserves work-area space, updates from manager
state, supports mouse interaction, and renders configurable widgets. Relevant
reference areas are `main.rs`'s status-bar implementation and
`astur-config/src/lib.rs`'s `bar_*` settings.

- **Basic implementation now present:** one non-activating top/bottom bar per monitor;
  appbar work-area reservation; active/occupied workspace display and
  click-to-switch and wheel-to-cycle; focused title, clickable layout/tiling
  state (click cycles layouts), and clock.
- Settings now control the bar edge (top/bottom) and logical height; height is
  scaled per monitor's DPI and the selected edge remains reserved for tiles.
- Validate the appbar handshake and restoration against the Windows taskbar,
  other appbars, Explorer restarts, monitor hot-plug, and mixed-DPI displays.
- Add monitor selection, theme, styling, and accessibility options.
- Make the current left/center/right zones configurable widget lists.
- Workspace pills: icons, optional app icons, hover state, and active-pill
  animation.
- Core widgets: app buttons and configurable date/time formats.
- System widgets: CPU, memory, battery/charging, network speed/type, volume/mute,
  keyboard language, disk, uptime, and media controls. Handle missing devices
  and unavailable data clearly.
- Widget click actions for left/right/middle buttons; standard actions should
  open the relevant Windows app/settings page.
- Styling/configuration: padding, font and weight,
  colors/palettes, dark/light/system theme, strip/floating/islands/pills shapes,
  workspace styles, separators, borders, hover, tooltips, app labels, and
  per-monitor selection.
- Optional auto-hide and reveal-on-edge-hover; ensure it does not steal focus or
  interfere with the Windows taskbar.
- Bar configuration editing and live update from Settings; avoid introducing a
  second, incompatible configuration source without a migration plan.

### P2 — Launcher and system menu

#### App launcher

- Global shortcut and non-activating/controlled activation behavior, monitor
  placement, keyboard navigation, selection, and dismissal.
- Search installed desktop and Store apps, custom commands/URLs, open windows,
  and scoped/indexed files. Add fuzzy scoring, MRU ranking, result limits,
  refresh, and safe process launching.
- Inline calculator and configurable web-search fallback.
- Optional sources/providers for clipboard history and emoji; require explicit
  opt-in and document data retention/privacy.
- File result details (modified/size/path columns), reveal-in-folder, icons,
  wide/narrow sizing, and accessible keyboard/mouse interaction.
- Persist launcher configuration and MRU safely; bound indexing and background
  work.

#### System/power menu

- Configurable Power and Setup categories and actions for lock, sleep,
  hibernate, sign-out, restart, and shutdown; require confirmation for
  destructive actions.
- Settings/config/reload/restart actions, screenshot/wallpaper tools, custom
  commands, URLs, scripts, ordering, icons, and categories.
- Show actionable errors for unavailable or denied Windows operations.

### P2 — Rules, integrations, and desktop extras

- Rich process/executable, window class, and title rules: ignore/float, layout,
  workspace, and monitor assignment; deterministic precedence and diagnostics.
- Scratchpad window and configurable launch/toggle chord.
- Optional workspace-aware Alt+Tab replacement.
- Workspace-triggered wallpapers and per-workspace/monitor wallpaper settings.
- Media title/control integration.
- Local named-pipe IPC with a documented, validated command protocol and
  restrictive defaults; optional YASB-compatible workspace WebSocket API bound
  to loopback only.
- Persist workspace state and launcher MRU; define what is intentionally not
  persisted (for example HWND-to-workspace assignments across process restarts).
- Optional clipboard history/emoji sources, disabled by default and with clear
  privacy/retention controls.

### P2 — Complete settings and configuration experience

- Expand the preferences model to cover the full manager, bar, launcher, system
  menu, rules, hotkeys, and integrations.
- Decide on a versioned configuration format and migration path from the
  existing JSON preferences; support import/export and reset.
- Apply changes live where safe; use rollback/confirmation for risky key or
  window-manager changes.
- Add searchable settings, shortcut conflict warnings, rule/entry tables,
  validation errors, presets/previews, and a generated shortcut cheat sheet.
- Provide troubleshooting diagnostics and logs without exposing sensitive
  process/window data by default.
- Make all controls reflect the effective runtime state, including denied or
  externally disabled Windows startup settings.

### P3 — Polish, accessibility, and release engineering

- Per-monitor-DPI-v2 behavior for bars, popups, overlays, settings, and window
  geometry. Verify mixed-DPI setups and negative-coordinate monitors.
- Keyboard accessibility, high contrast, reduced-motion behavior, and screen
  reader labels for custom UI.
- Resource/performance bounds for event hooks, animations, launch search,
  statistics, icons, and caches.
- Structured logs, crash reports/diagnostics, upgrade/uninstall cleanup, and
  versioned package identity/signing strategy.
- Validate clean install, upgrade, uninstall, packaged/unpackaged launch,
  autostart, Explorer restart, multi-monitor, sleep/resume, sign-out/shutdown,
  and recovery after forced termination.

## Suggested implementation order

1. Establish pure layout/configuration tests and fix lifecycle/restoration issues.
2. Finish focus, swap, directional movement, float/tiling, master controls, and
   configurable hotkeys.
3. Replace the placeholder scrolling layout with explicit persistent column
   state and tests.
4. Implement window rules and complete multi-monitor/workspace state semantics.
5. Extend the new status bar with configurable widgets and settings.
6. Add the launcher and system menu as separate, testable services.
7. Add desktop extras and IPC only after core lifecycle and privacy boundaries
   are stable.
8. Complete animation parity, accessibility, packaging, and live acceptance
   testing.

## Definition of "full clone"

Tessellation should not be called a full Astur clone until the core behaviors
and the full-edition features described in the reference README are implemented
or explicitly documented as out of scope, and acceptance tests demonstrate:

- Correct window handling across supported layouts, workspaces, monitors, DPI
  settings, and lifecycle transitions.
- Keyboard/mouse behavior matching the reference, with configurable bindings
  and no unintended input leakage or swallowing.
- Status bar, launcher, system menu, tray, settings, and extras working together
  against one consistent configuration and manager state.
- Recovery from app failure, Windows session end, display changes, and normal
  upgrade/uninstall flows.
- A repeatable test/build/package process and live desktop verification.

## Reference map

- `tmp/astur/README.md` — user-visible feature inventory, hotkeys, layouts,
  scrolling behavior, launcher, system menu, settings, and extras.
- `tmp/astur/crates/astur/src/main.rs` — manager state, native hooks, hotkeys,
  window rules, status bar, scrolling manager, animations, persistence, IPC,
  wallpaper, focus, and cleanup.
- `tmp/astur/crates/astur/src/layout.rs` — pure layout and scrolling geometry.
- `tmp/astur/crates/astur/src/yasb.rs` — optional loopback YASB-compatible
  workspace API.
- `tmp/astur/crates/astur-config/src/lib.rs` — configuration schema, defaults,
  parsers/formatters, key bindings, bar widgets/presets, launcher, rules, and
  system menu.
- `tmp/astur/crates/astur-settings/src/main.rs` — full Settings GUI and its
  interaction model.
