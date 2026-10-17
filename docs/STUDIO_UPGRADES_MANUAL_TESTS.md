# SmartMacro Studio upgrades (7 items) – manual test checklist

Written without a .NET SDK (not compiled yet). Run `tools\verify.cmd` first (send `artifacts\build.log` /
`artifacts\test.log` if anything fails), then go through this list. Notepad + Paint are enough for most of it.

## 1. Failsafe: nothing stays held down
- [ ] Make a macro: Key press **Down** W → Delay 10 s. Open Notepad, play, press **Esc** after 2 s.
      Notepad stops getting "wwww" right away; the W key isn't stuck (type normally afterwards).
- [ ] Same with Mouse click **Down** (left) + Delay 10 s in Paint: Esc → the line stops being drawn.
- [ ] Feedback loop holding the left button (test page `docs/feedback-test-page.html`): Esc while it holds → button released.
- [ ] Log shows "Input safety: released …" only when something was really still held (normally the engine already let go).

## 2. ▶ Run step / ⏭ Play from here
- [ ] Select a step → **▶ Run step** (inspector header, right-click menu, or Ctrl+F5) plays only that step once.
- [ ] Run step on a **Repeat** (or its End Repeat) runs the whole loop; on an **If** the whole If block.
- [ ] Run step on a turned-off step still runs it (only for the test; it stays turned off).
- [ ] A step with a problem elsewhere in the macro (e.g. an unfinished Find image at the end) doesn't stop testing step 1.
- [ ] **⏭ Play from here** (toolbar / right-click) plays from the selected step to the end, once (repeat count ignored).
- [ ] Play from a step **inside** a Repeat: the log says "Starting at step N (the start of the block…)" and the loop runs normally.
- [ ] Esc / Stop work during both; the running step highlight and the mini overlay work as usual.

## 3. Undo / Redo + Copy / Paste
- [ ] Delete a Feedback loop or a whole If block → **Ctrl+Z** (or ↶ in the Workflow steps toolbar) brings it back exactly.
- [ ] Undo also works for: add step, quick pattern, duplicate, move / drag, Wrap in If / Repeat, Clean up mouse paths, paste.
- [ ] **Ctrl+Y** (or ↷, or Ctrl+Shift+Z) redoes. A new edit after Undo clears Redo. Tooltips say how many undos are left.
- [ ] Typing in a text box: Ctrl+Z there still undoes the typing (the text box's own undo), not a step edit.
- [ ] Select a step (or a whole block, or just its If / Repeat) → **Ctrl+C** → select another step → **Ctrl+V**: pasted after it.
- [ ] Open another macro → Ctrl+V pastes the same steps there too (new step IDs; Find image pictures included).
- [ ] Copying only "Repeat + one inner step" (without End Repeat) shows a "copy whole blocks" message.
- [ ] Opening another macro or "Discard" clears the undo history.

## 4. Target window + auto-adjust coordinates
- [ ] Editor header → **🎯 Any screen ▾** opens the popover. Pick a window (refresh button lists open windows) or "Any / Main screen".
- [ ] Turn on **Auto-adjust coordinates…** → reference text shows the current size (e.g. 1920×1080 at (0, 0)).
- [ ] Pick a click position in a windowed app (e.g. Paint's colour), move the window, play → the click follows the window.
- [ ] Resize the window (or change the game resolution) → positions scale proportionally (custom Find areas too).
- [ ] Target window closed → playback stops with "The target window "…" isn't open".
- [ ] Turning auto-adjust off plays the exact stored pixels again. The library card shows a 🎯 TARGET pill.

## 5. On-screen highlight
- [ ] **Test detection now** on Find image / color / text: a cyan dashed box shows the search area and a green box +
      crosshair + "✓ 94% Match" label marks the match for ~0.8 s, then fades. Not found → red box "✗ Not found".
- [ ] Feedback loop test: cyan bar area + green box on the marker ("✓ Marker at 42%").
- [ ] **🎯 Show search area on screen** (next to Pick area, and in the Feedback loop bar area) outlines the area anytime.
- [ ] The highlight never takes focus, can't be clicked, and a second test right after doesn't find the highlight itself.
- [ ] Mixed-DPI / second monitor: the boxes sit exactly on the right spot.

## 6. Humanize
- [ ] Header **Humanize** pill switch; tooltip explains it. Play 20 clicks on the same spot in Paint (pencil): dots land a
      few px apart. Delays vary a little (log timings). Find-step clicks stay inside the found button.
- [ ] Turned off: exact same pixel and timing as before. The library card shows a HUMANIZE pill.

## 7. Studio UI: Library, Settings, Logs
- [ ] Library: search box filters cards by name / description (also "vision", "ocr", "feedback", "logic", "input", "errors").
- [ ] Each card: title, description, step count badge, category pills, "edited 5 min ago", buttons ▶ Play, ✏ Edit,
      📋 Duplicate, 📤 Export .smacro, 🗑 Delete (each acts on that card). Double-click a card opens it; a fast double-click
      on a card button doesn't.
- [ ] **📥 Import .smacro** in the header imports .smacro (new) and .smartmacro (old) files. Export writes `.smacro`.
- [ ] Settings: cards Global Hotkeys, Recording Defaults, Playback & Safety, Storage & Data with pill switches; Save /
      Undo changes / Reset still work; hotkey boxes still capture combos.
- [ ] Logs: dark #0D131F surface, coloured pills (INFO blue, MATCH violet, DONE green, WARN amber, ERROR red), search box,
      All / Info / Match / Warn / Error filter, Copy Log (copies what's shown) and Clear Log.
