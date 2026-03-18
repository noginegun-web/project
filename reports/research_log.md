# Research Log


## 2026-03-17 17:41:42
- Task: End-to-end functional check (server + control panel) and fix failures.
- Plan: Start server, verify plugin connect, test panel API commands, inspect logs, fix.


### Results
- Success: Control panel running on port 18800 and reachable.
- Success: SCUMServer launched (PID observed) and ControlClient connected (server appears in /api/servers).
- Success: Plugins compiled and loaded (anti-vpn, CustomSpawnItems, DynamicWeather, fastTravel, ItemRestriction, PrivateMessages, SamplePlugin, sethome, VehicleRental).
- Success: Command pipeline works via file-queue + UE4SS bridge (announce OxygenTest executed; UE4SS log confirms).
- Partial: /api/chat returns empty when no chat log entries; needs in-game activity.
- Partial: /api/players empty without active players; RCON disabled so only login log based updates.
- Partial: /api/squads returns empty (placeholder).


### Attempt
- Action: Rebuilt ScumOxygen.Core and tried hot-copy into server folder while SCUMServer running.
- Result: Failed to copy DLL/PDB (file in use by SCUMServer).
- Fix plan: Stop SCUMServer, replace files, restart server.


### Results
- Success: SCUM client started with -log -fileopenlog (SCUM.exe running, client log updated at C:\Users\User\AppData\Local\SCUM\Saved\Logs\SCUM.log).
- Note: Server log shows bind on port 7777.
- Known issue: ReloadPlugin currently unloads but does not re-load (needs server restart to deploy fix).


## 2026-03-17 17:49:37
- Task: Investigate incorrect IP for client connect and server crash.


### Fix
- Change: Guard plugin command/hook/invoke with try/catch to prevent unhandled exceptions from crashing server.

### Results
- Crash evidence: CrashContext shows unhandled CLR exception 0xe0434352 in SCUMServer (coreclr). Likely unhandled exception from managed plugin code.
- Fix applied: Wrapped plugin invoke/commands/hooks with try/catch to prevent managed exceptions from crashing server.
- Control panel restart required after server restart; panel now reachable and server connected.
- IPs discovered: LAN 192.168.3.2, WSL 172.19.240.1, VPN 172.16.0.1. Server bound to 0.0.0.0:7777 (see SCUM.log).


## 2026-03-17 17:59:43
- Task: Diagnose client connection failure to local SCUM dedicated server.
- Plan: Inspect SCUMServer command line, log ports, UDP listeners, config files; verify expected connect IP/port.


### External Research
- Source: Host Havoc SCUM connect guide -> client connection port is configured port + 2.
- Source: SCUM dedicated server setup wiki -> UDP game port (7777), raw UDP port (7778), TCP query port (7779) and note query port constraints. 


### Fix
- Action: Restart server with -nobattleye to prevent BattlEye kicks ('Game restart required').

### Result
- Failure cause: Server log shows BattlEye kicking player with reason 'Game restart required' immediately before disconnect. Likely BE update/installation issue.


### Fix
- Action: Started BattlEye service (BEService) and restarted SCUMServer without -nobattleye to avoid BE 'Game restart required' kicks.

### Evidence
- Server log shows BattlEye kick: 'Kicked player log323 for the following reason: Game restart required' right before disconnect.
- Client log shows successful join to 192.168.3.2:7777 then ConnectionLost after kick.
- Action: Started BEService (Battleye service) and restarted SCUMServer.


## 2026-03-17 18:21:22
- Task: Fix BE kick ('Game restart required') despite client BE installed; verify server BattlEye files and version.


### Diagnosis
- Server log shows BE kick 'Game restart required' right before disconnect. Likely BattlEye server components missing/outdated on dedicated server install.


### Fix Attempt
- Action: Update dedicated server via SteamCMD (appid 3792580) to ensure BattlEye server files present and up to date.

## 2026-03-17 18:29:12 - SCUM client disconnect investigation
**Success:**
- Found client log error: BattlEye communication component can't access BattlEye client (client log).
- Verified server BattlEye initialized successfully (server log).
- SteamCMD update completed; appmanifest buildid 22072605.
- BEService started (client).

**Failed/Blocked:**
- Server-side BattlEye logs not found under D:\SCUM_Dedicated\BattlEye (no *.log present).
- Unable to set BEService startup type to Automatic (access denied without elevation).

**Actions Taken:**
- Ran Install_BattlEye.bat from client SCUM folder (RunAs).
- Started BEService.
- Collected steamcmd content_log updates and confirmed install path contents.


## 2026-03-17 18:31:42 - Root cause identified
**Success:**
- Found client was launched with -nobattleye (confirmed via Win32_Process command line).
- Restarted SCUM client without -nobattleye; kept -fileopenlog.
- SCUMServer listening on UDP 7777 and 27015.

**Next:**
- Connect to 192.168.3.2:7777 and verify no BattlEye error in client log.


## 2026-03-17 18:45:03 - Server name + UE4SS client cleanup
**Success:**
- Set scum.ServerName=KolinsFer in ServerSettings.ini.
- Restarted SCUMServer with -fileopenlog and ports 7777/27015.
- Moved client UE4SS files out of SCUM to avoid BattlEye kicks.

**Details:**
- UE4SS backup path: C:\Users\User\Desktop\UE4SS_BACKUP_2026-03-17


## 2026-03-17 19:30:13 - YouTube Oxygen channel attempt
**Success:**
- Retrieved video list via yt-dlp (7 videos) from channel @jemixs9655.

**Failed/Blocked:**
- YouTube metadata/subtitles download timed out (bmTyX7SPVp4) despite retries; unable to fetch descriptions/subtitles.

**Next:**
- Need specific video URLs or longer/alternative access to pull transcripts for deep analysis.


## 2026-03-17 19:48:08 - Kill log parsing added
**Success:**
- Added KillInfo API + OnPlayerDeath/OnPlayerKill hooks.
- Added kill_*.log and event_kill_*.log tailers with JSON parser.
- Built ScumOxygen.Core and deployed to server + deploy bundle.

**Failed/Blocked:**
- YouTube video analysis still blocked by timeouts; videos appear to be silent (no transcript).


## 2026-03-17 21:15:00 - Panel without server-side .exe
**Success:**
- Перевёл панель на прямое HTTP API внутри плагина (без ScumOxygen.Control).
- Добавил CORS + API key защиту + allowlist IP в WebApiService.
- Добавил endpoints: /api/servers, /api/players, /api/chat, /api/kills, /api/squads, /api/command, /api/broadcast.
- Реализован вывод отрядов через SCUM.db (SQLite). При ошибке возвращает пустой список и логирует.
- Панель обновлена: поля IP:PORT + API key, сохранение в localStorage, прямые запросы к API.
- Обновлён deploy: LOCAL_PANEL (HTML/JS), runtime.json (новые поля), control.json отключён.

**Failed/Blocked:**
- HTTP порт на хостинге может быть закрыт firewall'ом — требуется проверка после выгрузки (без теста на хостинге).


## 2026-03-17 21:48:00 - Hosted bootstrap fix for FTP-only SCUM hosting
**Success:**
- Найдена точная причина падения стартового пакета на хостинге: `version.dll` зависел от системного `nethost.dll`, а пакет не содержал app-local .NET runtime.
- Убрана зависимость от `nethost.dll`: proxy теперь грузит `hostfxr.dll` из `ScumOxygen\dotnet\host\fxr\<version>\hostfxr.dll`.
- В пакет добавлен app-local runtime `Microsoft.NETCore.App 8.0.25`, чтобы хостингу не требовалась установленная .NET 8.
- Исправлено несовпадение путей: managed-часть теперь работает от `Win64\ScumOxygen\...`, а не от `Win64\...`.
- `HttpListener` заменён на встроенный TCP HTTP server без URL ACL, чтобы веб-панель не требовала `netsh http add urlacl` на хостинге.
- Drop-in пакет локально протестирован через загрузку `version.dll`: bootstrap прошёл, `Oxygen.log` создан, `/api/status` ответил `{\"rcon\":false,\"players\":0}`, `/` вернул HTTP 200.

**Failed/Blocked:**
- Хостинг всё ещё может блокировать входящий TCP-порт 8090 firewall'ом; это нельзя подтвердить локально без реального деплоя.
- `DynamicWeather.cs` и `SamplePlugin.cs` дают `NullReferenceException` в `OnLoad/OnPluginInit` в тестовом bootstrap-сценарии, но ядро не падает, потому что ошибки изолированы try/catch.

**What changed:**
- `src/ScumOxygen.ServerProxy/version.cpp`
- `src/ScumOxygen.ServerProxy/CMakeLists.txt`
- `src/ScumOxygen.Core/OxygenPaths.cs`
- `src/ScumOxygen.Core/OxygenRuntime.cs`
- `src/ScumOxygen.Core/WebApiService.cs`
- `src/ScumOxygen.Core/ApiController.cs`
- `scripts/Build-HostedPackage.ps1`
- `scripts/Test-HostedBootstrap.ps1`


## 2026-03-18 11:55:00 - Oxygen parity audit + compatibility foundation
**Success:**
- Pulled and reviewed original Oxygen docs/public plugin sources:
  - installing plugins
  - player API
  - web API
  - actors
  - hook docs
  - public `oxygen.plugins`
- Identified the main architectural gap:
  - our native hook layer still stops at `HookProcessEvent()` TODO
  - `HookChatMessage()` is also not finished
- Rebuilt permissions toward Oxygen-style behavior:
  - `oxygen.users.json`
  - `oxygen.groups.json`
  - wildcard support
  - group inheritance
  - legacy import from flat permissions
- Added missing compatibility surface for public Oxygen-style plugins:
  - `PermissionManager`
  - `DeathData`
  - `PlayerRespawnData`
  - `PlayerLockPickData`
  - `Item`
  - expanded `PlayerInventory`
  - expanded `PlayerBase`
  - `After(...)` helper
  - extra `OxygenPlugin` hook signatures
- Verified `ScumOxygen.Core` builds in Release.
- Verified public `AdminManager.cs` from `oxygen.plugins` now compiles against our core.
- Verified a synthetic docs-compat plugin compiles with:
  - `OnPlayerOpenInventory`
  - `OnPlayerRespawn`
  - `OnPlayerLockPickEnded`
  - `OnPlayerDeath(PlayerBase, DeathData)`
  - `Inventory.All`
  - `GiveItem(...).SetDurability(...)`
  - `After(...)`

**Failed/Blocked:**
- Full solution build still blocked by pre-existing target mismatch:
  - `ScumOxygen.Core` = `net8.0-windows7.0`
  - sibling projects/tests still reference `net8.0`
- Runtime parity is still incomplete because API compatibility is now ahead of native data extraction.
- Full Oxygen-style web/plugin parity still needs:
  - real ProcessEvent-based hooking
  - entity-backed inventory
  - actor-backed live map layers
  - plugin-level `StartWebServer/WebRoute`

**Files changed:**
- `src/ScumOxygen.Core/PermissionService.cs`
- `src/ScumOxygen.Core/PermissionManager.cs`
- `src/ScumOxygen.Core/OxygenAPI.cs`
- `src/ScumOxygen.Core/OxygenAttributes.cs`
- `src/ScumOxygen.Core/OxygenRuntime.cs`
- `reports/oxygen_parity_gap_2026-03-18.md`


## 2026-03-18 13:40:00 - OpenClaw Telegram and project hygiene
**Success:**
- Enabled the built-in OpenClaw Telegram plugin and added the bot as a real channel account.
- Approved the owner's Telegram sender ID through OpenClaw pairing.
- Verified Telegram channel status: running, polling, works.
- Sent messages through OpenClaw itself instead of one-off Bot API calls.
- Made the GitHub repository public so the project can be reviewed by link.
- Standardized default project/server identity for runtime and hosted package generation:
  - `ServerId = kolinsfer-main`
  - `ServerName = KolinsFer`
- Replaced outdated public README with a current architecture/status document.
- Added `docs/engineering_journal.md` to preserve proven rules and avoid repeating mistakes.

**Failed/Blocked:**
- `gh` CLI was not authenticated, so repository visibility had to be changed via Git credential manager backed API access instead.
- Telegram allowlist persistence in OpenClaw config behaved differently than expected; pairing approval works and channel is operational, but config semantics need a deeper pass later if we want strict allowlist-only DM policy.

**Operational lesson:**
- OpenClaw channel support may exist in the installation but still be invisible until the corresponding plugin is both enabled and explicitly allowlisted.


## 2026-03-18 14:05:00 - Server identity autodetection
**Success:**
- Removed the wrong hardcoded hosted server identity from runtime defaults and hosted package generation.
- Added runtime server identity resolution from real SCUM config:
  - `Saved/Config/WindowsServer/ServerSettings.ini`
  - fallback to configured values only when explicitly set
- Added automatic `ServerId` slug generation from the detected real server name.
- Extended runtime status payload with:
  - `serverId`
  - `serverName`
  - `serverIdentitySource`
- Core build verified after the change.

**Why this matters:**
- The web panel should reflect the actual hosted SCUM server, not a name accidentally baked into a previous local test package.

**Files changed:**
- `src/ScumOxygen.Core/RuntimeConfig.cs`
- `src/ScumOxygen.Core/ControlClient.cs`
- `src/ScumOxygen.Core/OxygenRuntime.cs`
- `src/ScumOxygen.Core/ApiController.cs`
- `scripts/Build-HostedPackage.ps1`


## 2026-03-18 14:25:00 - Public rebrand to NeDjin Relay
**Success:**
- Rebranded the public-facing project title to `NeDjin Relay`.
- Updated the hosted package layout to deploy under `NeDjin` instead of `ScumOxygen`.
- Updated server proxy bootstrap to prefer `NeDjin` and fallback to `ScumOxygen` for compatibility.
- Updated the web panel branding and install instructions to the neutral runtime name.
- Switched default build/test package paths to `NDJ_RELAY_*`.

**Why this matters:**
- The project should not be trivially discoverable by game-specific branding in public titles and package names.

**Files changed:**
- `src/ScumOxygen.ServerProxy/version.cpp`
- `scripts/Build-HostedPackage.ps1`
- `scripts/Test-HostedBootstrap.ps1`
- `src/ScumOxygen.Control/wwwroot/index.html`
- `src/ScumOxygen.Control/wwwroot/app.js`
- `README.md`

## 2026-03-18 14:25:00 - Command pipeline fix on hosted server
**Success:**
- Identified the key server-command bug: runtime emitted raw SCUM verbs without `#` prefix, so commands could be parsed by plugins but still not execute correctly in SCUM.
- Added central command normalization in `CommandService`:
  - `broadcast ...` -> `#Announce ...`
  - all other raw server commands -> prefixed with `#` unless already prefixed.
- Added live runtime diagnostics API and panel support:
  - `/api/runtime-log`
  - command registration
  - command parse/dispatch
  - normalized command logging
  - sender path logging (`native-pipe`, `file-queue`, `console`).
- Built and published updated runtime package.
- Verified with isolated hosted-like harness:
  - `/hello` from SCUM chat log produced `#Announce Привет из SamplePlugin`
  - `/sethome base` produced `#SendNotification 1 376 "Home 'base' has been successfully set!"`
- Deployed updated runtime/web files to real hosting after stopping the server through hosting panel endpoint.
- Restarted hosted server through hosting panel endpoint.
- Verified hosted API after restart:
  - `/api/status` responds
  - `/api/runtime-log` responds
  - runtime log shows new command registrations and diagnostics
  - hosted web `/api/chat` test returns `native-pipe`
  - diagnostics confirm `broadcast WEB_DIAG_HELLO_20260318` -> `#Announce WEB_DIAG_HELLO_20260318` via `native-pipe`

**Failed/Blocked:**
- Early startup commands from old chat log replay can still hit `file-queue` before native bridge connects; this is acceptable for startup replay but not ideal for perfect parity.
- Full Oxygen parity is still blocked by unfinished native UE hook layer (`ProcessEvent`, inventory/equipment/world actor hooks).

**Important evidence:**
- Hosted runtime diagnostics showed historical command replay:
  - `[EventPump] Chat parsed ... /sethome base`
  - `[CommandPipeline] ... cmd='sethome'`
  - `[CommandService] Normalize 'SendNotification ...' -> '#SendNotification ...'`
- Hosted live API test showed:
  - `POST /api/chat` -> `{"ok":true,"response":"native-pipe"}`
  - diagnostics line: `native-pipe -> #Announce WEB_DIAG_HELLO_20260318`
# Research Log

## 2026-03-18 - Oxygen parity workstream: hook-first runtime

### Sources reviewed

- https://oxymod.com/
- https://docs.oxymod.com/guide/owners/installing-plugins.html
- https://docs.oxymod.com/guide/web-request.html
- https://docs.oxymod.com/guide/actors.html
- https://github.com/Oxygen-SCUM/oxygen.plugins
- Local SCUM UE4SS/CXX dumps in:
  - `E:\SteamLibrary\steamapps\common\SCUM\SCUM\Binaries\Win64\CXXHeaderDump`

### Confirmed findings

- Original Oxygen exposes plugin-facing HTTP routes and server start helpers:
  - `StartWebServer(int port, string token)`
  - `[WebRoute(path, method, requireAuth)]`
- Public docs confirm hot-reload by saving `.cs` plugin files into `SCUM/Binaries/Win64/oxygen/plugins/`.
- Local SCUM dumps provide live class layout data we can trust more than generic UE guesses:
  - `APrisoner._inventoryComponent` at `0x0EA8`
  - `APrisoner._userId` at `0x0ED0`
  - `APrisoner._itemInHands` at `0x18A8`
  - `AConZPlayerController._userProfile` at `0x06D0`
  - `AConZPlayerController._repFamePoints` at `0x07E4`
  - `AConZPlayerController._moneyBalanceRep` at `0x07F0`

### What was implemented from this research

- Native bridge payload schema was updated to match managed DTO expectations.
- Native player snapshots now include Steam IDs from live process memory.
- Plugin web routes were implemented in runtime:
  - attribute discovery
  - auth flag support
  - load/unload lifecycle cleanup
- Added `POST /api/plugin-command` so plugin chat commands can be tested without a human joining the server.

### Validation completed

- Local dedicated server started successfully from `D:\SCUM_Dedicated\SCUM\Binaries\Win64`.
- Verified:
  - `GET http://127.0.0.1:8090/relay/ping`
  - `POST http://127.0.0.1:8090/api/plugin-command`
- Confirmed plugin-command execution in live logs:
  - `/hello`
  - `/travel`
  - `/sethome base`
  - `/homes`

### Remaining gap after this pass

- ProcessEvent detour is still not implemented.
- Chat/inventory/respawn/melee/lockpick hooks still need to move from fallback sources to direct process events.
- Map and equipment still need a stronger native actor/inventory pipeline.
## 2026-03-18 - Preserving live player identity across fallback refresh

### Sources reviewed

- Local project runtime and registry merge paths:
  - `src/ScumOxygen.Core/PlayerRegistry.cs`
  - `src/ScumOxygen.Core/ServerEventPump.cs`
  - `src/ScumOxygen.Core/NativeBridgeService.cs`
  - `src/ScumOxygen.Native/src/dllmain.cpp`
- Local SCUM dump data in `ConZ.hpp`:
  - `APrisoner._serverUserProfileId` at `0x0EE0`
  - `APrisoner._userProfileName` at `0x0EE8`
  - `APrisoner._userFakeName` at `0x0EF8`

### Confirmed findings

- The biggest cause of bad player cards and wrong map/player identity was not only missing hooks.
- A second systemic problem existed in fallback merge logic:
  - live identity entered the runtime
  - DB snapshot poll ran later
  - the runtime overwrote the fresher player name/location with weaker fallback data
- This explains why the panel could temporarily look correct and then drift back to wrong names or stale positions.

### What was implemented from this research

- Native snapshot JSON now carries:
  - `profileName`
  - `fakeName`
  - `databaseId`
- Runtime bridge now consumes those fields and applies them to `PlayerBase`.
- `PlayerRegistry` now preserves live identity and known coordinates when DB fallback is weaker.
- `ServerEventPump` now refreshes the player's live recency marker on parsed chat/login events.
- `ResolveApiPlayer()` now prefers recent live players before DB fallback when no explicit player match is found.

### Validation completed

- Local dedicated server restarted successfully with the new package.
- Verified web/plugin layer still works:
  - `GET /relay/ping`
- Verified synthetic in-game command path:
  - wrote a new Unicode `chat_*.log` line with `/hello`
  - EventPump parsed it
  - command registry executed it
  - output normalized to `#Announce Привет из SamplePlugin`
- Verified post-refresh identity stability:
  - after waiting through another `PollPlayers` cycle, `/api/players` still returned `Name = NeDjin`
  - player source stayed `native`

### Remaining gap after this pass

- Full process-first player/equipment parity still needs:
  - item name resolution from live `AItem*`
  - quick access slots from process memory instead of DB fallback
  - equipment/clothes extraction
  - `ProcessEvent` hook completion
