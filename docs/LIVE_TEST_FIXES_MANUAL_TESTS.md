# Live-test fixes (7 bugs from testing b408b36) – manual checklist

Written without a .NET SDK, so first run `tools\verify.cmd`. If the build or a test fails, send `artifacts\build.log` /
`artifacts\test.log`. Then go through the list below. Each item says what to do and what you should see.

## 1. Window layout (windowed mode)
1. Close SmartMacro, start it again. It should open at about 1340 × 860.
2. Click the restore button so the window is NOT maximized, and make it small (it stops at 1024 × 680).
3. Open a macro. Check:
   - The header is two short rows (name + Save on top, description + Play / Speed / Wait below).
   - The Problems bar is a single line when there are no problems.
   - Add step / Workflow steps / Properties get most of the height (at least ~360 px). The Add step buttons are
     visible right under "Add new steps at the end" and you can scroll them.
4. Drag the gaps between Add step | Workflow steps | Properties left / right: the panels resize.
5. The Activity panel is small. Drag the little grip on its top edge up: it gets taller; down: shorter.

## 2. Properties panel text
1. Add a Feedback loop step. Long labels ("Captures per second (max)", "Min time between changes (ms)") wrap and are
   fully readable. "Max runtime", "When the time is up", "Target missing for", "Then" are stacked full-width, so the
   drop-downs show their whole text.
2. "Done when a condition is true (e.g. a "caught" …)" and "Failed when …" checkboxes wrap onto a second line.
3. Add a Repeat → While → Look for: Image. "Look for" and "True when it" are full-width; "Capture image" is fully visible.

## 3. Mouse wheel over drop-downs
1. Select a Find image step. Put the mouse over the closed "When found" drop-down and scroll the wheel.
   The Properties panel scrolls; "When found" does NOT change.
2. Open the drop-down and scroll: the open list scrolls as usual.

## 4. Find image: small text and see-through game buttons
1. Notepad: type "Hi". Add Find image, capture just "Hi", search area around Notepad. Press "Test detection now" 5
   times: it should match every time and never say "coarse search at 1/4 size".
2. Roblox Fisch: capture the SHAKE button over the pier. Turn on
   **"Ignore background / match bright text and edges (for see-through game UI)"**. Test while a SHAKE button is over
   water: it should match (85 %+). 📏 Send the confidence numbers you see over wood and over water.

## 5. Fisch SHAKE loop
1. Repeat → While → Image (SHAKE) is on screen. New field **"Keep checking before leaving (ms)"** shows 800.
   Inside: Find image SHAKE → Click its center (optional: "Move the cursor out of the search area after clicking").
2. Play. It should keep clicking every SHAKE (also the 3rd, 4th…) without you moving the mouse, and only leave the loop
   ~0.8 s after the last one is gone (the log says "kept checking for 800 ms").
3. "When found: Double-click" clicks twice with a visible gap (not instantly).

## 6. Smooth mouse playback
1. Settings → "Smooth mouse movement during playback" is ON.
2. Record: open Settings in some app, click a few buttons, drag a volume slider 50 % → 80 % → 50 %, drag a Chrome
   scrollbar. Play it back: the cursor glides (no teleporting), every click lands, the slider ends at 50 %.
3. Turn the setting off and play again: moves jump like before (for comparison).

## 7. "Test detection now" hides SmartMacro briefly
1. Put the area you test behind the SmartMacro window. Press "Test detection now".
   SmartMacro disappears for a moment (no minimize animation) and the picture shows the app behind it, not SmartMacro.
   SmartMacro is back before the result window opens.
