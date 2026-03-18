# NeDjin Relay

`NeDjin Relay` is a private dedicated-server runtime built around a native in-process bridge plus a local control panel.

Public repository:
- [https://github.com/noginegun-web/project](https://github.com/noginegun-web/project)

## Current direction

The project is no longer being treated as a simple log parser or database wrapper.
The target architecture is now:

1. Native in-process hooks
2. Managed runtime and plugin host
3. Hot-reloadable `.cs` plugins
4. Web panel for live control, map, players, chat, and plugin editing

That is the only path that can realistically reach full runtime-level behavior.

## What already works

- Drop-in hosted package for FTP-only game hosting
- `version.dll` bootstrap loading inside `SCUM/Binaries/Win64`
- Embedded local web API and panel
- Hot-save / reload flow for `.cs` plugin files
- Telegram-connected OpenClaw workspace for project continuity
- role and permission groundwork:
  - `oxygen.users.json`
  - `oxygen.groups.json`
  - wildcard permissions
  - group inheritance
- compatibility surface for the current script/runtime model

## What is still incomplete

The project is not yet at full feature parity with the reference implementation.

The biggest remaining gaps are:

- generic `ProcessEvent` detour
- real native gameplay event dispatch
- true live equipment and inventory extraction
- actor-backed world map layers
- richer plugin-facing web API/hooks

Until those are complete, any DB/log-based feature should be treated as fallback only.

## Architecture

Main components:

- `src/ScumOxygen.ServerProxy`
  - hosted bootstrap loader via `version.dll`
- `src/ScumOxygen.Native`
  - native bridge, memory access, hook plumbing
- `src/ScumOxygen.Core`
  - runtime, plugin host, permissions, web API, snapshots
- `src/ScumOxygen.Control`
  - panel assets (`wwwroot`)
- `scripts/Build-HostedPackage.ps1`
  - assembles the hosted drop-in package

## Hosted package

The build script prepares a package for:

- `SCUM/Binaries/Win64/version.dll`
- `SCUM/Binaries/Win64/NeDjin/...`

Default hosted runtime behavior:

- `ServerName` is auto-detected from the server `ServerSettings.ini`
- `ServerId` is auto-generated from the real server name unless explicitly configured
- `Web = http://+:8090/`

## Research policy for this repo

We are explicitly tracking:

- what worked
- what failed
- why it failed
- what should not be repeated

Current research files:

- [reports/research_log.md](reports/research_log.md)
- [docs/engineering_journal.md](docs/engineering_journal.md)

## Next engineering milestone

The next real milestone is not cosmetic UI work.
It is:

1. finish `ProcessEvent` detour
2. map native events to managed hooks
3. replace synthetic inventory/map sources with actor-backed live data
4. bring web features on top of those native snapshots

## Notes

If you are browsing this repository as an external reviewer:

- the current codebase is a live research project
- parity with the reference implementation is the goal, not the current state
- the most important unfinished work is in the native hook pipeline
