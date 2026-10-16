# Stage 2 – Manual tests (visual detection)

Do these **after** `tools\verify.cmd` is green. For each test write down: passed / failed, what you saw, and the
log lines that start with "Found …", "… didn't appear …", "… is no longer on screen" or "Checked N times …"
(Logs page, or `%LOCALAPPDATA%\SmartMacro\Logs`). Those lines contain the timing numbers we need.

Unit tests prove the matching maths with fake screens. They do **not** prove that real windows or games capture
correctly, so these checks matter.

---

## A. Find image in normal apps (Paint or Notepad)

**A1 – Capture + Test now**
1. Open Notepad. Create a macro, add **Find image**.
2. Click **Capture image**, drag a box tightly around Notepad's **File** menu word (or any icon with detail).
3. Expected: a small preview of the picture appears, size shown (for example "34 × 18 pixels").
4. Click **Test now**. Expected: a window with a screenshot opens, a **green box** is on the File menu, and the text says
   "Found at (x, y), 9x % match". Note the "Capture took … ms, search … ms" numbers.

**A2 – Click it**
1. Set **When it's found** = Click it. Add a **Delay** 500 ms after it. Save, **Play**.
2. Expected: the File menu opens. Log: "Found the image at (…), …% match. Checked 1 time …".

**A3 – It moved**
1. Move / resize the Notepad window so the File menu is somewhere else. Play again.
2. Expected: it still clicks File (that's the point of image search).

**A4 – Search area**
1. Click **Pick area on screen**, drag a box around the top-left part of Notepad. Test now.
2. Expected: found; the search time is lower than for the whole screen.
3. Drag the area somewhere the menu is **not**. Test now → "Not found … The best similarity was …%".

**A5 – Not found / timeout**
1. Minimize Notepad. Set **Keep checking for** = 3000 ms. Play.
2. Expected: after about 3 s an error: "The image didn't appear within 3 s. The best similarity was …".
3. Set **If it's not found in time** = Skip and continue. Play → the macro continues, log says "Continuing."

**A6 – Wait until it disappears**
1. Set **Wait for** = "Wait until it disappears", timeout 10000. Start Play with Notepad visible, then close or
   minimize Notepad within 10 s.
2. Expected: the step finishes right after Notepad disappears ("The image is no longer on screen.").

**A7 – Different color**
1. Capture a colored icon (for example a colored toolbar button in Paint). Change the color setting / theme so the icon's
   shape stays but color changes (or capture a "disabled" grey button and test against the enabled one).
2. Expected with **Ignore color** off: not found, message mentions "right shape but a different color".
   With **Ignore color** on: found.

**A8 – Multi-monitor (if you have two monitors)**
1. Put Notepad on the second monitor (also try a monitor with different scaling). Capture + Test + Play with Click.
2. Expected: the click lands on the right spot on that monitor.

**A9 – Saved and exported**
1. Save the macro, close and reopen SmartMacro: the preview picture is still there.
2. **Export…** the macro and **Import** it again: the imported copy still finds the image.

## B. Find color

**B1 – Pick + Test**
1. Open Paint, draw a filled red square. Add **Find color**, click **Pick from screen**, click inside the square.
2. Expected: the color box and #RRGGBB text update. Pick a search area around the square. Test now → green box on the
   square, "Found color #…".
3. Draw a second, smaller red square in the area. Test now → orange box on the smaller one, and the message says a macro
   uses the biggest.

**B2 – Tolerance and minimum size**
1. Set tolerance 0 and pick an anti-aliased edge color: maybe only few pixels match. Raise tolerance to ~30: more match.
2. Set Minimum size above the square's pixel count → "Not found … no patch had at least … pixels".

**B3 – Track for 3 s (moving marker)**
1. Pick an area shaped like a bar (wide and short), and a color of something you can move inside it (for example drag a
   small red shape in Paint, or move the mouse cursor over a colored progress bar / video seek bar).
2. Click **Track for 3 s** and move the thing during those 3 s.
3. Expected: "Tracked for 3.0 s: N frames (… per second)… Position … X% to Y%. Samples: …" and the numbers follow your
   movement. Write down the frames per second and the capture/search ms.

**B4 – Click found color**
1. Add a **Mouse click** step after Find color with "Click on what the last Find text / image / color step found".
   Play → clicks the middle of the biggest patch.

## C. Find text additions

**C1 – Wait until text disappears**
1. Find text "Loading" (or any text you can make disappear), **Wait for** = disappears. Play, then remove the text.
2. Expected: the step finishes when the text is gone.

**C2 – Old OCR macros**: play an OCR macro made before Stage 2 → it behaves exactly as before.

**C3 – Pause**: during a long Find image/text wait press the pause shortcut for ~5 s, then resume. The timeout should
not count the paused time (new in Stage 2).

## D. Games (only where allowed by the game's rules)

Example ideas (configure, don't expect game-specific code):
- **Grow a Garden:** capture the shop / seed button with Find image, Click it. Test in different camera positions.
- **Fisch:** capture a "Shake" button picture; try Test now while it's on screen. Use **Find color** + **Track for 3 s**
  on the reeling bar to see whether the fish/marker position is read (frames per second and "unclear" counts matter).

Expected / to report:
- Does Test now show the game (not a black picture)? If black: try windowed / borderless mode.
- Found or not, score %, capture and search ms, frames per second during tracking.
- Whether the game reacts to the click (SmartMacro can only send the click; the game decides).

## E. Responsiveness and cost

1. Find image with **Search the whole screen** on, check every 250 ms, timeout 30 s, nothing to find. Play.
2. During the wait, the app and the banner should stay responsive; Esc stops at once.
3. Note the "Checked N times …; capture … ms, search … ms … (busy …%)" line. Also check Task Manager CPU for SmartMacro.
4. Repeat with a small search area: the numbers should drop a lot.

## What to send back

- `artifacts\build.log` / `artifacts\test.log` if verify fails.
- For each test: pass/fail + the log line(s).
- Capture/search times and tracking frames per second (these tell us what Stage 4 can realistically do).
