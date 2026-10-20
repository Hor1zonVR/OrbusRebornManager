# Orbus Reborn — mod development guide

Build and share plugins that work with **OrbusVR: Reborn Community Edition** or **Orbus Renewed**. A plugin that works in one client version is not automatically compatible with another.

## 1. Start with a separate instance

1. Install RebornManager.
2. Choose **New instance → Community Edition** or **Orbus Renewed**.
3. Enable **Prepare for mods** (BepInEx Unity IL2CPP) or install BepInEx from the instance overview.
4. Launch that instance once, then close the game. BepInEx should generate `BepInEx/interop/`.
5. Confirm `BepInEx/LogOutput.log` exists and shows that the loader started. If it does not, resolve the loader first; don't assume a mod DLL is broken.

For testing, do not modify the original downloaded game directory. Keep stable and experimental instances independent.

## 2. Build a client-side mod

RM currently supports **Windows Unity IL2CPP BepInEx plugins**. Your C# project should reference the installed instance's **matching** `BepInEx/core/` loader and runtime DLLs, plus `BepInEx/interop/` Unity/IL2CPP assemblies as needed. Build for the Windows environment and against the correct API versions.

Use the source of [RebornCam](https://github.com/Hor1zonVR/OrbusRebornCamera) and [OrbusQuestHelper](https://github.com/Hor1zonVR/OrbusQuestHelper) as examples of project setup; neither is a guarantee of compatibility with a new game release.

Considerations:
- Respect original game/server rules, other players' privacy and mod permissions.
- Do not change movement, identity, gameplay networking, anti-abuse checks, or other players' visibility.
- Avoid hardcoded absolute game paths; develop against an instance chosen by the developer.
- Test a clean game launch first, then with the plugin, and inspect BepInEx logs.
- Include configuration defaults and a description of every keyboard/VR control.

## 3. Test in RM

Use the **instance overview → Import DLL** to add your compiled plugin to that **specific instance**. That is a local-test path only. Check both basic game startup and the mod's primary features.

If your mod supports more than one game edition, test each edition/version and document the results. RM's download catalogue is curated; a listing should not promise untested compatibility.

## 4. Publish a release

Push your code to a public GitHub repository and create a **published GitHub Release** with the compiled DLL or a narrowly scoped ZIP as an asset. Uploading only source code is not enough for one-click installs. Avoid multiple ambiguous matching DLL/ZIP assets.

Recommended release notes:
- Version number and supported client edition/builds
- Installation requirements (BepInEx version and dependencies)
- What the mod changes and its hotkeys/VR interactions
- Known bugs and rollback instructions
- Any relevant permissions/licence information

## 5. Submit a mod to the curated catalogue

In RM open **Community & Help → Submit a mod**. Include your repository URL, a clear mod name, author, release asset pattern, compatible game editions/versions and a concise description. The maintainer will review it before adding the item to `catalog/mods.json`.

The catalogue has a `target` field: `client` means a PC game plugin, and `server` means a server-side project. Server-only mods must never be installed into a game client. The catalogue currently does **not** auto-validate edition compatibility, so be truthful about support.

For bugs in RM itself, use [Report a bug](https://github.com/Hor1zonVR/OrbusRebornManager/issues/new) and provide the manager version, game edition/version, reproduction steps and **redacted** logs. Never post account identity files, server tokens, or unreviewed private information.

## 6. How to safely update game builds

Rebuild or retest plugins if a game's Unity, IL2CPP or API layout changes. Keep old game instances until updated plugins have been confirmed working. Installing the newest BepInEx or the newest mod does not automatically solve all build incompatibilities.

**Note:** The Community Edition licence grants specific modification rights, including content and mods directories, but limits reverse engineering of compiled software. Review upstream licences/permissions before distributing patches or modified game binaries.
