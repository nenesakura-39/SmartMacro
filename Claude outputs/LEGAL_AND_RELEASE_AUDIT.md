# KuroLogic SmartMacro – Attribution, licensing and release audit (draft)

Prepared 2026-10-16. I'm not a lawyer, and nothing in this document is legal advice. It sorts out what I verified, what I couldn't
verify, and what you need to decide. Nothing was committed, pushed or published.

---

## 1. Repository and attribution audit

### Where "Riku" appears today (verified in the source)

| Place | What users see | Where it comes from |
|---|---|---|
| Settings → About card | `Owner & developer: Riku` | `Views/SettingsView.xaml` line ~46 binds `OwnerText`; `ViewModels/SettingsViewModel.cs` line 66 builds it from `AppInfo.Owner` |
| Sidebar under the version | `by Riku` | `MainWindow.xaml` line 92 → `ViewModels/MainViewModel.cs` line 290 → `AppInfo.Owner` |
| EXE file properties (right-click → Properties → Details) | Company `Riku`, Copyright `© 2026 Riku` | `SmartMacro.csproj` lines 16-18 (`Company`, `Authors`, `Copyright`) |
| README / docs | "Owner & developer: **Riku**", "© 2026 Riku" | `README.md` and `PROJECT_STATUS.md` |

- The name is **hardcoded** in one constant: `Services/AppInfo.cs` → `public const string Owner = "Riku";`. It isn't stored in settings.json or any other config.
- The app currently shows **"Riku" without "(リク)"**. Your brief says Settings shows "Riku (リク)", but the code doesn't contain "リク" anywhere.
- "KuroLogic" isn't in the app yet. The product name is just `SmartMacro` (`AppInfo.Name`).
- I didn't change any UI or source file for this task.

### Recommended (smallest) change, waiting for your approval

1. In `Services/AppInfo.cs`, change `Owner` to `Riku (リク)`, add `Brand = "KuroLogic"`, change `Name` to `KuroLogic SmartMacro`, and add
   `Copyright = "Copyright © 2026 KuroLogic. All rights reserved."`.
2. In the Settings About card, show four lines: "KuroLogic SmartMacro", "Version x", "Developer: Riku (リク)" and the copyright line. Add
   two small buttons, **License (EULA)** and **Third-party notices**, that open `EULA.txt` / `THIRD-PARTY-NOTICES.txt` from the app
   folder in Notepad. Keep the sidebar as "by Riku (リク)".
3. In `SmartMacro.csproj`, set `Company = KuroLogic`, `Authors = Riku (リク)`, `Product = KuroLogic SmartMacro` and
   `Copyright = Copyright © 2026 KuroLogic. All rights reserved.`.
4. In `tools\publish.cmd`, copy `EULA.txt`, `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` and `RELEASE_README.txt` (as `README.txt`) into
   the package instead of the developer `README.md`.

These four changes touch about 5 files and add no new screens. A first-launch "I agree" dialog is a separate, optional step (see section 5).

### Real name
I searched the repository. Your legal name doesn't appear anywhere in the source or docs. Local build logs (`release\test.log`,
`artifacts\*.log`, `*.trx`) contain your Windows user-folder path (`C:\Users\sakur\…`). They're not part of the package, but don't
upload them.

## 2. Repository visibility (couldn't be fully verified)

- The GitHub API isn't enabled for this session, so I couldn't read the visibility setting.
- An anonymous `git clone` of `https://github.com/nenesakura-39/SmartMacro` asked for a login. GitHub does that for private (or
  non-existent) repositories, which suggests the repo is **private**, but it doesn't prove it.
- To check: open the repo on GitHub. A **Private** label next to the name means only you and invited collaborators can see the code.
- If the repo was ever public, anyone could have cloned or forked it then. Making it private later doesn't remove those copies.

## 3. Third-party dependency report

The app and Core projects have **no NuGet `PackageReference`s** (verified in the `.csproj` files). The release is published as a
self-contained single-file EXE (`tools\publish.cmd`), so it **bundles** the components below.

| Component | Version | License | Verified source | Distribution obligations | Status |
|---|---|---|---|---|---|
| .NET runtime (Microsoft.NETCore.App) | 10.0.x (unverified; check the build) | MIT + extra terms for Windows builds + .NET third-party notices | [dotnet LICENSE.TXT](https://github.com/dotnet/dotnet/blob/main/LICENSE.TXT); [license-information.md](https://source-hub.etos-pro.ru/dotnet/core/src/branch/main/license-information.md) (mirror of dotnet/core) | Keep the copyright and MIT text, ship the .NET third-party notices, follow the Windows extra terms. Applies to the runtime only, not your code. | MIT verified. **The Windows extra terms text couldn't be fetched (unverified).** |
| WPF / Windows Desktop runtime | 10.0.x (unverified) | MIT (repo); Windows-only native parts under the .NET Windows terms | [github.com/dotnet/wpf](https://github.com/dotnet/wpf) | Same as above | MIT verified. Native-part terms unverified. |
| Microsoft.Windows.SDK.NET.dll (C#/WinRT projection; comes from the TFM `net10.0-windows10.0.19041.0`, needed for OCR) | 10.0.19041.x (unverified) | Microsoft Windows SDK license | [Windows SDK redist lists](https://learn.microsoft.com/en-us/legal/windows-sdk/redist); [fuget package page](https://www.fuget.org/packages/Microsoft.Windows.SDK.NET.Ref/10.0.20348.18) (license URL aka.ms/WinSDKLicenseURL) | The redist list says it may be distributed **unmodified, as part of your program**, to call WinRT APIs. | Allowed per the redist list. The list names `lib/net8.0` and `lib/net6.0`, so **confirming the .NET 10 build is covered is still open**. Note: an informal Microsoft Q&A answer said "you may not redistribute it" ([link](https://learn.microsoft.com/en-us/answers/questions/1696668/can-i-redistribute-microsoft-windows-sdk-net-dll)), but the official redist page and [a Microsoft maintainer](https://github.com/microsoft/WindowsAppSDK/discussions/4368) point to the SDK terms and redist list. |
| WinRT.Runtime.dll (C#/WinRT) | unverified | MIT | [github.com/microsoft/CsWinRT](https://github.com/microsoft/CsWinRT); also on the redist list | Keep the copyright and MIT text | Verified |
| xunit 2.9.2, xunit.runner.visualstudio 2.8.2, Microsoft.NET.Test.Sdk 17.12.0 | as listed | (Apache-2.0 / MIT from memory, not re-verified) | – | **None.** Test-only, not shipped. | Not distributed |
| Windows OCR, GDI, SendInput; Segoe UI / Fluent Icons / MDL2 fonts | part of Windows | Windows license | – | Not bundled; used from the user's Windows | No action |
| `Assets/SmartMacro.ico` | – | – | – | – | **Origin to confirm.** Project notes say it was made for this project (blue square with a lightning bolt). Confirm it isn't copied from an icon pack. |

Conclusions:
- None of the shipped components requires you to publish SmartMacro's source code. MIT and the Windows SDK redist terms allow use
  in a closed-source, free app, as long as the notices are kept. These licenses apply to those components only.
- `dotnet list package --include-transitive` couldn't run here because there's no .NET SDK in my environment. You can run it on
  your PC (see section 8).

## 4. Ownership questions (unresolved, for you to decide)

1. **Who is the legal licensor?** "KuroLogic" is a brand. As far as I know it isn't a registered company, and using a brand or alias
   doesn't create a legal entity. In law, the copyright owner of your own code is normally **you, the person**. A contract (the
   EULA) needs a real party behind it, and in a dispute you may have to identify yourself. The EULA leaves this as a placeholder.
   Common options: "the individual developer known as Riku (リク), publishing as KuroLogic", or registering a business name later.
2. **AI-assisted code.** Much of SmartMacro's code was written by an AI coding assistant at your direction, as the project notes say.
   - Claude's consumer terms generally give the user rights to the outputs, but whether copyright protects AI-generated code
     depends on the country. Some countries, for example the US, require human authorship, and your selection, arrangement,
     direction and edits are what count.
   - The EULA still works as a license contract either way, but how strong the copyright is in purely AI-written parts is uncertain.
   - Get advice if this matters to you. Don't claim more than the notice says.
3. **Contributors:** there's no sign of other human contributors or copied third-party code in the repository. The comments and
   history show no external sources, but I can't see the full git history from here. Confirm nobody else contributed code or assets.
4. **Trademarks:** "KuroLogic" and "SmartMacro" aren't checked for existing trademarks. Other products named "SmartMacro" may exist.
   A trademark search is advisable before investing in the brand.
5. **Year:** keep 2026 (first publication) and add a range for later releases ("2026-2027"). Don't replace the original year.

### What the terms mean
- **Developer attribution:** credit for who made it ("Developer: Riku (リク)"). It's a fact statement, not a legal right.
- **Copyright ownership:** the legal right in the code and design. It belongs to a person or legal entity, normally the author or
  their employer.
- **Public brand name:** the name you publish under ("KuroLogic"). It's a label, not a person.
- **Licensor:** the person or entity granting the EULA. It must be someone who really holds the rights.
- **Third-party IP:** .NET, WPF, the Windows SDK and so on. You only get the rights their licenses give, and you can't relicense them.
- A copyright notice tells people who claims the rights. It doesn't prove ownership on its own.

## 5. Showing and accepting the EULA

| Method | What it does |
|---|---|
| `EULA.txt` in the ZIP | Makes the terms available. Weakest evidence that a user agreed. |
| Link on the download page ("By downloading you agree…") | Better notice before download |
| About → "License (EULA)" button | Users can read it any time after install |
| Installer with an "I accept" page | Explicit acceptance (you currently ship a ZIP, not an installer) |
| First-launch dialog "I agree / Exit" (version stored in settings.json) | Explicit acceptance without an installer |

Including a file only gives notice. A click-to-accept screen is much better evidence of agreement, but nothing is guaranteed to be
enough in every country.

**Starter recommendation:**
- Ship `EULA.txt` in the ZIP.
- Link the EULA on the download page.
- Add the About buttons from section 1.
- Later, add a small first-launch "I agree" dialog (about 1 new window, a settings field `AcceptedEulaVersion`, and a check in
  `App.xaml.cs`). I'll implement this only if you approve it.

## 6. Public distribution plan

- Keep `nenesakura-39/SmartMacro` **private**. Don't create GitHub Releases there. Releases on a public repo always offer "Source
  code (zip)" downloads, so releasing from the source repo would expose code if it were public.
- Create a **separate public repo**, for example `KuroLogic/SmartMacro-Releases`, or use another download site (itch.io, a website, a
  Google Drive folder). It should contain only `README.md`, `EULA.txt`, `THIRD-PARTY-NOTICES.txt`, `LICENSE.txt`, and Releases with
  the ZIP. Its auto-generated "Source code" archive then only contains those text files.
- The ZIP should contain `SmartMacro.exe`, `EULA.txt`, `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` and `README.txt`. Nothing else: no
  .pdb files, no logs, no docs.
- A private repo doesn't stop decompiling. .NET programs can be decompiled into readable C#. The EULA prohibits it where the law
  allows, and an obfuscator is optional (it makes decompiling harder, not impossible). Never put secrets or tokens in the code.

## 7. Copy-and-paste notices

### YouTube (description)
```
⬇️ KuroLogic SmartMacro (free): [OFFICIAL DOWNLOAD LINK]
📄 License (EULA): [EULA LINK]
📄 Third-party notices: [NOTICES LINK]
☕ Support KuroLogic on Ko-fi (optional): [KO-FI LINK]
Only download SmartMacro from the official link above. Use macros only where the game/app rules allow it.
SmartMacro © 2026 KuroLogic · Developer: Riku (リク)
```

### Discord (#releases)
```
**KuroLogic SmartMacro [VERSION] is out!**
⬇️ Official download: [OFFICIAL DOWNLOAD LINK]
📄 EULA: [EULA LINK] · Third-party notices: [NOTICES LINK]
By installing SmartMacro you agree to its EULA (separate from this server's rules).
Please share the official link instead of re-uploading the file.
☕ Ko-fi support is completely optional: [KO-FI LINK] — SmartMacro stays free either way.
```

### Ko-fi (page / about)
```
Hi! I'm Riku (リク), the developer behind KuroLogic.
KuroLogic makes proprietary software like KuroLogic SmartMacro, a free Windows automation tool.
SmartMacro is intended to remain free to download and use. Supporting KuroLogic here is completely voluntary.
Your support helps me keep developing it, but it doesn't buy the software, transfer any ownership,
or change the SmartMacro EULA. Thank you! 💙
Download: [OFFICIAL DOWNLOAD LINK] · EULA: [EULA LINK]
```

## 8. Recommended file locations

| File | In the private repo | In the public package / download repo |
|---|---|---|
| `LICENSE.txt` | repo root | ZIP root + public repo root |
| `EULA.txt` | repo root | ZIP root + public repo root (link target) |
| `THIRD-PARTY-NOTICES.txt` | repo root | ZIP root + public repo root |
| `RELEASE_README.txt` | `release-docs/RELEASE_README.txt` | ZIP root as `README.txt`; public repo `README.md` (same text) |
| `RELEASE_CHECKLIST.md` | `docs/RELEASE_CHECKLIST.md` | **not** public |
| `LEGAL_AND_RELEASE_AUDIT.md` | `docs/` (private notes) | **not** public |

## 9. What to do next (beginner steps)

1. Read `EULA.txt` and fill in every `[PLACEHOLDER]`. Decide the `[REVIEW]` points: licensor, governing law, contact address, the
   videos clause and the Ko-fi wording. Create a KuroLogic e-mail address so you don't use a personal one.
2. On GitHub, check that the source repo shows **Private**.
3. On your PC, in the repo folder, run `dotnet list package --include-transitive` and send me the output. Then run
   `tools\publish.cmd` and list the files in `publish\SmartMacro-1.0.0-win-x64`. With that I can fill in the exact versions in
   `THIRD-PARTY-NOTICES.txt`.
4. Tell me whether you approve the small code changes in section 1 (names in `AppInfo` and `.csproj`, two About buttons, extra
   files in `publish.cmd`). Optionally approve the first-launch "I agree" dialog from section 5 as well.
5. Confirm where the icon came from.
6. If possible, have someone with legal knowledge where you live look at the EULA, especially sections 2, 6(d), 11, 12 and 15.
7. Then work through `RELEASE_CHECKLIST.md`.
