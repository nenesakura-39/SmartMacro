# Stage 3 – Manual tests (conditions and loops)

Do these after `tools\verify.cmd` is green. Each test says what to do and what you should see.
Tick a box when it works; if something doesn't, write down the test number, what you did and what happened
(a screenshot of the editor and the Logs page helps a lot).

You need: SmartMacro, Notepad, and Paint (or any app with a big coloured area).
Tip: keep the **Logs** page open in a second look after each run — Stage 3 writes one line per If answer
(only when it changes) and one line when each loop ends, saying why.

---

## A. Editor basics

- [ ] **A1 Add If.** Open a macro, click **Logic → If** in the toolbox.
  You get two new steps: **If** and **End If**, and a status message saying so. The If is selected.
- [ ] **A2 Add Repeat.** Click **Logic → Repeat**. You get **Repeat** and **End Repeat**.
- [ ] **A3 Indentation.** Add a *Move mouse* step and move it (Alt+Up / Alt+Down) between Repeat and End Repeat.
  The step is indented to the right. Inside an If inside a Repeat it is indented twice.
- [ ] **A4 Wrap in Repeat.** Add 3 normal steps. Click the first, Shift+click the third, click **Wrap in Repeat**
  (or Ctrl+Shift+R). A Repeat appears above and an End Repeat below; the 3 steps are indented.
- [ ] **A5 Wrap in If.** Same with **Wrap in If** (Ctrl+Shift+I).
- [ ] **A6 Wrap refuses half blocks.** Select only the *Repeat* step and the step below it (not its End Repeat),
  click Wrap in If. A warning says to select whole blocks; nothing changes.
- [ ] **A7 Wrap refuses gaps.** Ctrl+click two steps that are not next to each other, click Wrap. A warning appears.
- [ ] **A8 Delete a block marker.** Select only a **Repeat** step and press Delete. The Repeat **and** its End Repeat
  are removed, the steps that were inside stay (status message says so). Same test with an **If** that has an Else:
  If, Else and End If all disappear, inner steps stay.
- [ ] **A9 Problems list.** Add a single **End If** from the toolbox with no If above it. The Problems list shows an
  error like "Step N: This End If has no If before it…", the step has a red icon, and **Save is refused**.
  Delete it again: the error disappears.
- [ ] **A10 Overlap error.** Make: Repeat, If, End Repeat, End If (move steps so the Ends are in the wrong order).
  The Problems list explains that blocks must be closed in reverse order. Play is refused with the same message.
- [ ] **A11 Too deep.** (Optional, a bit tedious) Nest 9 Repeats inside each other. The 9th shows "nested too deeply".
- [ ] **A12 Save + reopen.** Make a macro with If / Else / End If and Repeat / End Repeat, save, restart SmartMacro,
  open it. Everything (conditions, limits, indentation) is still there.

## B. If / Else with text (Notepad)

Setup: open Notepad, type `READY` in big text (Ctrl + mouse wheel to zoom in). Keep it visible.

- [ ] **B1 True branch.** Macro: `If` (Look for: Text, `READY`, "is on screen") → `Type text "yes"` → `Else` →
  `Type text "no"` → `End If`. Put a Mouse click on the Notepad text area before the If so typing goes there.
  Press **Test now** inside the If's condition first: it should find READY.
  Play. "yes" is typed. Log: `If (step …): yes.`
- [ ] **B2 False branch.** Change the text to look for to `BANANA`. Play. "no" is typed. Log: `If (step …): no.`
- [ ] **B3 NOT.** Set "True when it" to **is NOT on screen** with `BANANA`. Play. "yes" is typed.
- [ ] **B4 Wait for it (timeout).** Look for `LATER`, "Keep checking for" = 5000 ms. Start the macro, and within
  5 s type `LATER` in Notepad. The If answers yes as soon as it's seen. If you don't type it, after ~5 s it
  answers no and the Else part runs.
- [ ] **B5 Pause during the wait.** As B4, but press **F11 (Pause)** after 2 s, wait 10 s, press F11 again.
  The If keeps waiting for about 3 more seconds (paused time doesn't count) before answering no.

## C. Loops

- [ ] **C1 Repeat N times.** `Repeat` (A number of times: 3) → `Type text "x"` → `End Repeat`. Play: `xxx`.
  The status/progress text shows "loop turn 1 of 3", "2 of 3", "3 of 3".
- [ ] **C2 Repeat until (Notepad).** `Repeat` (Until a condition becomes true; Text `STOP`; Max turns 50) →
  `Type text "a"` → `Delay 500` → `End Repeat`. Play, and after a few a's type `STOP` yourself in Notepad
  (use the keyboard; the macro keeps typing too, that's fine). The loop ends within about a second.
  Log: `Repeat (step …) finished after N turns: its condition is true.`
- [ ] **C3 Repeat while (Paint colour).** In Paint draw a big red rectangle. `Repeat` (While a condition is true;
  Color – pick the red with **Pick from screen**; area around the rectangle; Max turns 100) → `Delay 500` →
  `End Repeat` → `Type text "done"` into Notepad. Play, then fill the rectangle white in Paint.
  The loop ends shortly after and "done" is typed.
- [ ] **C4 Max turns → stop.** `Repeat` While text `READY` is on screen, **Max turns 5**, "When a limit is reached:
  Stop the macro" → `Delay 200` → `End Repeat`. Play (READY stays visible). After 5 turns the macro stops with an
  error saying it reached its limit of 5 turns.
- [ ] **C5 Max turns → continue.** Same, but "Leave the loop and continue". After 5 turns the steps after End Repeat run.
- [ ] **C6 Max time.** Max turns 0, Max time 3000. The loop stops after about 3 s (error or continue as chosen).
- [ ] **C7 Safety limit required.** Set Max turns 0 **and** Max time 0 on a While loop. The Problems list shows
  "needs a safety limit" and Save / Play are refused.
- [ ] **C8 Nested.** `Repeat 2 times` → `Type "A"` → `Repeat 3 times` → `Type "b"` → `End Repeat` → `End Repeat`.
  Result: `AbbbAbbb`.

## D. Safety inside loops

- [ ] **D1 Esc inside a loop.** Make a While loop with Max turns 1000 that holds a key: before the loop
  `Key press: Hold down Shift`, inside the loop `Type text "a"` + `Delay 300`. Play, then press **Esc**.
  Playback stops at once and **Shift is not stuck** (type in Notepad afterwards: letters are lowercase).
- [ ] **D2 Stop button / F10** inside the same loop: same result.
- [ ] **D3 Esc during a long condition wait.** If with "Keep checking for" = 60000 looking for text that isn't there.
  Press Esc after 2 s: it stops within a fraction of a second.
- [ ] **D4 Pause/resume inside a loop.** C1 with 10 turns and a 500 ms delay; press F11 in the middle, wait, F11 again.
  The loop continues where it was and still types exactly 10 letters in total.
- [ ] **D5 Empty loop doesn't burn CPU.** `Repeat` While text READY is on screen (Max time 10000) → `End Repeat` with
  nothing inside. While it runs, Task Manager shows SmartMacro using a modest amount of CPU (OCR every 500 ms),
  not a full core. Write down the CPU % you see.

## E. Old macros

- [ ] **E1** Open a few macros made before Stage 3 (Stage 1 recordings, Stage 2 Find image / color macros). They open,
  play as before, and saving them doesn't change their format (they're still readable by the Stage 2 build).
- [ ] **E2** Export a macro **with** blocks and try to import it in an older SmartMacro build (if you kept one): it
  refuses with "made by a newer version (format 5)". That is expected.

## F. Games (only where the game's rules allow automation)

- [ ] **F1** Any game with a simple UI: `Repeat until` an image (e.g. a "Done" / "Full" icon) appears, with a click inside.
  Check that it stops by itself when the icon appears, and that Esc always works.
- Note anything that behaves differently in a game than in Notepad / Paint (e.g. capture shows black in exclusive
  full-screen; then try windowed or borderless).

---

## What to send back

1. `artifacts\build.log` and `artifacts\test.log` from `tools\verify.cmd`.
2. The list of ticked / failed tests from this file, with notes for failures.
3. For D5: the CPU % you saw.
4. Anything confusing in the editor (wording, layout) — those go to the final polish pass.
