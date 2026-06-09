# Vegha — Upgrade (auto-update) testing runbook

Hand this to anyone who can open a terminal and copy-paste. It verifies that an **installed**
Vegha can notice a newer version, download it, and update itself (the VS Code-style banner).

You will run four tests:

1. **Make a version change and build** an installer.
2. **Test the upgrade with a local folder feed** (no internet).
3. **Test the upgrade with a temporary private GitHub repo** (the real online path).
4. **Test channels** — prove the app only updates within its own platform channel.

---

## 0. Before you start (one time)

**Use a Windows 11 (x64) machine. Open PowerShell and `cd` into the repo root** (the folder
that contains `Vegha.sln`). Run **every** command from there.

Check your tools — each line should print a version, not an error:

```powershell
dotnet --version          # must start with 10.
git --version
gh --version              # only needed for Test 3 — install from https://cli.github.com if missing
```

Install the Velopack CLI (the build script also does this, but do it now to be safe):

```powershell
dotnet tool install -g vpk
```

> If `vpk` is "not recognized" later, **close and reopen PowerShell** and `cd` back to the repo root.

### Things to know

- The update UI only appears when Vegha is **installed** (via `Setup.exe`). Running it from
  source (`dotnet run`) deliberately shows nothing. These tests install to a **scratch folder**
  (`%LOCALAPPDATA%\VeghaUpgradeTest`) and never touch a real Vegha you may have installed.
- The number you pass as `-Version` is stamped into the app — you'll see it **bottom-right in
  the status bar** and in **Help → About**. It's also what the updater compares.
- `VEGHA_UPDATE_*` environment variables redirect the updater for testing. They only affect the
  PowerShell window you set them in (and apps launched from it). They are **never** used by a
  normal production build.

### Placeholders

Replace anything in `<ANGLE BRACKETS>` with your real value (e.g. your GitHub username).

---

## Test 1 — Make a version change and build

**Goal:** produce an installer stamped with a version you choose.

1. Build an installer for version `1.0.0`:

   ```powershell
   ./eng/Pack-Installer.ps1 -Runtime win-x64 -Version 1.0.0
   ```

   ✅ **PASS** when it ends with `Done. Installers in ...\releases\` and this shows the files:

   ```powershell
   Get-ChildItem releases/win-x64 | Select-Object Name
   ```

   You should see at least: `Vegha-win-x64-Setup.exe`, `releases.win-x64.json`, and a
   `*-full.nupkg`.

2. Confirm the version was stamped correctly:

   ```powershell
   Get-Content releases/win-x64/releases.win-x64.json
   ```

   ✅ **PASS** when the text contains `"Version":"1.0.0"`.

> That's the whole "version change + build" step. For Tests 2–4 you'll just pass a different
> `-Version` number and the script stamps it — you do **not** need to edit any files.
>
> (For a *real* release the version lives in `Directory.Build.props`; `eng/Publish-Release.ps1`
> bumps it and tags the release. You don't need that for testing.)

---

## Test 2 — Upgrade using a LOCAL FOLDER feed (no internet)

**Goal:** install `1.0.0`, point it at a local folder that also contains `1.0.1`, watch it
update.

Run these blocks **in order, in the same PowerShell window.**

1. Start clean:

   ```powershell
   ./eng/test/Uninstall-LocalBuild.ps1
   Remove-Item releases/win-x64 -Recurse -Force -ErrorAction SilentlyContinue
   ```

2. Build **1.0.0** and install it:

   ```powershell
   ./eng/Pack-Installer.ps1 -Runtime win-x64 -Version 1.0.0
   ./eng/test/Install-LocalBuild.ps1
   ```

   ✅ **PASS** when it prints `Installed OK.` and a `Launch it with:` line.

3. Build a newer **1.0.1** into the **same** folder (this becomes the "feed"):

   ```powershell
   ./eng/Pack-Installer.ps1 -Runtime win-x64 -Version 1.0.1
   Get-Content releases/win-x64/releases.win-x64.json
   ```

   ✅ **PASS** when the JSON now lists **both** `1.0.0` and `1.0.1`.

4. Point the installed app at the local feed and launch it:

   ```powershell
   $env:VEGHA_UPDATE_FEED    = (Resolve-Path releases/win-x64).Path
   $env:VEGHA_UPDATE_CHANNEL = "win-x64"
   Start-Process "$env:LOCALAPPDATA\VeghaUpgradeTest\current\Vegha.App.exe"
   ```

   ✅ **PASS** — in the app window you should see:
   - bottom-right status bar shows **v1.0.0**, and
   - within a few seconds a banner at the top: **"Vegha 1.0.1 is ready — restart to finish
     updating"** with **Restart now / Later / Release notes**.
   - (If it doesn't appear on its own, click **Help → Check for Updates…** to trigger it.)

5. Click **Restart now** in the app.

   ✅ **PASS** — the app closes and reopens, and the **bottom-right status bar now shows
   v1.0.1** (Help → About also says 1.0.1). **That is a successful self-update.**

6. Clean up:

   ```powershell
   ./eng/test/Uninstall-LocalBuild.ps1
   Remove-Item Env:\VEGHA_UPDATE_FEED, Env:\VEGHA_UPDATE_CHANNEL -ErrorAction SilentlyContinue
   ```

---

## Test 3 — Upgrade using a TEMPORARY private GitHub repo (the real online path)

**Goal:** verify the actual GitHub path the production app uses, against a throwaway private
repo. Your real/production repo is never touched.

### 3a. One-time setup

1. Make sure GitHub CLI is logged in:

   ```powershell
   gh auth status
   ```
   If it says you're not logged in, run `gh auth login` and follow the prompts.

2. Create a **private** sandbox repo:

   ```powershell
   $REPO = "<YOUR-GITHUB-USERNAME>/vegha-update-sandbox"
   gh repo create $REPO --private
   ```

3. Create a **Personal Access Token (PAT)** so the app can read the private repo:
   - Go to <https://github.com/settings/tokens> → **Generate new token**.
   - Classic token: tick the **`repo`** scope. (Or a fine-grained token with **Contents:
     Read** on `vegha-update-sandbox`.)
   - **Copy the token string** — you'll paste it in step 3b.4.

### 3b. Run the test (same PowerShell window throughout)

1. Start clean and set your repo variable:

   ```powershell
   $REPO = "<YOUR-GITHUB-USERNAME>/vegha-update-sandbox"
   ./eng/test/Uninstall-LocalBuild.ps1
   Remove-Item releases/win-x64 -Recurse -Force -ErrorAction SilentlyContinue
   ```

2. Build **1.0.0**, publish it to the sandbox, and install it:

   ```powershell
   ./eng/Pack-Installer.ps1 -Runtime win-x64 -Version 1.0.0
   $files = (Get-ChildItem releases/win-x64 -File).FullName
   gh release create v1.0.0 $files --repo $REPO --title "v1.0.0" --notes "test"
   ./eng/test/Install-LocalBuild.ps1
   ```

   ✅ **PASS** when `gh` prints a release URL and the install prints `Installed OK.`

3. Build a newer **1.0.1** and publish it to the sandbox:

   ```powershell
   ./eng/Pack-Installer.ps1 -Runtime win-x64 -Version 1.0.1
   $files = (Get-ChildItem releases/win-x64 -File).FullName
   gh release create v1.0.1 $files --repo $REPO --title "v1.0.1" --notes "test"
   ```

   ✅ **PASS** when `gh` prints a second release URL.

4. Point the installed app at the sandbox repo and launch it (**paste your PAT**):

   ```powershell
   $env:VEGHA_UPDATE_REPO    = "https://github.com/$REPO"
   $env:VEGHA_UPDATE_TOKEN   = "<PASTE-YOUR-PAT-HERE>"
   $env:VEGHA_UPDATE_CHANNEL = "win-x64"
   Start-Process "$env:LOCALAPPDATA\VeghaUpgradeTest\current\Vegha.App.exe"
   ```

   ✅ **PASS** — same as Test 2: status bar shows **v1.0.0**, then a banner offers **1.0.1**.
   Click **Restart now** → it reopens as **v1.0.1**.

5. Clean up (this deletes the sandbox repo entirely):

   ```powershell
   ./eng/test/Uninstall-LocalBuild.ps1
   Remove-Item Env:\VEGHA_UPDATE_REPO, Env:\VEGHA_UPDATE_TOKEN, Env:\VEGHA_UPDATE_CHANNEL -ErrorAction SilentlyContinue
   gh repo delete $REPO --yes
   ```

   > `gh repo delete` may ask you to re-authorize with the `delete_repo` scope the first time;
   > follow its prompt, or delete the repo by hand on github.com.

---

## Test 4 — Build and test update in different CHANNELS

**Goal:** prove that each platform updates only within its own channel — e.g. a `win-x64`
install follows `win-x64` releases and ignores `win-arm64`. This is what lets every platform
share one GitHub Release safely.

Run in order, same window:

1. Start clean:

   ```powershell
   ./eng/test/Uninstall-LocalBuild.ps1
   Remove-Item releases/win-x64, releases/win-arm64, eng/test/combined-feed -Recurse -Force -ErrorAction SilentlyContinue
   ```

2. Build **x64 1.0.0** and install it:

   ```powershell
   ./eng/Pack-Installer.ps1 -Runtime win-x64 -Version 1.0.0
   ./eng/test/Install-LocalBuild.ps1
   ```

3. Build an **x64 1.0.1** (an update on the x64 channel) and an **arm64 1.0.0** (the arm64
   channel stays at 1.0.0 — no update there):

   ```powershell
   ./eng/Pack-Installer.ps1 -Runtime win-x64  -Version 1.0.1
   ./eng/Pack-Installer.ps1 -Runtime win-arm64 -Version 1.0.0
   ```

   > The arm64 build cross-compiles on an x64 machine; it may take a minute. You never run it —
   > you only need its channel feed files.

4. Combine both channels into one folder (like a real multi-platform GitHub Release) and run the
   **collision check**:

   ```powershell
   $combined = "eng/test/combined-feed"
   New-Item -ItemType Directory -Force $combined | Out-Null
   Copy-Item releases/win-x64/*   $combined/ -Force
   Copy-Item releases/win-arm64/* $combined/ -Force

   "win-x64","win-arm64" | ForEach-Object {
     if (Test-Path "$combined/releases.$_.json") { "OK   releases.$_.json" } else { "FAIL missing releases.$_.json" }
   }
   Get-ChildItem "$combined/*-full.nupkg" | Select-Object Name
   ```

   ✅ **PASS** when:
   - both `OK   releases.win-x64.json` and `OK   releases.win-arm64.json` print, **and**
   - the `*-full.nupkg` list shows **distinct names per channel** (the channel appears in the
     file name, e.g. `...win-x64...` and `...win-arm64...`).

   ❌ **STOP and report** if a `releases.*.json` is missing, or if the two channels produced
   **identically-named** `.nupkg` files. That means the channels collide and the multi-platform
   release needs adjusting. *(This is the one thing we specifically want this test to confirm.)*

5. **Test A — same channel sees the update.** Launch the installed x64 app pointed at the
   combined feed on its own channel:

   ```powershell
   $env:VEGHA_UPDATE_FEED    = (Resolve-Path $combined).Path
   $env:VEGHA_UPDATE_CHANNEL = "win-x64"
   Start-Process "$env:LOCALAPPDATA\VeghaUpgradeTest\current\Vegha.App.exe"
   ```

   ✅ **PASS** — a banner offers **Vegha 1.0.1**.
   Then **close the app WITHOUT clicking Restart** (click the banner's ✕, then close the window).

6. **Test B — a different channel sees no update.** Re-launch the **same** install pointed at
   the **arm64** channel:

   ```powershell
   $env:VEGHA_UPDATE_CHANNEL = "win-arm64"
   Start-Process "$env:LOCALAPPDATA\VeghaUpgradeTest\current\Vegha.App.exe"
   ```

   ✅ **PASS** — **no update banner appears**, and **Help → Check for Updates…** says
   *"Vegha 1.0.0 is the latest version."* (The arm64 channel only has 1.0.0.)

   Together, A and B prove the channel decides what the app sees from the *same* feed.

7. Clean up:

   ```powershell
   ./eng/test/Uninstall-LocalBuild.ps1
   Remove-Item Env:\VEGHA_UPDATE_FEED, Env:\VEGHA_UPDATE_CHANNEL -ErrorAction SilentlyContinue
   Remove-Item releases/win-arm64, eng/test/combined-feed -Recurse -Force -ErrorAction SilentlyContinue
   ```

---

## Final cleanup (run once when you're completely done)

```powershell
./eng/test/Uninstall-LocalBuild.ps1
Remove-Item Env:\VEGHA_UPDATE_FEED, Env:\VEGHA_UPDATE_REPO, Env:\VEGHA_UPDATE_TOKEN, Env:\VEGHA_UPDATE_CHANNEL -ErrorAction SilentlyContinue
Remove-Item releases -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item eng/test/combined-feed -Recurse -Force -ErrorAction SilentlyContinue
git status   # should show no source changes from testing
```

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| **No banner, and `Help → Check for Updates…` is missing** | The app isn't a real install. Launch the exe under `%LOCALAPPDATA%\VeghaUpgradeTest\current\`, not from source. Re-run `Install-LocalBuild.ps1`. |
| **Banner never appears / "Couldn't check for updates"** | The feed/channel don't match. Confirm `releases.win-x64.json` exists in the feed folder, and that `$env:VEGHA_UPDATE_CHANNEL` is `win-x64` in the **same window** you launched the app from. |
| **Version bottom-right doesn't change after Restart** | The update didn't apply. Check the log at `%LOCALAPPDATA%\VeghaUpgradeTest\current\` and `%LOCALAPPDATA%\Vegha\` (Serilog / `crash.log`). Re-run the test from a clean state. |
| **`vpk` not recognized** | `dotnet tool install -g vpk`, then close & reopen PowerShell. |
| **`gh` asks for `delete_repo` scope** | Run `gh auth refresh -h github.com -s delete_repo`, or delete the sandbox repo on github.com. |
| **arm64 pack fails** | Ensure the .NET 10 SDK is installed and you have internet (it downloads the arm64 runtime). |
| **Settings → Updates** | You can also flip "Automatically check for updates" and the Stable/Beta channel here; the banner respects them. |
