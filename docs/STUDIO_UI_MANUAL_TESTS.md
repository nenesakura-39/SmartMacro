# SmartMacro Studio UI – manual test checklist

Written without a .NET SDK (not compiled yet). Run `tools\verify.cmd` first, then go through this list.

## 1. Look & controls
- [ ] App background is deep obsidian, cards are slate with thin borders, text boxes look raised and glow blue when focused.
- [ ] Library, Logs and Settings pages still look right with the new colours (nothing unreadable).

## 2. Layout
- [ ] Sidebar is narrow (175 px). The chevron at the bottom collapses it to a 60 px icon rail and back. Tooltips name each page.
- [ ] Workflow Editor: one header bar with name, description, Play/Speed/Wait, Discard, Save, ● Record, ▶ Play.
- [ ] The old middle bar ("Ready | Play: … | Record | Play") and the old Activity panel are gone on the editor page,
      but still shown on Library / Logs / Settings.
- [ ] While playing, the header shows Pause + Stop; the drawer pill shows Playing and the progress text.
- [ ] Mini overlay / Minimize pill switches in the header still save to Settings.
- [ ] Bottom drawer: 32 px bar with status pill, problems badge, latest log line. "Expand ▴" (or a click on the
      problems badge) opens Activity log | Problems (120 px, drag the splitter between them).

## 3. Toolbox
- [ ] "Insert: After selected | At end" segmented pill works (new step placement), right-click on a tile still offers both.
- [ ] Tiles in 2 columns under INPUT, VISION & AI, LOGIC & LOOPS; Quick patterns as full-width rows. All tiles add the right step.

## 4. Step cards
- [ ] Each card has a coloured left strip + icon badge (blue input, cyan delay, violet vision, amber logic, green feedback).
- [ ] Selected card: blue border + glow. During playback the running card turns green with "▶ RUNNING".
- [ ] Steps inside If / Repeat are indented 24 px per level with a guide line.
- [ ] Find image shows a small thumbnail; Find color / Feedback loop show colour dots.
- [ ] Disabled steps are dimmed with a DISABLED pill.
- [ ] Drag-and-drop reordering, Shift/Ctrl multi-select, right-click menu and shortcuts still work.

## 5. Properties
- [ ] Header shows badge, title, "Step N" and an Enabled pill switch (turning it off dims the card).
- [ ] Find steps: section cards with uppercase headers (1. WHAT TO FIND … 4. SEARCH TIMING).
- [ ] "Test detection now" is pinned at the bottom for Find text / image / color and Feedback loop; the result
      (and "Show me") appears right under it. Measure / Preview results of a Feedback loop also show there.
- [ ] Inside an If / Repeat condition, the condition's own "Test detection now" button is still in the card.
