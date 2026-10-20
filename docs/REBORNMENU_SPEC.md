# RebornMenu — in-game mod configuration platform

**Status:** approved design target / implementation **not started**. This is the canonical product and engineering specification for the planned RebornMenu core companion plugin. It is **not** a claim that RM already installs it or that an SDK exists today.

**Applies to:** OrbusVR Reborn Community Edition and tested Orbus Renewed (FourOfSpades) PCVR instances using Windows BepInEx 6 IL2CPP. Standalone Quest is **not** in the initial scope.

**Goal:** one clean, VR-first settings menu for any installed BepInEx mod, plus optional polished custom pages and shared 3D placement tools for compatible developers.

## 1. Player experience

**Desktop setup**

1. Create/select a modded instance in RebornManager (RM).
2. Choose **Prepare for mods** or **Install BepInEx**.
3. RM installs BepInEx and installs/repairs the **RebornMenu core companion** as a separate, manager-owned plugin by default.
4. Launch that game instance. A **Mods** wrist shortcut opens a readable, movable in-world panel.
5. The panel lists loaded mods and exposes their normal BepInEx settings even if their authors have done nothing special for RebornMenu.
6. Enhanced mods can show custom controls, a live preview and an **Edit Placement** mode.
7. Close the menu to play normally. Nothing intercepts your controls while the menu is closed.

**Important:** RebornMenu is installed automatically by RM when preparing BepInEx, but **RM does not require RebornMenu to function**, and regular mods **never require it** unless an author explicitly documents a separate optional enhancement. Users may disable or repair RebornMenu per instance without breaking the game or RM.

**Mod list:** search, alphabetical names, version, author/plugin GUID, status (loaded/unavailable/disabled, when known), generic or enhanced settings capability. The desktop RM remains the authoritative tool for installing/updating/disabling DLLs; RebornMenu edits settings **not arbitrary running assemblies**.

## 2. Two levels of integration

### Level A: universal settings, zero integration work

The baseline discovers runtime-loaded BepInEx 6 IL2CPP plugins and their registered \`ConfigFile\` / \`ConfigEntryBase\` entries, using **plugin GUIDs** as stable identifiers. Do not depend on filenames or display names.

- \`bool\` → toggle
- \`enum\` or \`AcceptableValueList<T>\` → selection/dropdown
- \`int\`, \`float\`, \`double\` with \`AcceptableValueRange<T>\` → bounded slider and precise number input
- numeric types without a declared range → numeric field with validation (never invent min/max)
- \`string\` → VR keyboard/text input, with displayed help/description
- other supported serialisable types → type-specific editor only when verified; otherwise a labelled safe string representation or read-only value
- group by BepInEx config **section**; use **description**, **default**, accepted values/ranges and tags where present
- each setting has **Reset**, each mod has **Reset All** with confirmation, and placement settings have **Recover visibility**
- search settings and show an explicit **restart may be required** indicator

Change loaded values through the typed registered config entry so BepInEx validates the type/range and can raise \`SettingChanged\`; save using its owning \`ConfigFile\`. This **does not guarantee** a third-party plugin dynamically reacts: some settings only take effect after game restart.

**Unloaded plugins and files:** discover matching \`BepInEx/config/*.cfg\` files in a clearly marked **file-only** section. A normal BepInEx config file can be edited by a conservative round-trip section/key parser, preserving unknown lines and comments and writing via an atomic temp-file replacement/backup. Changes to unloaded plugins are **restart required**. An arbitrary file with a \`.cfg\` extension is not necessarily a BepInEx configuration: if its syntax or value types cannot be safely understood, show **read-only / unsupported format**, rather than overwrite or corrupt it. Do not assume arbitrary JSON, XML or proprietary configs are editable.

**Secrets:** sensitive-looking fields (tokens, credentials) must not be echoed to logs, screenshots or support exports; mask them by default. Never execute code embedded in config strings, accept file paths outside the selected instance or invoke arbitrary reflection methods based on file contents.

### Level B: enhanced pages, optional for mod authors

Mods may expose **declarative page metadata** (sections, tabs, explanatory cards, readouts, switches, sliders, colour/theme presets, buttons and preview slots) through the optional **RebornMenu extension contract**, described in [Developer integration](REBORNMENU_DEVELOPER_API.md).

RebornMenu owns the **common visuals, inputs, value validation, persistence, accessibility and layout**. A mod owns its setting names, ranges, specialised preview data and narrowly scoped actions. A mod does **not** render raw unmanaged VR controls or replace the whole menu.

There will be two enhancement choices:
1. **Declarative manifest only:** an optional \`rebornmenu.json\` beside a plugin can describe page grouping, icon/summary, grouping presets and associate controls with existing \`Config.Bind\` section/key pairs. No reference to a RebornMenu DLL required.
2. **Runtime provider (advanced):** opt-in registration/callbacks for live readouts, previews, placement target transforms or mod-owned actions. Registration is **soft/optional**, version negotiated, and must fail harmlessly if RebornMenu is missing. The final C# API names/signatures will be frozen only after the first IL2CPP runtime proof.

A missing/broken custom provider must fall back to **Level A** for the same mod; it must not hide or corrupt standard BepInEx settings.

## 3. Shared VR layout and physical placement

All overlays (CastMetrics, QuestHelper, GoblinTimer and future projects) should ultimately use the **same placement model**, rather than hand-writing unrelated XYZ/scale UIs.

- **Anchors:** left wrist, right wrist, left controller/hand, right controller/hand, wand, head-relative or world-fixed — only offer anchors a specific mod supports.
- **Standard configuration:** anchor choice, offset X/Y/Z in metres, rotation, scale, show/hide, and mod-specific defaults.
- **Edit Placement:** after explicitly choosing it, hide the menu or put it in a compact safe mode; show a labelled holographic outline and grab handle; use a controller/hand trigger to move and rotate the HUD; release to preview; **Confirm**, **Cancel** and **Reset Placement** are always recoverable.
- **Safety:** don't move unrelated game objects, change the XR rig, or hijack combat/grab inputs outside active edit mode. Automatically exit placement mode on scene change, disconnect or missing tracked hand.
- **Recovery:** if the target goes out of view or scale becomes invalid, an always-accessible **Reset HUD layout** and **Recenter menu** reset to a verified preset. Clamp extreme scales and distances to safe, per-mod bounds.
- **Persistence:** use the mod's existing BepInEx config values (or documented sidecar section/keys). RebornMenu must not create a second conflicting truth for the same overlay. Missing menu must leave existing mod placement working.

A mod must explicitly expose a safe **placement target** or transform adapter; RebornMenu **cannot automatically locate or drag arbitrary third-party render objects merely because a config file exists**. Without a provider, users edit the normal position/scale config entries through Level A.

## 4. VR-first UI and controls

- World-space VR panel, large readable high-contrast text, predictable button hitboxes, scalable layout and page breadcrumbs: **Mods → Mod → Section / Custom page**.
- A discreet **Mods** wrist button that can be reassigned/repositioned/hidden, plus an alternate **Recenter/Open menu** method so players cannot lose it. Configure wrist side and activation sensitivity.
- Basic laser/pointer + trigger selection, scrolling and VR keyboard where available. No forced use of Quest-specific APIs: PCVR via SteamVR/Oculus/Quest Link is the initial platform.
- Work in **both** the Community and Renewed clients without assuming their inventory/PowerUI implementations are identical. Use a standalone Unity world-space canvas/panel first; **do not patch the game journal or native menus** for v0.1.
- The menu should pause its own edit interactions, **not** the whole game, and must never retain gameplay input capture after closing.
- Windows desktop fallback for testing: an independent, configurable shortcut (do not steal existing RebornCam F5/F6/F7/F10 binds). No dependence on Unity IMGUI being unstripped for the VR renderer.

**Technical spike required:** Unity 2018.4 + IL2CPP generated \`UnityEngine.UI\` / \`UIModule\` / input modules, controller raycast and trigger behaviour, scene transitions, threading, renderer order and player rig access must be inspected and tested independently for both editions. Choose the panel renderer only after this passes.

## 5. Architecture / ownership

\`\`\`text
RebornManager (Windows desktop, per-instance)
  └─ Install BepInEx 6 IL2CPP
       ├─ BepInEx loader (upstream)
       └─ RebornMenu core companion (manager-managed)
            ├─ Loaded BepInEx plugins → generic ConfigEntry pages
            ├─ BepInEx .cfg files → safe file-only fallback
            ├─ Optional JSON display manifests
            ├─ Optional live providers / placement adapters
            └─ Unity VR input + world-space UI + persistence

Other mods (QuestHelper / CastMetrics / GoblinTimer / RebornCam...)
  ├─ run normally with BepInEx even WITHOUT RebornMenu
  ├─ existing Config.Bind(...) settings work as baseline
  └─ optionally register enhanced page / movable HUD
\`\`\`

- **Runtime repository:** future \`Hor1zonVR/OrbusRebornMenu\` (does not exist yet).
- **Manager integration:** current \`Hor1zonVR/OrbusRebornManager\` repository. Automatic companion install belongs to the managed BepInEx setup / repair path, **not** general mod catalogue discovery.
- **Per-instance path:** \`<game>/BepInEx/plugins/OrbusRebornMenu/\`, one pinned/reviewed release with SHA-256 checked before installation.
- **No circular dependency:** neither RM app startup nor another mod's startup is blocked by missing RebornMenu. If companion download fails, finish installing BepInEx, visibly report companion failure and allow **Repair/Reinstall RebornMenu** later.
- **Update semantics:** RM maintains a compatible companion version for the selected BepInEx and game edition; checks for new stable signed/pinned release information, but doesn't silently install experimental menu builds on every game launch. Keep an explicit disable option per instance.
- **Mod manifest and configs:** place sidecars with the owning mod or under its dedicated settings folder; never alter unknown files on another plugin's behalf.
- **Game-version updates:** existing RM swaps whole game folders and preserves \`BepInEx\` on update. RebornMenu remains present, but generated interop must be regenerated as needed. Detect incompatible menu builds and prompt for repair, not delete user configuration.
- **Ownership:** the menu is a core companion but is not part of the BepInEx distribution. Maintain its own version, changelog, rollback and diagnostics.

### Menu health indicators in RM

One compact status line per instance: **BepInEx installed**, **RebornMenu ready / missing / disabled / update available / incompatible**. Actions: **Open menu setup**, **Repair**, **Disable**, **Check updates**, **View logs**. RM must remain fully functional in every state.

## 6. Versioning, compatibility and security

- Version the extension schema separately from UI/plugin binary (e.g. \`schemaVersion: 1\`); refuse unknown major versions and still expose basic configs.
- Mod identity = BepInEx GUID, never mod DLL basename.
- No arbitrary reflected type construction, remote scripts, code snippets, custom DLL fetching or unrestricted method invoking through a manifest.
- Runtime providers may only invoke actions exposed by their own loaded plugin; validate capability, version and callback lifetime.
- Keep loaded state scoped to the **selected running game instance**. A menu inside one game must not list or write another instance's configs.
- Atomic writes, backup on parse/write failures, reasonable rate-limits on slider saves; never store usernames, secret tokens or telemetry without explicit user opt-in.
- Permissions/server rules apply to all mods: the menu must not bypass server-side gameplay restrictions.

## 7. Milestones and acceptance criteria

**Blocker policy: before resuming major development of QuestHelper, CastMetrics, GoblinTimer or other planned mods, RebornMenu must have a tested v0.1 runtime, documented provider contract and proven shared placement workflow.** Mod discovery and configuration alone are not sufficient.

| Stage | Deliverable | Exit test |
| --- | --- | --- |
| 0. Research + API spec | This document, optional integration contract, compatibility checklist | All four existing mod codebases can map their config entries; no required dependency |
| 1. VR runtime proof | Independent BepInEx IL2CPP plugin, wrist open/close, world-space UI | Menu readable and controllable in VR on Community + Renewed; no changes to game rig |
| 2. Generic configuration v0.1 | Mod list, sections, typed widgets, save/reset, file-only fallback | Toggle/slider/string/enum persist; unsupported file is read-only; restart warning works |
| 3. Enhanced API v0.2 | Manifest support, optional live provider, shared HUD placement | RebornMenu absent → target mod still runs; plugin pages gracefully fall back |
| 4. Real HUD pilot | CastMetrics as first placement adapter without rewriting its gameplay hooks | Grab, move, scale, confirm/cancel/reset all work and persist |
| 5. RM integration | Auto-install with BepInEx, repair/disable/version status | Fresh and existing instances work; failure leaves BepInEx usable |
| 6. Foundation stable | Cross-edition testing, docs/examples, accessibility/recovery | QuestHelper, GoblinTimer, RebornCam can adopt APIs without per-mod menu reimplementation |

**Regression checklist:** VR and desktop input; clean first run; disconnected controller; scene change; config save/restart; unreadable/invalid values; unknown file formats; mod missing; menu disabled; duplicate GUID; disabled mod; HUD offscreen reset; safe game update/rollback; both PC clients.

## 8. Non-goals for the first release

- Shipping Quest Android modding (different loader/runtime and rendering).
- Unloading/reloading arbitrary BepInEx DLLs in a live IL2CPP process.
- Guaranteeing third-party settings apply instantly.
- Editing arbitrary proprietary config formats.
- Replacing native Orbus menus or changing the XR rig.
- Requiring other mod developers to install an SDK for basic settings.

See [Developer API / manifest contract](https://github.com/Hor1zonVR/OrbusRebornMenu/blob/main/docs/DEVELOPER_API.md) for integration details, examples and compatibility rules.
