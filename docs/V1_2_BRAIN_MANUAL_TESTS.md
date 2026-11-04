# SmartMacro v1.2 "Brain" — Manual Verification Checklist

Use these 5 scenarios to hand-verify v1.2 **Variables & Screen OCR Numbers** (Pillar 3), **Checkpoints, Failure Policies & Watchers** (Pillar 4), **Opt-In Self-Healing** (Pillar 4), and **Sub-Macros & Bundled `.smacro` Export** (Pillar 5) on Windows.

---

## 1. Memory & Reading Numbers Off the Screen ("Farm Until 10,000")

1. Open a macro in the **Editor** and click **🧠 Variables** in the header bar:
   - Add `gold` (`Number`, default `0`) and `runs` (`Number`, default `0`).
2. Add a **Repeat** block (`Repeat Until`, max `20` turns) and set its condition to **🧠 Variable comparison**:
   - `gold` `>= (greater or equal)` `10000`.
3. Inside the Repeat block, add two **🧠 Set variable** steps from **MEMORY & SUB-MACROS**:
   - Step 1: `runs` · `Add (+)` · `Constant value` = `1`.
   - Step 2: `gold` · `Set (=)` · `🔢 Read number on screen (OCR)` → click **Pick area on screen** and drag around a number on Notepad, Calculator, or a game HUD (e.g. type `Gold: 2,500` in Notepad).
4. Add a **Type text** or log-visible step after `End Repeat` using `{gold}` and `{runs}` (e.g. `Done after {runs} runs with {gold} gold!`).
5. Press **Play**, then change the number in Notepad from `2,500` → `6,800` → `12,400`:
   - Verify the **🧠 Variables** popover updates `live:` values in real time and the loop exits automatically as soon as `gold >= 10000`.

---

## 2. Per-Step Failure Policies & Top-Level Checkpoints

1. Add a **📍 Checkpoint** step named `lobby` at the top level of a macro (outside any `If` / `Repeat` blocks).
   - Quick check: drag the **Checkpoint** inside an `If` or `Repeat` block and verify the **Problems** drawer immediately flags an error requiring checkpoints to stay at the top level; move it back out.
2. Add a **Find text (OCR)** step searching for `"Ready"` (`Timeout = 1000 ms`).
3. Under **IF NOT FOUND / TIMED OUT**, test the three new v1.2 resilience policies:
   - **🔁 Retry this step before giving up** (`RetryCount = 2`, `RetryDelayMs = 500`): reveal `"Ready"` on screen during retry #2 and verify the macro recovers and continues.
   - **📍 Jump back to a Checkpoint** (`lobby`): leave `"Ready"` hidden for 1–2 passes, then reveal `"Ready"` and verify playback jumps cleanly back to `lobby` and finishes.
   - **🛡 Run a recovery sub-macro, then continue**: pick a helper macro from your library (e.g., `"Close Popups"`) and verify it runs on timeout before continuing.

---

## 3. Macro-Level Watchers (Surprise Popup / Disconnect Guard)

1. Open **🛡 Watchers** in the Editor header bar and click **+ Add watcher**:
   - Name: `Popup closer`, Condition: **Text is visible (OCR)** = `"Dismiss"` (or `"Close"`), Action: **Click the found spot**, Check every `1000 ms`, Cooldown `3000 ms`.
2. Add a few normal **Wait** / **Mouse** steps to the macro so it runs for ~8–10 seconds.
3. Press **Play** and bring up a window/dialog or Notepad line showing `"Dismiss"`:
   - Verify SmartMacro spots `"Dismiss"` between steps, clicks it automatically, logs `🛡 Watcher "Popup closer" triggered`, respects the cooldown, and finishes the main macro uninterrupted.

---

## 4. Sub-Macros (`Call macro`) & Self-Contained `.smacro` Bundle Export/Import

1. Create and save a helper macro named **`Compute Bonus`**:
   - Declare variables `baseAmount` (`Number`, `0`) and `bonusResult` (`Number`, `0`).
   - Add **Set variable**: `bonusResult = {baseAmount}`, followed by **Set variable**: `bonusResult + 50`.
2. Create a main macro named **`Main Routine`**:
   - Declare variable `total` (`Number`, `100`).
   - Add **📦 Call macro** → select **`Compute Bonus`**:
     - Under **Input parameters**, add `baseAmount` ← `{total}`.
     - Under **Output values**, add `bonusResult` → `total`.
   - Save and press **Play**: verify `total` becomes `150` in **🧠 Variables**!
3. Go to **Library**, select **`Main Routine`**, and click **Export**:
   - Open the exported `.smacro` file in a text editor and verify `"schemaVersion": 16` and `"subMacros"` contains **`Compute Bonus`** automatically.
4. Click **Import** and choose that `.smacro` file:
   - Verify both the main macro and its bundled sub-macro import cleanly (with collision-safe names and remapped IDs) and run immediately.

---

## 5. Opt-In Smart Self-Healing Banner (Never Silent Mutation)

1. Record or create a **Mouse click** step with **🧭 Smart Intent** enabled (e.g., clicking a button in **Calculator** or **Notepad**).
2. Move the target window by **at least 50+ pixels** across your screen and press **Play**:
   - Smart Intent relocates and clicks the moved button.
3. Look at the **Editor** after playback finishes:
   - Verify the step's saved `(X, Y)` coordinates were **not** silently overwritten during playback.
   - Verify the **🩹 Smart Self-Heal Suggestion** banner appears above the step list showing `Step #N: (oldX, oldY) → (newX, newY)`.
4. Click **Apply** (or **Apply all**):
   - Verify the step's coordinates update, **Ctrl+Z** cleanly undoes the update if desired, and **Save** persists it only when you choose.
