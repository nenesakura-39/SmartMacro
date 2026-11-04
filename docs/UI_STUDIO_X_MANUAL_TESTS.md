# SmartMacro "Studio X" — Manual UI Verification Guide

This checklist covers visual, interaction, and Windows 11 DWM integration verification for the **SmartMacro Studio X (Obsidian Neon AAA UI Upgrade)**.

---

## 1. Custom Studio Window Chrome & Windows 11 DWM (`MainWindow.xaml` / `MainWindow.xaml.cs`)
- [ ] **Custom 40px Title Bar**: Verify the custom 40px title bar renders with the vector `BrandLogo`, breadcrumb (`Library` / `Workflow Editor ▸ <Macro Name>`), and quick Save icon button when on the Editor page.
- [ ] **Windows 11 Mica-Alt Backdrop**: On Windows 11 (Build 22621+), verify the subtle Mica-Alt dark backdrop and dark window border (`DWMWA_SYSTEMBACKDROP_TYPE = 4`).
- [ ] **Windows 11 Snap Layouts**: Hover the mouse over the Maximize/Restore button in the top-right corner for ~500 ms and confirm Windows 11's native Snap Layouts flyout appears (`WM_NCHITTEST` → `HTMAXBUTTON`).
- [ ] **Global `Ctrl+K` Search Pill**: Click the centered `Quick Command... Ctrl+K` pill in the title bar from any page (or press `Ctrl+K` in the Editor) and verify it jumps straight to the Command Palette.

## 2. Global Unified Transport Bar (`MainWindow.xaml`)
- [ ] **Record Button + Recording Options Popover**: Click the `▾` chevron next to `Rec` in the title bar and verify the glass popover opens with **Recording Mode**, **Replay Fidelity**, and quick switches (`Show mini status overlay`, `Minimize while playing`, `Show overlays in OBS / Discord`).
- [ ] **Neon Play / Pause / Stop**: Play a macro (`F6` or the green neon `Play` button) and verify the live telemetry pill (`Run 1/1 · 00:01 · Step ...`) appears inside the Transport Bar alongside `Pause` and `Stop`.

## 3. Collapsible Animated Navigation Rail (`MainWindow.xaml`)
- [ ] **Pin / Unpin Toggle**: Click the Pin button at the top of the left sidebar to collapse it to the `56px` icon rail.
- [ ] **Hover Flyout**: While unpinned, hover the mouse over the `56px` rail and verify it smoothly expands to `192px` as an overlay without shifting the main editor workspace, then collapses when the mouse leaves.
- [ ] **Neon Active Pill**: Switch between `Library`, `Editor`, `Manual`, `Logs`, and `Settings` and verify the glowing vertical neon indicator bar animates on the active navigation item and the page content cross-fades smoothly.

## 4. Workflow Editor Hero Screen (`Views/EditorView.xaml`)
- [ ] **Toolbox v2 (`248px`)**: Type `"ocr"`, `"variable"`, or `"checkpoint"` in the `Filter steps & patterns...` box at the top of the Toolbox and verify categories filter live.
- [ ] **Step Card v2 & Hover Action Pill**: Hover over any step card in the timeline and verify the floating action pill (`▶ Run`, `🧪 Test`, `📋 Copy`, `🗑 Delete`) appears on the right without shifting layout.
- [ ] **Smart Chip Badges**: Verify steps with Smart Intent (`🧭 Intent`), Smart-Aim (`✨ Smart-Aim`), Adaptive Delay (`⚡ Adaptive`), Set Variable (`{var}` / `👁 OCR Read`), Checkpoint (`📍 Name`), Call Sub-Macro (`↗ Sub-Macro`), or Failure Policies (`🔁 Retry ×N` / `🛡 Recovery`) display their inline pill badge on the step card.
- [ ] **Workflow Minimap Rail**: Load or create a multi-step macro and verify the `16px` `WorkflowMinimap` on the right of the step list renders color-coded ticks by step category, highlights errors/warnings and the selected step, and scrolls to any step when clicked or dragged.
- [ ] **Collapsible Bottom Dock (`🧠 Variables`, `🛡 Watchers`, `⚠ Problems`)**: Click the tab pills in the bottom dock header to expand/collapse the dock and switch between Variables, Background Watchers, and Problems.
- [ ] **Sub-Macro Deep Link (`Open ↗`)**: Select a `Call sub-macro` step and click `Open ↗` in the Inspector (or on the step card badge) to jump directly to that sub-macro.

## 5. Mission Control Floating Telemetry Pod (`Views/MissionControlWindow.xaml`)
- [ ] **Open via `Ctrl+M` or Title Bar**: Click the `Mission Control` radar icon in the title bar (or press `Ctrl+M`) to toggle the floating `MissionControlWindow`.
- [ ] **Live Telemetry & Variables**: Run a macro that updates variables (e.g., the **RPG / MMO Smart Loop** blueprint) and verify the big neon Run Counter, `Cascadia Mono` elapsed clock, active step progress bar, and live Variables readout update in real time.

## 6. Macro Library, Settings & Live Theme Switching (`Views/LibraryView.xaml`, `Views/SettingsView.xaml`)
- [ ] **Workflow Fingerprint Strip**: Open the Macro Library and verify each macro card displays a proportional multi-color category bar (`Input`, `Vision`, `Logic`, `Memory`, `Feedback`, `Utility`) plus `BRAIN` and `🧭 INTENT` pills where applicable.
- [ ] **Live Studio Theme, Accent & Density Switching**: Open `Settings` → **Studio Appearance & Motion** and switch between **Obsidian Neon** and **Midnight Blue**, click each of the **5 Accent Swatches** (`Neon Brand`, `Electric Blue`, `Cyber Violet`, `Emerald Pulse`, `Rose Quartz`), and toggle **Layout Density** (`Comfortable` / `Compact`) — verify the entire UI updates immediately.
