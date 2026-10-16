# QoL batch – manual tests

Run `tools\verify.cmd` first (build + all unit tests). Then try these in the app. Tick each one and note anything odd.

## QoL 1 – "When found" / "When not found" (+ If "when true")
- [ ] Add **Find image**, capture a button, set **When found = Click its center**. Move the window so the button is somewhere else, play: it clicks the new spot.
- [ ] Same with **Hold the left button on it for… 800 ms** (e.g. a hold-to-buy button). The step list reads like `Find image (…) on screen → Hold left button 800 ms on center when found (wait up to 10 s)`.
- [ ] **When not found = Skip the next step**: hide the button, play: the step below is skipped, the rest runs.
- [ ] **If** with an image condition, **When true = Click its center**: the click happens before the steps inside the If.
- [ ] If condition set to **is NOT on screen** + a "when true" action: a warning explains it never runs.

## QoL 2 – Repeat while / until
- [ ] Roblox Fisch: **Repeat until** (image of the reeling bar) **is on screen**, inside it **Find image "Shake" → Click its center**, look once, **When not found = Continue**. The loop keeps clicking Shake and ends when the bar appears.

## QoL 3 – Test detection now
- [ ] Find image / Find color: **Test detection now** says `Matched at (x, y) — 94% confidence`; **Show me** moves the cursor onto the match.
- [ ] Find text: shows `Matched! Read text in area: 'OK'` or `Not found. Text read in area: '0K'`.
- [ ] If / Repeat condition: after testing, a line says `Right now this condition is TRUE / FALSE`.
- [ ] Feedback loop: **Test detection now** says where the marker is (%, left/inside/right of the zone) and what it would do. No input is sent.

## QoL 4 – Feedback loop presets
- [ ] **Follow target bar**: pick area, target color, your bar's color → Preview 10 s on `docs/feedback-test-page.html` mode 2. Then a real run.
- [ ] **Click when marker enters zone**: mode 1 of the test page; the loop clicks once when the marker reaches the zone and ends. Try the "Release the held input" variant on a casting meter.
- [ ] "Show all settings (advanced)" shows every raw setting; switching to Custom keeps the values.

## QoL 5 – Mouse click hold / drag
- [ ] **Hold for a duration (ms) = 850** on a casting meter: the button is held ~0.85 s.
- [ ] **Drag to a position**: a Roblox volume slider (start on the knob, end further left), drag time 500 ms. The knob follows smoothly.

## QoL 6 – Simplified recordings
- [ ] Settings → Recording: **Simplify mouse paths** is on. Record 5 shop clicks in Grow a Garden: you get about 5 Click + 4 Delay steps, no Move mouse flood.
- [ ] Record dragging a slider: the drag path is still there (Press, a few Moves, Release).
- [ ] Open an old long recording (e.g. the 289-step one) → **Clean up mouse paths**: far fewer steps; it plays the same. Discard changes undoes it.
