# KuroLogic SmartMacro – Release checklist

Nothing below is ticked: none of it has been verified yet. Tick an item only after you
have checked it yourself.

## Ownership and legal
- [ ] Source repository remains private (check on GitHub: repo → Settings → General → "Danger Zone" shows "Change visibility"; the repo page shows the **Private** label).
- [ ] Ownership and attribution have been reviewed (see `LEGAL_AND_RELEASE_AUDIT.md`, "Ownership questions").
- [ ] The Licensor line in `EULA.txt` section 2 has been decided (no invented company; real name only if you choose to).
- [ ] The Settings attribution is consistent with Riku (リク) and KuroLogic (currently it shows "Owner & developer: Riku"; the EXE file properties say Company/Authors "Riku", Copyright "© 2026 Riku").
- [ ] The copyright notice "Copyright © 2026 KuroLogic. All rights reserved." is included (LICENSE.txt, EULA.txt, README, app About).
- [ ] The proprietary EULA has been reviewed; every [PLACEHOLDER] and [REVIEW] is resolved (law, contact, Ko-fi wording, videos clause).
- [ ] Unresolved legal questions have been documented (or answered by a qualified person).

## Third-party
- [ ] Third-party dependencies have been audited against the actual release build (`dotnet list package --include-transitive` + the files in the publish folder).
- [ ] Exact versions filled in, and full license texts pasted into `THIRD-PARTY-NOTICES.txt`.
- [ ] Required license notices are included in the download package.

## Build and package
- [ ] `tools\verify.cmd` green; manual tests (Stages 1–4) done.
- [ ] The compiled application has been tested on a clean Windows PC (not the dev PC).
- [ ] The package contains only: `SmartMacro.exe`, `EULA.txt`, `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt`, `README.txt` (from RELEASE_README.txt).
- [ ] The public package contains no private source code, `.pdb` files, logs (`release\*.log`, `artifacts\`), `PROJECT_STATUS.md`, docs with internal notes, or credentials.
- [ ] The ZIP was opened and inspected by hand before upload.
- [ ] (Optional) SHA-256 of the ZIP published next to the download link.

## Public pages
- [ ] Public download location chosen (separate public "releases" repo or another site) — NOT a release of the private source repo.
- [ ] The download page links to the EULA.
- [ ] The EULA is accessible after installation (EULA.txt next to the EXE; later also from the app's About section).
- [ ] YouTube and Discord links have been reviewed (all placeholders replaced, links open the right pages).
- [ ] Ko-fi is described as optional support everywhere.
