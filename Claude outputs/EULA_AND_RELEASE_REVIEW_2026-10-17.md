# KuroLogic SmartMacro – EULA simplification and release review (2026-10-17)

Private note. Not legal advice. Nothing was committed, pushed, published or uploaded.

## What changed in `release-docs/`
- `EULA.txt` – replaced the 17-section draft with a 14-section plain-English EULA. The old text is kept in
  `Claude outputs/archive/EULA-long-draft-2026-10-16.txt`.
  Removed: the 14-day cure period, the "zero or a fixed amount" liability cap, the entire-agreement/waiver/translation
  boilerplate, the separate "Updates and support" section and the "AS AVAILABLE"/merchantability list.
  Rewritten from verified source code: section 5 (privacy). Rewritten from Microsoft's license text (fetched today):
  section 6.2 (.NET Library License "terms that protect it at least as much").
- `README.txt` – version filled in as 1.0.0 (matches `SmartMacro.csproj` `<Version>` and `tools\publish.cmd` `VERSION`);
  "Everything stays on your PC" replaced with a statement that points to EULA section 5.
- `THIRD-PARTY-NOTICES.txt` – two [VERIFY] items added to the UNRESOLVED list (extra native DLLs in the Release folder;
  Windows SDK redist wording checked today). Nothing else changed. `DOTNET-THIRD-PARTY-NOTICES.txt` and `LICENSE.txt`
  were not changed.

## Verified privacy facts (from the source, 2026-10-17)
- No networking code in SmartMacro's own source (`*.cs`, `*.xaml`, both `.csproj` files): no HttpClient, WebRequest,
  sockets, hyperlinks or URLs other than XAML schema names. No NuGet packages in the app or Core project.
- Data folder `%LOCALAPPDATA%\SmartMacro` (`Core/Storage/AppPaths.cs`): Macros, Trash, Logs, settings.json.
- Logs: one file per day `smartmacro-yyyyMMdd.log` (`Core/Logging/LogService.cs`), never deleted automatically.
- Test pictures: `Logs\last-ocr-test.png` (`App.xaml.cs` → `FindTextTools`), `last-image-test.png`,
  `last-color-test.png` (`Services/Vision/VisionTools.cs` `TrySave`), overwritten on every test.
- Find image steps store the captured picture inside the macro (`TemplateCodec`).
- Copy diagnostics (`ViewModels/MainViewModel.cs` `BuildDiagnostics`): clipboard only, on click; includes Windows
  version, screen area, OCR status, data folder path (contains the Windows user name) and the last 40 log lines.
- OCR: `Windows.Media.Ocr` (built into Windows).
- Crash reports: Microsoft's license-information-windows.md says single-file .NET runtimes add extra data to Windows
  Error Reporting crash reports. Whether those are sent depends on the user's Windows settings.
- **Mismatch in the app UI (source not changed for this task):** `Views/SettingsView.xaml` line 142 says screen
  captures "are never saved or uploaded", but the Test buttons save PNGs in the Logs folder. Fix the wording before a
  public release (e.g. "are processed on this PC and never uploaded. The last test picture is kept in the Logs folder.").

## Every review marker, classified
| # | Item | Class | Status |
|---|---|---|---|
| 1 | Licensor identity (EULA 1.2) | Factual question for the owner (+ legal question if you want to rely on a pseudonym only) | [PLACEHOLDER] – blocker |
| 2 | "KuroLogic is not a registered company" (EULA 1.1) | Factual – owner's statement; confirm | written as stated by the owner |
| 3 | Commercial use (EULA 2.2) | Owner's product decision | [REVIEW] – default "allowed" |
| 4 | Monetized videos/streams (EULA 2.3) | Owner's decision, matches stated goals | resolved: allowed (confirm) |
| 5 | Reverse engineering (EULA 3d) | Verified wording – "except where the law gives you that right" works under any law | resolved |
| 6 | Data practices (EULA 5) | Verified from source | resolved (re-check if network features are added) |
| 7 | Ko-fi terms (EULA 7) | Owner's decision only if you add paid perks/tiers | resolved for "no perks" |
| 8 | Microsoft .NET Library License end-user terms (EULA 6.2) | Verified requirement text; wording not legally confirmed | resolved in good faith; the indemnity to Microsoft is YOUR obligation, not EULA text |
| 9 | Third-party notices (WPF notices, SDK redist, extra DLLs, versions) | Verified-but-needs-new-build | [VERIFY] in THIRD-PARTY-NOTICES – blocker |
| 10 | No warranty (EULA 8) | Plain wording + "rights that cannot be removed" carve-out | resolved |
| 11 | Liability (EULA 9) | Legal question | [REVIEW] – owner decides after reading (lawyer optional but advised) |
| 12 | Termination (EULA 10) | Simplified, no cure period | resolved |
| 13 | Governing law (EULA 12) | Legal choice; owner is based in the Philippines | [REVIEW] – default Philippine law, no fixed court |
| 14 | Courts/venue | Not necessary for a first free release – left to the normal rules | removed |
| 15 | Contact (EULA 13, README) | Factual – owner must create it | [PLACEHOLDER] – blocker |
| 16 | Effective date (EULA header) | Factual – the real publication date | [PLACEHOLDER] – fill on release day |
| 17 | Software version | Verified 1.0.0 | resolved |
| 18 | README links (download, bug report, YouTube, Discord, Ko-fi) | Factual – owner | [PLACEHOLDER] – download link is a blocker; the others can be removed if you don't have them yet |
| 19 | LICENSE.txt "Open points" + DRAFT | Depends on #1 | blocker until #1 is decided |
| 20 | App/EXE attribution (`AppInfo.Owner = "Riku"`, csproj Company/Copyright "Riku") | Owner approval for a small code change | not a legal blocker, but the EXE says "© 2026 Riku" while the documents say "© 2026 KuroLogic" |
| 21 | Icon origin (`Assets/SmartMacro.ico`) | Factual – owner | open |

## Release readiness (2026-10-17)
Not ready for a public release today. A private test build is possible with `tools\publish.cmd --allow-drafts`.
- The only package in `publish\` (`...-DRAFT-NOT-FOR-RELEASE`, EXE from 2026-10-16 23:04 JST) was built from the
  573-test state, before the 8-bug fix batch and the QoL batch. A fresh publish is required.
- Latest verify run (2026-10-17 02:48 JST, Debug): 0 warnings, 0 errors, 651/651 tests. `publish.cmd` runs the tests
  again in Release; that hasn't been done for the current code.
- Manual tests not done: QOL, 8-bug re-test, Stage 4, Editor UX (no ticks in any checklist; PROJECT_STATUS agrees).
- QoL, bug-fix and Editor UX changes are uncommitted (per PROJECT_STATUS; `git status` couldn't be run from here).
- No clean-PC test yet.

## Update after the owner's answers (2026-10-17, later)
Owner's decisions: licensor = Riku (リク), individual, owner of SmartMacro and the KuroLogic brand (not registered, no
plan to register for now); use allowed anywhere incl. commercial; contact = KuroLogic Discord server (e-mail left out on
purpose); distribution = Discord + Google Drive (no website yet); keep the plain-English liability and Philippine-law
sections; purpose = early test release for feedback; icon generated with Gemini from the owner's own prompt.
- EULA, README, LICENSE: all markers resolved. Added: early-version note (EULA 8.2), feedback clause (EULA 7.3).
  LICENSE now names Riku (リク) as owner; the "Open points" section moved out (AI-assisted-code note stays in the audit).
- THIRD-PARTY-NOTICES: SDK redist and CsWinRT items resolved (package only has lib/net8.0, which the redist list names;
  CsWinRT text compared). DiaSymReader added to the .NET Library License list, msquic / PenImc noted as MIT, from the
  2026-10-16 Release deps.json. Still open: paste WPF notices by hand (WebFetch blocked by robots.txt; not in the
  package), re-read versions after the fresh publish, then remove the DRAFT header + UNRESOLVED section.
- Brand note: a Google search shows another creator already using "KuroLogic" for digital tools on BOOTH. Not a
  blocker for a Discord test release, but a naming/confusion risk before investing in the brand.
- Still open in the app (needs owner approval): Settings "never saved" wording; EXE/About attribution "Riku" /
  "© 2026 Riku".
