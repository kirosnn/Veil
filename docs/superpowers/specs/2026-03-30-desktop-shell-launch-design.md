# Desktop Shell Launch Design

**Goal:** When Veil launches, desktop shortcuts and the Recycle Bin desktop icon should be hidden. A Recycle Bin entry should appear pinned on the real Windows taskbar while Veil is running. When Veil exits, Windows should return to its original state.

**Chosen approach:** Use a dedicated shell service that snapshots the original desktop icon state and the relevant taskbar policy registry values, prepares a temporary Start Menu shortcut for `Corbeille`, applies a temporary taskbar layout policy that pins that shortcut, and restarts Explorer. This taskbar pin flow is intentionally unsupported and best-effort.

**Key constraints**

- The desktop icon visibility must be restored exactly to its pre-launch state.
- The Recycle Bin desktop icon visibility must be restored exactly to its pre-launch state.
- Any existing `StartLayoutFile` or `LockedStartLayout` policy values must be restored exactly.
- If the temporary taskbar pin cannot be detected after Explorer restarts, Veil must roll back the desktop and policy changes instead of leaving the user with a hidden desktop and no visible Recycle Bin entry.

**Implementation outline**

- Add a testable `DesktopShellService` orchestration layer.
- Add a Windows adapter that:
  - reads and writes desktop icon visibility;
  - reads and writes the Recycle Bin desktop icon registry values;
  - creates a temporary `Corbeille` Start Menu shortcut and policy XML;
  - applies and restores the taskbar layout policy values;
  - restarts Explorer and probes the taskbar via UI Automation.
- Call the service from app launch before tray initialization.
- Restore the original shell state during app shutdown.

**Verification**

- Unit-test the orchestration path with a fake adapter.
- Build the solution.
- Run the targeted tests.
