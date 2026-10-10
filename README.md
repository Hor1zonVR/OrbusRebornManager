# Orbus Reborn Manager

A Windows launcher and curated mod manager for **OrbusVR Reborn Community Edition**.
This project is not intended for OrbusVR Classic or Preborn.

> RebornManager is under active development. Published releases contain a Windows installer and receive updates through the in-app updater.

## Install, uninstall, and update

**Player download:** get `RebornManager-Setup.exe` from [GitHub Releases](https://github.com/Hor1zonVR/OrbusRebornManager/releases). Run it to install RebornManager. The user-facing GitHub release has **one attached download only**, with no package archives or updater manifests to sift through.

The installed application can check for newer versions, show an **Update & restart** action, and install the update after user approval.

**Update packages:** The app obtains the `releases.win.json` feed and its matching Velopack `.nupkg` from GitHub Pages, **not** GitHub Releases:
`https://hor1zonvr.github.io/OrbusRebornManager/updates/`.

These packages are needed for the in-app updater even though normal users should only see Setup.exe on the downloads page. The site hosts the latest full update package so users can jump directly to the newest version; delta updates can be added later.

**Uninstall:** Open RebornManager → Settings → Uninstall RebornManager. The button uses Velopack's Windows uninstaller. It keeps the original OrbusVR installation, your separate modded instances, and saved manager settings so you can reinstall without starting again. You can also uninstall using Windows Installed apps.

**Existing testers:** v0.4.0 used GitHub Releases as its updater source. Since v0.4.1 switches to the separate feed, uninstall the old v0.4.0 through Windows Settings first, then install the new v0.4.1 Setup.exe. Once v0.4.1 is installed, later updates will use the new feed. The new in-app Uninstall button will be available from v0.4.1 onward.

### Developer release prerequisites

**Enable Pages once** on the GitHub repository: Settings → Pages → Build and deployment → Source → **GitHub Actions**. If Pages is not enabled, the release job will intentionally stop before publishing the public installer. This prevents a public Setup.exe appearing without its updater feed.

### Publish a new manager update

Once the changes you want are on `main` and the **Build and release RebornManager** check is green:

1. Open [GitHub Actions → Publish RebornManager Update](https://github.com/Hor1zonVR/OrbusRebornManager/actions/workflows/publish-update.yml).
2. Click **Run workflow**, choose the **main** branch, keep **patch** selected (normal updates), and click **Run workflow**.
3. The workflow verifies the latest main build passed, bumps the project version, commits it, creates the matching tag, and dispatches the existing Windows release workflow.
4. Watch **Build and release RebornManager** complete. It deploys the Velopack update feed to GitHub Pages and publishes only `RebornManager-Setup.exe` to GitHub Releases.

No local Git commands, manual tag creation or merging is needed **when the approved changes are already on `main`**. For experimental development on branches, someone must first review/merge those changes; the publishing button intentionally does **not** merge unreviewed pull requests.

Choose *minor* for substantial feature releases or *major* for deliberately breaking version changes. Normal changes use *patch*. **Never run the publish workflow just to test unfinished code.**

The Actions test artifact contains just Setup.exe, although GitHub's Actions interface may wrap artifact downloads in a ZIP. The **public Releases page** exposes the executable directly without an extra ZIP.

This project is currently a development preview. Do not tag a public release until the installer and update path have been validated on Windows.

### Local Windows build

Requires Windows and the **.NET 8 SDK**.

```powershell
.\build-windows.ps1
```

The script builds the installer and update packages locally into `Releases/`. Use **the Setup.exe** for testing; the other generated files are inputs to the updater publishing process, not downloads for players.

## Instance library and first run

1. Install RebornManager using `RebornManager-Setup.exe` from the latest GitHub Release.
2. On **My Instances**, choose **Add existing game** and point it to your OrbusVR Reborn installation (the folder containing `vrclient.exe`). This only registers the folder.
3. Click **New Instance**. Give the copy a name and confirm the source installation.
4. New copies default to `%LOCALAPPDATA%\OrbusRebornModdingInstances`. Click **Change** to choose another drive. The manager remembers your selected location if you leave the checkbox enabled.
5. Leave **Prepare for mods** checked to install the supported BepInEx loader in your **new copy**, or untick it for a clean copy without the loader.
6. When prepared for mods, launch the new instance once to let BepInEx generate interop files, then close the game before installing plugins.
7. Open an instance card to manage its mods, or press the Play icon to launch it. Right-click or press the card's menu icon to open its folder.

The instance library supports searching by name, sorting by recently added or name, and filtering original/existing installations versus manager-created modded copies. **Changing the default location never moves existing game copies.**

New copies are created using the existing safe staging-and-copy routine, exclude the original loader's files, and check free disk space before copying. The original source game files are never deleted during instance creation.

## Curated catalogue workflow

RebornManager uses an explicitly curated catalogue, rather than crawling arbitrary GitHub repositories.

**Maintainers:** review a mod author's source, binary release assets, permissions and game compatibility, then edit `catalog/mods.json` and commit the reviewed entry. Set `"target": "client"` or `"target": "server"` explicitly for each listing. Client mods can be installed into a selected instance; server-only mods are discoverable separately and must not be installed into a player's game.

The catalogue is fetched automatically from the configured GitHub raw JSON source. A GitHub repository is not a safety guarantee: review publishers and release assets before adding them.

### Catalogue entry format

See `catalog/mods.example.json`. Example entry:

```json
{
  "id": "orbusreborncamera",
  "name": "Orbus Reborn Camera",
  "author": "Horizon",
  "description": "Wide-FOV recording and creator camera controls.",
  "repository": "https://github.com/YOUR_USERNAME/OrbusRebornCamera",
  "assetPattern": "*.dll"
}
```

The mod creator must publish a **stable GitHub Release** with a `.dll` or `.zip` asset matching the pattern. GitHub's autogenerated **Source code (zip)** is *not* accepted as a binary release asset. Exactly one matching DLL/ZIP is required. `assetPattern` supports `*` and `?` wildcards; set precise patterns when authors upload multiple files.

The manager queries the GitHub **latest stable release** API. It uses the GitHub release tag as the installed version, downloads the matching release asset, and installs it under `BepInEx/plugins/<mod-id>/`.

**Automatic updates:** Enabled by default (the user can disable this in Settings). At manager startup, if a managed mod has a different latest release tag, it updates automatically **only when OrbusVR is closed**. If the game is running, users can close it and check again. Local imported DLLs never update automatically. Mods and BepInEx are not changed while the game is running.

Important security note: BepInEx plugins execute native or managed code with the user's game privileges. GitHub release hashes are verified when GitHub exposes a `sha256:` digest, but curation is *not* a guarantee of safety. Do not approve untrusted publishers, and review new release permissions/dependencies.

## Safety and cleanup

- Reborn-only path validation prevents accidentally installing to Classic or another game folder.
- The installer refuses to replace an unknown loader (`winhttp.dll` or `doorstop_config.ini` already present).
- Existing loader files under BepInEx/dotnet are copied to an automatic timestamped backup before overwrite; failed installs attempt to roll back changed files.
- Archive paths and executable/script payloads are restricted; DLL ZIPs install only into mod-owned plugin folders.
- Every manager-installed catalogue mod gets a dedicated directory.
- Disable moves the whole folder to `OrbusRebornManager/disabled/<id>/`, **outside** BepInEx.
- Uninstall deletes only the manager-owned folder, not the whole `BepInEx/plugins` tree, and preserves BepInEx config files.
- Local managed state and settings are stored in `%LOCALAPPDATA%\OrbusRebornManager\`.
- The manager never runs arbitrary scripts from a mod archive or the catalogue.

## Planned repositories and naming

- **OrbusRebornManager** — this Windows application and the public `catalog/mods.json`.
- **OrbusRebornCamera** — formerly BetterMirror; retain existing plugin compatibility while changing public name.
- **OrbusRebornAutoMount** — automatically summon equipped mount after combat ends; to be developed separately.

Releasing the camera and AutoMount requires **published compiled DLL assets**, not just source files, for the manager to offer one-click installs and updates.
