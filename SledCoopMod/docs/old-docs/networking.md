# Networking

Game scripts and libraries for lobby management, server authority, voice, and platform services.

## FishNet (Game Network Library)

FishNet is the game's networking framework. It provides server-authoritative spawning, SyncVars, and RPC routing. Clone pawns currently have no `NetworkObject` so ServerRpc calls are unreachable — local action logic bypasses them via `RpcLogic___` reflection.

## Related split-screen docs

- [Split-screen Architecture](docs/splitscreen_architecture.md)
- [Slot Input Lifecycle](docs/slot_input_lifecycle.md)
- [Network / Fallback](docs/network_fallback.md)
- [Full Support Checklist](docs/full_support_checklist.md)

| Type / Assembly | Notes |
|-----------------|-------|
| `FishNet.Runtime.dll` (416 KB) | Core library. `NetworkManager`, `NetworkObject`, `NetworkBehaviour` all live here. |
| `NetworkManager` | Singleton managing server/client state. Access via `UObject.FindObjectOfType<NetworkManager>()` (used in `SpawnHooks.FindPlayerPrefab()`). |
| `NetworkObject` | Component that gives a GameObject a network identity. Clone pawns lack this — reason ServerRpc calls fail for guests. |
| `NetworkBehaviour` | Base class for any script with ServerRpc / ObserversRpc. `PlayerControl`, `PlayerSledController`, `PlayerHoldingController` etc. all inherit from it. |

## Lobby

| Type | Path | Notes |
|------|------|-------|
| `LobbyManager` | `LobbyManager.cs` | Manages the active session (host/join/leave). Listens for FishNet client connect/disconnect. |
| `LobbySettings` | `LobbySettings.cs` | Static class holding lobby configuration. `ResetStatics()` fires when returning to menu — patched by `Patch_LobbySettings_ResetStatics` to trigger `LocalPlayerManager.ResetForNewSession()`. |
| `ClientLobbyConnectionHandler` | `ClientLobbyConnectionHandler.cs` | Client-side lobby join/reconnect flow. |
| `LobbyHeartbeat` | `LobbyHeartbeat.cs` | Periodic EOS/Steam lobby keepalive ping. |
| `SledLobby` | `SledLobby.cs` | Game-mode-specific lobby wrapper (sled racing). |
| `UICreateLobby` | `UICreateLobby.cs` | UI screen for creating a new lobby. |
| `UILobbyExplorer` | `UILobbyExplorer.cs` | UI screen for browsing/joining lobbies. |

## Boot Sequence

| Type | Path | Notes |
|------|------|-------|
| `BootSceneManager` | `_Scripts.Boot.BootSceneManager` | Orchestrates the boot sequence (auth init, Addressables load, scene transition). `InitializeBootables()` patched by `Patch_BootSceneManager_Init` → calls `SpawnHooks.OnBootComplete()`. |
| `AuthPlatform` | (Assembly-CSharp) | Wraps Steam / EOS SDK auth handshake performed during boot. |

## Platform Services

| Service | Notes |
|---------|-------|
| Steam | Session and lobby via Steamworks.NET. Online hybrid mode (`ModConfig.OnlineHybridEnabled`) is intended to support Steam + local guests in one session. |
| EOS (Epic Online Services) | Party presence, cross-play. `PlayerPartyGroupHandler` wraps EOS party APIs. |

## Voice Chat

| Type / Assembly | Notes |
|-----------------|-------|
| `DissonanceVoip.dll` | Dissonance voice chat library. Integrated per-player via `PlayerVoiceController`. Guest slots currently have `PlayerVoiceController` suppressed — not functional. |

## Mod Implementation Status

- [x] `LobbySettings.ResetStatics` patched to reset mod state on return to menu
- [x] `BootSceneManager.InitializeBootables` patched to run boot-complete hooks
- [x] `NetworkManager` found via `FindObjectOfType` for prefab lookup during spawn
- [x] Clone pawns assigned `NetworkObject` via `TryNetworkSpawnPawn` (server-owned, host mode); FishNet lifecycle callbacks suppressed on `SledCoopP*` roots so game scripts don't fire during spawn registration
- [ ] Sled, snowball, build actions use proper FishNet ServerRpc path for guests
- [ ] Online hybrid: Steam session admits local guests alongside remote players
- [ ] Voice chat (`DissonanceVoip`) wired per guest slot
- [ ] EOS party presence updated for guest slots
