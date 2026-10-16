# Editor UX – Manual tests (Pick position, mouse steps, adding and moving steps)

Do these after `tools\verify.cmd` is green. Tick a box when it works. If something doesn't work, write down the test
number, what you did and what happened (a screenshot helps).

You need: SmartMacro and Notepad. For the scroll tests, any long web page or a long text file in Notepad.

---

## A. Pick position

- [ ] **A1 Move mouse.** Add a **Move mouse** step. Under *Destination*, click **Pick position**.
  - SmartMacro hides and every monitor gets a dim overlay with an orange crosshair.
  - Next to the cursor you see `X …, Y …`, and the numbers change as you move.
  - Click a spot. SmartMacro comes back with X and Y filled in, and the box says "Position set to (…)".
- [ ] **A2 The click doesn't reach the app below.** Open Notepad and put it under the spot you pick. Pick a point on
  Notepad's **X (close) button**. Notepad must stay open: the overlay takes both the press and the release.
- [ ] **A3 Esc cancels.** Note the current X/Y, click **Pick position**, then press **Esc** (or right-click). You see
  "Picking was cancelled. The position wasn't changed." and X/Y are the same as before.
- [ ] **A4 Edit by hand.** After picking, type a different X. The new value is kept and the Problems list updates.
- [ ] **A5 Use cursor position.** Click **Use cursor position (3 s)** and move the mouse to a spot within 3 seconds.
  The countdown shows in the box, then X/Y become where the cursor was.
- [ ] **A6 Two monitors / scaling (if you have them).** Pick a point on the second monitor, then play a macro with
  that Move mouse step. The cursor lands on the same spot. If the second monitor is left of or above the main one, X
  or Y is negative; that's correct. If your monitors use different scaling (100 % / 150 %), test one point on each.
- [ ] **A7 Pick color still works.** In a Find color step, **Pick from screen** now also shows the crosshair, and
  picks when you release the button.

## B. Mouse click

- [ ] **B1 Where to click.** Add a **Mouse click** step. *Where to click* lists: "At a chosen position",
  "Where the cursor already is", and "On what the last Find … step found". X/Y and the pick buttons are only shown
  for "At a chosen position".
- [ ] **B2 Click a picked spot.** Open Notepad and type a few lines. In a new macro, add a Mouse click and pick a spot
  in the middle of the text, then add Type text `HERE`. Click somewhere else in Notepad, then play. `HERE` is typed
  where you picked.
- [ ] **B3 Picking switches the mode.** Set *Where to click* to "Where the cursor already is", then use **Pick
  position**. The mode switches back to "At a chosen position" with the new X/Y.
- [ ] **B4 Bad coordinates.** Type X = `500000`. The Problems list shows an **error** ("far outside any screen") and
  Save is refused. Type X = `5000` on a 1920-wide screen: only a **warning** (it may be right on another monitor setup).

## C. Scroll wheel

- [ ] **C1 Direction and notches.** Add a **Scroll wheel** step. Set Direction = Down and Number of notches = 3.
  The step list says "Scroll down 3 notches …".
- [ ] **C2 Scroll at a chosen position.** Open a long web page (or a long text in Notepad). Set *Scroll position* to
  "At a chosen position", pick a spot on the page, and play. The cursor jumps there and the page scrolls down.
  Then change Direction to Up and play again: it scrolls up.
- [ ] **C3 Where the cursor already is.** Set *Scroll position* to "Where the cursor already is", put the mouse over
  the page and press the play shortcut. It scrolls under the cursor, and the cursor doesn't move.
- [ ] **C4 Exact amount.** Open **Exact amount (advanced)**. 3 notches down shows `-360`. Type `-60`: the text under
  the notches says "Exact amount: 60 (= 0.5 notches…)".
- [ ] **C5 Sideways.** Direction = Right in a wide spreadsheet or web page scrolls sideways (only if the app supports
  sideways scrolling).

## D. Adding steps

- [ ] **D1 At the end.** Open a macro with 20+ steps and select step 3. In the toolbox, choose **New steps go: At the
  end** and click **Delay**. The new Delay is the last step, it's selected, the list scrolls down to it, and the
  status bar says "Added Delay as step N (at the end)".
- [ ] **D2 After the selected step.** Switch to **After the selected step**, select step 3, then add **Key press**.
  It becomes step 4.
- [ ] **D3 Right-click in the toolbox.** Right-click **Mouse click** in the toolbox and choose **Add to end** (or
  **Insert after selected**). It goes there whatever the radio buttons say.
- [ ] **D4 Nothing selected.** In an empty macro, or after deleting the last step, adding a step puts it at the end.
- [ ] **D5 If / Repeat.** "At the end" + **If** adds If and End If as the last two steps.

## E. Moving steps

- [ ] **E1 Move to top / bottom.** Select a normal step in the middle and click the **Move to bottom** button (⌄,
  right of the down arrow) or press **Alt+End**. It becomes the last step and stays selected. **Alt+Home** moves it
  to the top.
- [ ] **E2 Right-click menu.** Right-click a step. The menu has Move to top / up / down / to bottom, Wrap in If /
  Repeat, Duplicate and Delete.
- [ ] **E3 Drag one step.** Drag a step with the mouse. A blue line shows where it will land; drop it there. Near the
  top or bottom edge of the list, the list scrolls while you drag.
- [ ] **E4 Drag a group.** Click step 2, Shift+click step 4, then drag one of the three (press on an already-selected
  step). All three move together in the same order and stay selected.
- [ ] **E5 Blocks move as a whole.** Make `Repeat … End Repeat` with 2 steps inside. Select only **Repeat** and
  choose **Move to bottom** (or drag it). The whole block moves, and the status says "whole If / Repeat blocks were
  moved together".
- [ ] **E6 Unsafe moves are refused.** Make `Repeat`, `End Repeat` (nothing inside). Select **Repeat** and press
  **Alt+Down**. Nothing changes and the status bar explains that it would break the blocks. Moving a normal step into
  or out of a block with Alt+Up / Alt+Down still works.
- [ ] **E7 Save + reopen.** After some moves, save, restart SmartMacro and open the macro. Order, comments,
  disabled steps and all settings are as you left them.

## F. Old macros

- [ ] **F1** Open macros made before this update (with clicks, scrolls, moves, recordings, If / Repeat, feedback
  loops). They open without new errors, show the right values (e.g. an old scroll of `-240` shows Down, 2 notches) and
  play exactly as before.
