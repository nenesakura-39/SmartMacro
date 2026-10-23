# AAA Studio Upgrade – manual tests

Parts covered: **Smart Click Snapshots** (Part 1), **"Last found target" + If "When found, also:" + click / move on the
last found spot** (Part 2), **Pause F8 / speed selector / Humanize** (Part 3, sections G–I), **target-lock overlay +
mini-HUD** (Part 4, sections J–K) and **✨ Auto-Fix Block Structure** (Part 5, section L). Part 6 is the unit tests
(`tools\verify.cmd` runs them).

Before you start: run `tools\verify.cmd`. If the build or tests fail, send `artifacts\build.log` / `artifacts\test.log`.
Then start SmartMacro. Tick each box when it works; write a short note when it doesn't.

---

## A. Smart Click Snapshots while recording

1. [ ] Settings → Recording: the switch **"✨ Smart Click Snapshots: save a small picture of what I click"** is on.
2. [ ] Recording mode **Smart**. Open Notepad (or a browser). Record: click the **File** menu, click somewhere in the text,
   press Esc. Stop recording.
3. [ ] The Activity log says **"✨ Smart Click Snapshots: N click(s) remember what they clicked…"**.
4. [ ] In the step list, the recorded Mouse click steps show a **small picture** (28 px) of what was clicked.
5. [ ] Select a click step: the Properties panel shows the **✨ SMART CLICK SNAPSHOT** card with the 56×56 picture (a small
   crosshair in the middle = where you clicked) and "What you clicked when recording: 56 × 56 px, clicked in the middle".
6. [ ] Click very close to a screen edge while recording: the card says "clicked at (x, y) inside it (near a screen edge)".
7. [ ] Repeat step 2 in **Desktop** mode (Settings → Advanced): the clicks have pictures too.
8. [ ] **Game** mode / Roblox camera turning: no pictures for camera clicks (that's expected).
9. [ ] Turn the Settings switch off, record again: no pictures, no card.
10. [ ] Save the macro, close SmartMacro, open it again: the pictures are still there.

## B. Smart-Aim

1. [ ] Record a click on a button in a window (e.g. a desktop app's toolbar button). Turn on
   **"✨ Smart-Aim: find & click this visual target if it moved (within 120 px)"**. A ✨ appears on the step's picture in the list.
2. [ ] Move the window **about 40 px right and 30 px down**. Play. The click still hits the button.
   Activity log: "Smart-Aim: the clicked target moved by (+40, +30) px; clicking it at (…)".
3. [ ] Move the window **more than 120 px** away. Play: the log says "wasn't found within 120 px … clicking the recorded spot",
   and it clicks the old X / Y (nothing else happens, the macro doesn't stop).
4. [ ] Raise "Search distance (px)" to 300: now step 3 finds it again.
5. [ ] Set "Match accuracy (%)" to 30: the Problems list shows an error (must be 50–100 %).
6. [ ] Switch "Where to click" to "Where the cursor already is" with Smart-Aim on: a warning says Smart-Aim only works with
   "At a chosen position".

## C. 🔍 Convert Step to Full 'Find Image & Click'

1. [ ] Select a recorded click with a picture. Click **🔍 Convert Step to Full 'Find Image & Click'**.
2. [ ] The step becomes a **Find image** step: the picture is the one from the card, "Where to look" = All monitors,
   "Wait up to" = 5 s, "When found" = Click (Right-click / Double-click / Hold for those kinds of clicks).
3. [ ] Play with the window moved anywhere on screen: it finds and clicks the same spot of the button.
4. [ ] **Ctrl+Z** puts the original Mouse click back (with its picture).
5. [ ] For a "Press down only" / "Release only" / "Drag" step the button is greyed out and the hint explains why.

## D. If → "When found, also:" (Tic-Tac-Toe style)

1. [ ] Add an **If**: Look for **Image**, True when it **is on screen**, capture a picture (e.g. a game board or a dialog).
2. [ ] Under **"When found, also:"** choose **Click the found spot**. Offset X / Y fields appear.
3. [ ] Click **🎯 Pick offset from screen**: SmartMacro hides and finds the image (the hint at the top of the screen says where),
   then click the spot you want (e.g. one cell of the board). The status says "Offset set to (+…, +…), measured from the target SmartMacro found…".
4. [ ] Play: the click lands on that spot, then the steps inside the If run. Move the window and play again: it still hits.
5. [ ] Try **Double-click / Right-click / Move the mouse to the found spot** too.
6. [ ] Switch "True when it" to **is NOT on screen** with no action set: the "When found, also:" section disappears.

## E. Mouse click / Move mouse → "On the last found spot (+ offset)"

1. [ ] Inside the If from D (action back to "Just run the steps inside the If"), add a **Mouse click** with
   "Where to click" = **On what the last Find step / If condition found (+ offset)**. Offset X / Y fields + the
   🎯 button appear. Pick an offset (e.g. the cell right of the middle). Play: it clicks there.
2. [ ] Add a second Mouse click with a different offset (another cell). Both cells get clicked, wherever the board is.
3. [ ] Add a **Move mouse** step with "Where to move" = **To what the last Find step / If condition found (+ offset)**:
   the cursor goes there (no click).
4. [ ] Move mouse with **By an offset from where the cursor is**, Offset X = 100: the cursor moves 100 px right of wherever it is.
5. [ ] A Move mouse "to the last found spot" as the very first step: the Problems list warns that nothing is found before it,
   and playing it stops with "…goes to the last found spot, but no Find … has found anything yet…".
6. [ ] Without anything on screen to find, 🎯 Pick offset asks you to click the target's **center** first, then your spot.

## F. Old macros

1. [ ] Open a macro saved before this update: clicks "on the last found spot", If "when true" actions and positions work
   exactly as before.
2. [ ] Library → Export a macro with a recorded click picture, Import it again: the picture and Smart-Aim setting survive.

Send back: anything that failed (screenshot + the Activity log lines), and how the Smart-Aim / Pick offset felt in a real game.

---

## G. Pause / Resume (F8)

1. [ ] Settings → Global Hotkeys: "⏸ Pause / resume (while playing)" shows **F8** (if you never changed it before).
2. [ ] With nothing playing, press F8 in Notepad / a browser: F8 does what it normally does (SmartMacro doesn't take it).
3. [ ] Make a macro: Key press **W – Hold down**, Delay 5 s, Key press **W – Release**, Move mouse. Play it in Notepad.
   While W repeats, press **F8**: typing stops at once (W is released), the editor button says **▶ Resume (F8)**,
   the HUD dot turns **amber** and the timer stops.
4. [ ] Wait ~10 s, press **F8** again: W is held again and the rest of the 5 s delay still runs (not skipped), then it releases.
5. [ ] The editor's **⏸ Pause (F8)** / **▶ Resume (F8)** button next to ⏹ Stop does the same.
6. [ ] Esc while paused stops playback; nothing is pressed again.

## H. Speed selector

1. [ ] The editor header has a **Speed** drop-down: 0.5× / 1× / 1.5× / 2× / 5×.
2. [ ] A macro with Delay 2 s between two clicks: at **2×** the wait is ~1 s, at **0.5×** ~4 s.
3. [ ] At **5×** in Roblox (or any game), recorded clicks / key taps still register (they're held at least 25 ms).
4. [ ] Change the speed **while the macro plays** (repeat it 0 = forever): the log says "Playback speed changed to 2× …" and
   it gets faster from the next step.
5. [ ] An old macro saved with speed 3 shows **3×** in the list (an extra entry); saving keeps it.

## I. 🎲 Humanize

1. [ ] Turn **🎲 Humanize** on. Play a macro with 20 clicks on the same spot: the clicks land within ±2 px.
2. [ ] Delays of 1 s vary a little (±4 %); recorded 16 ms mouse points keep their exact rhythm.
3. [ ] A Move mouse step with **Smooth glide** curves slightly (a small arc) instead of a ruler-straight line.

## J. Target-lock overlay

1. [ ] Turn on **With overlay** (editor header). Play a macro with a Find image step (or an If image condition, or a
   Smart-Aim click): a **neon box with corner brackets** and a **"🎯 98% Match · Find image"** badge flashes on what was
   found for about a third of a second.
2. [ ] It never takes focus from the game (keep typing / the game keeps running), and a Find image step in a fast loop never
   "finds" the reticle itself.
3. [ ] Turn **With overlay** off: no reticle, no HUD.

## K. Mini-HUD

1. [ ] While playing, the top-right pill shows: a **pulsing green dot**, **Run #1 / ∞** (or Run 1 / 3), a **00:24** timer,
   **Step 4/12: Find image**, what it's doing, and a thin progress bar.
2. [ ] Paused: the dot is **amber**, the step line says paused, the timer stops, the button shows **▶ F8**.
3. [ ] Click **⏸ F8** on the HUD with the mouse: pauses / resumes. Click **⏹ Esc**: stops.
4. [ ] A macro that clicks *under* the HUD (top-right corner of the screen) still clicks the app behind it — the macro's own
   clicks never press the HUD buttons.
5. [ ] Recording: the dot is **red**, the badge says **● REC**, the timer counts, no buttons, and clicking where the HUD is
   still records the click on the app below.

## L. ✨ Auto-Fix Block Structure

1. [ ] Make a macro: **End If**, **End If**, **If** (anything), Mouse click, **End If**. A red banner appears above the step
   list: "The If / Repeat blocks don't match up. Step 1: This End If has no If before it…" with **(+1 more problem)**.
2. [ ] Click **✨ Auto-Fix Block Structure**: the two stray End Ifs are gone, the banner disappears, the status says
   "✨ Fixed the blocks: Removed 2 orphan 'End If' steps…", and the Activity log has the same line.
3. [ ] **Ctrl+Z** brings the two End Ifs back (and the banner); **Ctrl+Y** fixes it again.
4. [ ] Add an **If** and a **Repeat** with steps inside but no End If / End Repeat: Auto-Fix adds **End If** then
   **End Repeat** at the very end (inner block closed first). The steps inside stay where they were.
5. [ ] Two **Else** steps in one If: Auto-Fix removes the second one.
6. [ ] Overlapping blocks (**Repeat**, **If**, click, **End Repeat**, delay, **End If**): after Auto-Fix it's Repeat, If,
   click, End If, End Repeat, delay — no step lost.
7. [ ] Open the old **tic tac toe auto v1** (the one with the two stray End Ifs at steps 1–2): one click fixes it, and
   then it can be saved and played.
8. [ ] A macro whose blocks are fine never shows the banner. Nesting more than 8 blocks deep shows the problem in the
   Problems list but no Auto-Fix button (that one can't be guessed).
