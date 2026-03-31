# Desktop Shell Launch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a launch-time shell integration that hides the desktop and exposes a temporary Recycle Bin pin on the real Windows taskbar while Veil is running.

**Architecture:** Keep the orchestration logic in a small testable service and isolate all Windows shell side effects behind a dedicated adapter. The app will apply the shell state before bootstrapping UI services and restore it during controlled shutdown.

**Tech Stack:** .NET 10, WinUI 3, Win32 interop, Registry, UI Automation, MSTest

---

### Task 1: Add the test project and expose internals

**Files:**
- Modify: `Veil.sln`
- Modify: `tests/Veil.Tests/Veil.Tests.csproj`
- Create: `apps/desktop/Veil/Properties/AssemblyInfo.cs`

- [ ] Add the test project to the solution.
- [ ] Reference the app project from the test project.
- [ ] Expose app internals to `Veil.Tests`.

### Task 2: Write the failing orchestration tests

**Files:**
- Create: `tests/Veil.Tests/DesktopShellServiceTests.cs`
- Delete: `tests/Veil.Tests/Test1.cs`

- [ ] Write a failing test for the successful apply path.
- [ ] Write a failing test for rollback when the taskbar pin check fails.
- [ ] Run the targeted test command and confirm the failures are about missing production code.

### Task 3: Implement the orchestration service

**Files:**
- Create: `apps/desktop/Veil/Services/DesktopShellService.cs`

- [ ] Add the snapshot, artifact, and bridge abstractions.
- [ ] Implement the apply path with rollback on taskbar pin failure.
- [ ] Implement the restore path with idempotent cleanup.

### Task 4: Implement the Windows shell adapter

**Files:**
- Create: `apps/desktop/Veil/Services/WindowsDesktopShellBridge.cs`
- Modify: `apps/desktop/Veil/Interop/NativeMethods.cs`

- [ ] Add the Win32 and registry integration needed for desktop icon state, Recycle Bin visibility, Explorer restart, and taskbar probing.
- [ ] Create and clean up the temporary Start Menu shortcut and taskbar XML.
- [ ] Apply and restore the temporary taskbar policy values.

### Task 5: Wire the feature into app startup and shutdown

**Files:**
- Modify: `apps/desktop/Veil/App.xaml.cs`

- [ ] Apply the shell state before tray initialization.
- [ ] Restore the shell state during controlled shutdown.
- [ ] Log failure paths without crashing app startup.

### Task 6: Verify

**Files:**
- None

- [ ] Run the targeted MSTest command.
- [ ] Run a solution build.
- [ ] Record any residual risk around the unsupported taskbar pin path.
