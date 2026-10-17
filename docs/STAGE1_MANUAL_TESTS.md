# Stage 1 – Smart recording: manual test checklist

Automated tests use fake input and a fake clock. They prove what SmartMacro *decides and sends*, not that a real
app or game accepts it. These manual tests are what actually confirm Stage 1. Tick each box and note anything odd.

**Before you start**

1. Run `tools\verify.cmd`. It must say `OK: build and tests passed`. If not, send `artifacts\build.log` and
   `artifacts\test.log` and stop here.
2. Start SmartMacro (F5 in Visual Studio, or the built exe).
3. Settings → Recording and playback → **Recording mode** should say **Smart (recommended)**.
   (Old settings files are switched to Smart once. Desktop only / Game only are still in the list.)
4. Keep the **Logs** page in mind: after every recording there's a line like
   `Smart recording: … → N steps (… editable part(s), … recorded-input part(s); mouse: … cursor-position part(s), … camera / relative part(s))`.
   Copy that line into your notes for each test.

---

## A. Desktop regression (Notepad)

| # | Do this | Expected |
|---|---------|----------|
|  A1 | Open Notepad. Press Ctrl+Shift+F9, click inside Notepad, type `hello world`, press Enter, stop with Ctrl+Shift+F9. | A new macro opens with **only normal steps** (Click, Press, Move, Delay). No "Recorded input" step. Log says `0 camera / relative part(s)`. |
| A2 | Clear Notepad, press Play. | Same text appears, clicked in the same place, at about the same speed. |
| A3 | Record: move the mouse around slowly, double-click a word, scroll the wheel 3 notches, select text by dragging. | Normal steps only (drag = Press / Move / Release). Play it back → same result. |
| A4 | Record while typing a lot, then moving the mouse right after typing. | Still normal steps only. (Windows hides the cursor while you type; SmartMacro must ignore that short moment.) |
| A5 | Record pushing the cursor hard against the left screen edge, then click. | Normal steps only (pushing against an edge isn't "camera movement"). |
| A6 | Hold Shift + click, hold Ctrl + press A. | Steps show **Left Shift** / **Left Ctrl** (or Right if you used the right ones). Playback does the same. |
| A7 | Click SmartMacro's own Stop button to finish one recording (instead of the shortcut). | That click isn't part of the recording. |
| A8 | Play any macro and press Esc halfway. | Stops immediately; no stuck keys/buttons (type in Notepad to check, try Shift). |
| A9 | Open your **old** macros (made before this update), including a Desktop one and a Game-mode one. Play them. | They load without errors and play exactly like before. The Game-mode step's editor shows the old "Recorded in game mode" text and the "Move the cursor to where recording started" option. |
| A10 | OCR regression: run a macro with a Find text step. | Works like before. |
| A11 | Export a new smart recording, then Import it. | Imports fine. (Older SmartMacro versions will say it was made by a newer version — expected.) |

## B. Recorded-input editor

| # | Do this | Expected |
|---|---------|----------|
| B1 | Make any recording that produced a "Recorded input" step (see section C). Select it. | You see Length / Events / Mouse / Keys / Buttons, and a **Mouse playback** list with Part 1, Part 2… Each part shows its time range, "Detected: … — <reason>", a drop-down, and "Plays as: …". |
| B2 | Change one part's drop-down. | "Plays as" updates, the macro is marked unsaved, Ctrl+S saves it. Reopen → the choice is still there. |
| B3 | Press **All: relative**, then **All: automatic**. | All parts change, then go back to what was detected. |

## C. Games (where allowed)

Only test in games/servers where macros are allowed, preferably a private server or an offline/test place.
SmartMacro doesn't bypass anti-cheat, and some games ignore simulated input completely — that's a valid result to report.

Use **windowed** mode first (easier to stop), same sensitivity and frame-rate settings for recording and playback, and
start every test from the **same spot facing the same direction**.

| # | Do this | Expected |
|---|---------|----------|
| C1 | Roblox (e.g. Grow a Garden): click a shop/menu button, click a seed, buy. No camera movement. | Normal editable steps. Playback clicks the same buttons. If a click doesn't register, check the log for `Input check:` lines. |
| C2 | Roblox, third person: hold the **right mouse button** and turn the camera left/right for ~2 s, release. | One **Recorded input** step. Parts: cursor part → **relative part** (reason: "the mouse moved but the cursor stayed in place" or "the cursor was hidden") → maybe a cursor part. Playback turns the camera about the same amount. |
| C3 | Hold **W** while turning the camera with the right button, release the right button first, keep walking, then release W. | One Recorded input step. Playback: walks and turns at the same time, W keeps being held after the camera stops. |
| C4 | Click a menu button → turn the camera with right-drag → click another menu button. | Steps: Click … Delay … **Recorded input** … Delay … Click. The total time is about the same as the recording. |
| C5 | First-person / shift-lock game (e.g. RIVALS): move with WASD and aim with the mouse for 5 s. | One Recorded input step with relative part(s) (reason usually "the cursor was hidden"). Playback turns like game mode did before. |
| C6 | During C2–C5 playback press **Esc**. | Stops at once; the right mouse button and W are released (the character stops, the camera stops turning). |
| C7 | Pause (F11 / Pause) during C3, wait 3 s, Resume. | While paused, W and the right button are released; on resume they're pressed again and playback continues. |
| C8 | If a part was detected wrongly (e.g. the camera didn't turn), open the step, set that part to **Relative movement**, play again. | Behaves as expected after the change. Note which game and what the reason text said. |

**Please write down for every game test:** game name, windowed/fullscreen, what the log line said (parts), whether
playback matched, and anything that went wrong. "The game ignored the input" is a useful result too.

---

## Known limits to keep in mind while testing

- Detection is a heuristic. Very low Windows pointer speed, or games that lock the cursor in unusual ways, can be
  guessed wrong → use the per-part drop-down.
- Games that keep snapping a *visible* cursor back to the centre may have the first ~0.15 s of a camera turn detected
  as cursor movement. Games that hide the cursor (most first-person games) aren't affected.
- If you move the mouse during a pause, the next cursor-position event puts it back, but a re-pressed held button is
  pressed wherever the cursor is at that moment.
- Keys in editable steps are sent by key code (like Desktop mode); keys inside Recorded input steps are sent by the
  exact hardware scan code (like Game mode).
