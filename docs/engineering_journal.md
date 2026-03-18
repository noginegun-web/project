# Engineering Journal

This file exists to stop repeated mistakes.

## Ground rules

1. Prefer hook-first/native-first design.
2. Treat database and log parsing as fallback, not primary truth.
3. Do not claim Oxygen parity until the feature is verified on a real running server.
4. Every important failure must leave a note here or in `research_log.md`.

## Proven lessons

### 2026-03-18 - Oxygen parity reality check

What worked:
- Public Oxygen docs and plugins are enough to reconstruct a meaningful compatibility layer in managed code.
- Permission compatibility can be brought much closer to Oxygen without touching native code first.
- Public plugin examples are valuable compile-time validation targets.

What did not work:
- Treating DB/log derived state as if it were equal to live process state.
- Assuming web parity can be reached before native event parity.

Rule:
- If a feature depends on precise player state, inventory, world actors, or live chat hooks, it must be backed by the process, not just by logs or SQLite.

### 2026-03-18 - Server identity consistency

What worked:
- Replacing hardcoded identity with runtime auto-detection from real SCUM server settings.

What did not work:
- Leaving `server-1`, `scum-server-1`, and `SCUM Server` in different layers.
- Hardcoding one specific hosted server name into shared source code.

Rule:
- Server identity must come from the real environment first:
  - `ServerName` from `Saved/Config/WindowsServer/ServerSettings.ini`
  - `ServerId` derived from the real name unless explicitly overridden

### 2026-03-18 - Public project presentation

What worked:
- Making the repository link shareable so external review is possible.
- Rewriting README to match current architecture instead of legacy injector-era docs.

What did not work:
- Keeping outdated project instructions in public view.

Rule:
- Public-facing docs must reflect the current architecture, not historical experiments.
