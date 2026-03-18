# Oxygen Parity Gap Analysis

Date: 2026-03-18

## Goal

Understand why original Oxygen already provides a broad working feature set while this project is still missing large pieces of functionality, then turn that into an actionable engineering plan.

## Primary sources reviewed

- Oxymod feature/site entry: https://oxymod.com/
- Installing plugins: https://docs.oxymod.com/guide/owners/installing-plugins.html
- Player API: https://docs.oxymod.com/api/players-methods.html
- Web API: https://docs.oxymod.com/guide/web-request.html
- Working with actors: https://docs.oxymod.com/guide/actors.html
- Hook docs:
  - https://docs.oxymod.com/hooks/player-chat.html
  - https://docs.oxymod.com/hooks/player-openInventory.html
  - https://docs.oxymod.com/hooks/player-takeItemInHands.html
  - https://docs.oxymod.com/hooks/player-lockPickEnded.html
  - https://docs.oxymod.com/hooks/player-respawn.html
  - https://docs.oxymod.com/hooks/player-respawned.html
- Public plugin repository: https://github.com/Oxygen-SCUM/oxygen.plugins
- Public documentation repository: https://github.com/Oxygen-SCUM/oxygen.docs

## What original Oxygen clearly has

1. `.cs` plugins are first-class. They are dropped into `SCUM/Binaries/Win64/oxygen/plugins/`, auto-compiled, and hot-reloaded without a restart.
2. `PlayerBase` is much richer than ours:
   - identity data
   - economy data
   - ping
   - fake name
   - direct inventory object
   - fluent item manipulation
3. Their public docs expose a wider hook surface:
   - chat
   - connected/disconnected
   - respawn/respawned
   - open inventory
   - take item in hands
   - melee attack
   - mini game ended
   - lockpick ended
4. Their docs expose actor-level world control:
   - spawn actor
   - move actor
   - destroy actor
5. Their plugin web layer is plugin-facing, not just panel-facing:
   - `StartWebServer(port, token)`
   - `[WebRoute(...)]`

## Why their system works and ours lags behind

### 1. Our native hook layer is still incomplete

This is the biggest gap.

Current state in `src/ScumOxygen.Native/src/hooks.cpp`:
- `HookChatMessage()` is still effectively unimplemented.
- `HookProcessEvent()` finds the address and stops at `TODO: Implement generic ProcessEvent detour`.

Impact:
- we do not have a generic event stream for gameplay events
- we cannot reliably populate live hooks like inventory open, lockpick, respawn, melee
- we are forced to overuse log parsing and partial memory polling

### 2. Our `PlayerBase` was too thin

Before this pass, our API exposed only a subset of what Oxygen-documented plugins expect:
- no `PermissionManager`
- no `DeathData`
- no `PlayerRespawnData`
- no `PlayerLockPickData`
- no `Item`
- no usable `Inventory.All`
- no `After(...)` helper on the base plugin class

Impact:
- public Oxygen plugins or examples either fail to compile or need manual rewriting

### 3. Our permission model was far behind Oxygen's documented model

Docs and public `AdminManager.cs` assume:
- user permissions
- group permissions
- wildcard matching
- group membership editing
- persistent files in `oxygen/data/oxygen.users.json` and `oxygen/data/oxygen.groups.json`

Previous local implementation was only:
- one flat dictionary of `userId -> permissions[]`
- no groups
- no wildcard resolution
- no `PermissionManager`

Impact:
- admin permission workflows from public Oxygen plugins could not behave like the original

### 4. Our inventory model is not yet native-backed

Original Oxygen docs describe:
- nested inventory traversal
- item destruction
- durability changes
- ammo changes

Current state after this pass:
- API shape exists
- synthetic inventory can be built from known runtime data
- but true item/entity-backed inventory is still missing

Impact:
- plugin compatibility is improved at compile-time
- runtime parity is still incomplete until native entity extraction is implemented

### 5. Our map is still assembled from mixed sources

Original Oxygen-style control expects accurate world objects.

Current state:
- players: mixed native + db + log-derived
- world layers: largely database-driven
- actor-level live extraction is still partial

Impact:
- map markers drift
- missing/incorrect world object placement
- weaker player equipment context

## Changes completed in this pass

### API compatibility

Added/expanded:
- `PermissionManager`
- `DeathData`
- `PlayerRespawnData`
- `PlayerLockPickData`
- `Item`
- richer `PlayerInventory`
- richer `PlayerBase`
- `After(float seconds, Action action)` helper
- extra hook signatures on `OxygenPlugin`

### Permission system

Rebuilt permission storage toward Oxygen-style behavior:
- user file: `oxygen.users.json`
- group file: `oxygen.groups.json`
- wildcard support
- group inheritance support
- group membership editing support
- legacy import from old flat `permissions.json`

### Runtime compatibility

Added overload-safe plugin invocation and compatibility adaptation so documented hook styles are easier to support without breaking current runtime dispatch.

## Verification completed

1. `ScumOxygen.Core` builds successfully in Release.
2. Public `AdminManager.cs` from the Oxygen plugins repository now compiles against this project.
3. A synthetic compatibility plugin using:
   - `OnPlayerOpenInventory`
   - `OnPlayerRespawn`
   - `OnPlayerLockPickEnded`
   - `OnPlayerDeath(PlayerBase, DeathData)`
   - `Inventory.All`
   - `GiveItem(...).SetDurability(...)`
   - `After(...)`
   also compiles successfully.

## What is still blocking full parity

1. `ProcessEvent` detour is still not implemented.
2. Actor and inventory extraction still need real native entity plumbing.
3. Plugin-facing web routing (`StartWebServer`, `WebRoute`) is not yet implemented in original-style form.
4. Whole-solution build still has an unrelated target-framework mismatch:
   - `ScumOxygen.Core` targets `net8.0-windows7.0`
   - some sibling projects still target `net8.0`

## Priority order from here

1. Finish `ProcessEvent` detour and function/event name resolution.
2. Turn native events into structured managed dispatch for:
   - respawn
   - inventory open
   - lockpick
   - melee
   - mini games
3. Replace synthetic inventory with entity-backed inventory snapshots.
4. Extend live actor enumeration for map layers.
5. Add plugin-level web route support compatible with Oxygen docs.
