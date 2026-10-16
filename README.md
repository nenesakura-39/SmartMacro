# SmartMacro

**Desktop automation for Windows: record, edit, play, and find text on screen.**

Owner & developer: **Riku** · Version 1.0.0

SmartMacro records your mouse and keyboard, lets you edit the result step by step, and plays it back.
It can also wait for text to appear on screen and click it, using the text recognition (OCR) built into Windows.
Everything stays on your PC. Nothing is uploaded.

---

## Requirements

- Windows 10 version 2004 (May 2020 Update) or newer, or Windows 11, 64-bit.
- Nothing else to install. The release package includes everything it needs.
- For the **Find text (OCR)** step, Windows needs an OCR language. Most PCs already have one for their display language. Check under **Settings → Text recognition** in SmartMacro.
  - If it's missing, go to Windows Settings → Time & language → Language & region. Open your language's options, add "Optical character recognition", then restart SmartMacro.

## Install / run

1. Unzip `SmartMacro-1.0.0-win-x64.zip` to any folder, for example `Documents\SmartMacro`.
2. Double-click **SmartMacro.exe**.
3. The first time, Windows SmartScreen may say "Windows protected your PC". The app isn't code-signed, because signing certificates are paid. Click **More info → Run anyway**.

Your macros and settings live in `%LOCALAPPDATA%\SmartMacro`, separate from the app folder. Updating means replacing the app folder; your macros stay.

## Quick start

1. **Record:** press **Record** (or Ctrl+Shift+F9), do your task, then press Ctrl+Shift+F9 again. A new macro opens in the editor.
2. **Edit:** select a step to change it on the right. Use the left panel to add steps. Reorder with the arrows (Alt+Up/Down) and delete with Delete.
3. **Play:** press **Play** (or Ctrl+Shift+F10).
   - In the editor header, set how many times to repeat (0 = until stopped), the speed, and a wait between repeats.
4. **Find text:** add a **Find text (OCR)** step. Type the text, optionally click **Pick area on screen**, press **Test now**, and choose what happens when it's found (for example, "Click it").

## Recording games (game mode)

Normal recording saves cursor **positions** ("move to X/Y"). First-person games lock the cursor and turn the camera from the
mouse's **relative movement**, so position-based playback can't turn the camera. For those, use game mode:

1. **Settings → Recording and playback → Recording mode → Game**, then **Save**.
2. Stand in your starting spot and face your starting direction. Press the record shortcut, play, press it again.
3. The recording becomes one **Recorded input (game mode)** step. It keeps every mouse movement sample and the exact
   down/up time of every key and button, and plays them back on the original timeline.
4. Before playing, put your character in the **same spot facing the same way**, then press Play.

Even with a perfect recording, a game can play it back a little differently: frame rate, physics, network lag and game
updates all change results, and some games ignore simulated input. Small differences can add up over a long recording.
Short recordings, a fixed starting point, and game sensitivity/frame-rate settings that match the recording help most.
Game mode never tries to get around anti-cheat.

## Safety

- **Esc** on your keyboard stops playback immediately.
- Ctrl+Shift+F10 also stops playback. You can change all shortcuts in **Settings → Keyboard shortcuts**.
- While recording or playing, a small banner at the top of the screen shows what SmartMacro is doing.
- Keys or mouse buttons held by a macro are always released when it stops, and while it's paused (they're pressed again when you resume).

## Library

- **Play / Edit / Duplicate / Delete** each macro. Deleted macros go to a Trash folder, so you can still recover them.
- **Export…** saves a macro as a `.smartmacro` file to share or back up. **Import** adds macros from files.
  - Imported files are checked first. They can only contain SmartMacro actions, never programs.

## Known limits

- Windows doesn't allow normal apps to control or record apps running **as administrator**. Nothing works on the lock screen or on UAC prompts.
- Some games with anti-cheat ignore simulated input. SmartMacro doesn't try to bypass this.
- Mouse positions are exact screen pixels. If you change monitors, resolution or scaling, recorded clicks may land in the wrong place. Use **Find text** steps for more robust macros.
- OCR reads normal horizontal text best. Very small, stylized or rotated text may not be found.

## Troubleshooting

- **Settings → Copy diagnostics** copies version, Windows, screen and OCR info plus the recent log, for bug reports.
- Daily log files: `%LOCALAPPDATA%\SmartMacro\Logs`.
- **Find text → Test now** saves `Logs\last-ocr-test.png`, which shows exactly what was captured.
- **Clicks in a game's menus don't register:** check the log for lines starting with "Input check:". They say if the
  game window wasn't active (the click only switched to it) or if the game had locked the mouse. Use Desktop mode for
  menu clicks, and Game mode for camera movement (including holding the right mouse button to turn the camera).

---

© 2026 Riku
