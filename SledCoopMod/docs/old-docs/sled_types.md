# Sled Types

Game scripts related to sledding, racing, and checkpoints.

## Sled Action

| Type | Path | Notes |
|------|------|-------|
| `PlayerSledController` | `PlayerSledController.cs` | Manages sled equip/unequip state and sled physics. The sled is spawned/despawned as a networked object via FishNet ServerRpc (`Cmd_Sled`). |
| `PlayerControl` | `PlayerControl.cs` | Entry point for sled equip: `RpcLogic___Cmd_Sled___(Vector3 position, Vector3 forward, float speed)` invoked via reflection in `LocalCoopActions.HandleSledAction()`. `SwitchStateToDefault()` used to exit sled state. |
| `SledLobby` | `SledLobby.cs` | Lobby-level sled settings and race mode selection. |

## Racing

| Type | Path | Notes |
|------|------|-------|
| `PlayerRacingController` | `PlayerRacingController.cs` | Per-player race state (current lap, checkpoint index, finish time). Suppressed on clone pawns via `Patch_ClonePawnScriptSuppressor`. Needs per-slot activation to track guest racers. |
| `RaceCheckpoint` | `RaceCheckpoint.cs` | World trigger volume; advances `PlayerRacingController` checkpoint index on overlap. Currently only responds to P1. |
| `PlaceableRaceInteractable` | `PlaceableRaceInteractable.cs` | A buildable race-course element (a checkpoint/gate placed via build mode). |

## Mod Implementation Status

- [x] Sled equip/unequip via reflection (`RpcLogic___Cmd_Sled___`, `SwitchStateToDefault`)
- [x] Local sled physics for guest pawns (`LocalCoopMovement.SledFixedUpdate`): slope-driven gravity, lateral damping, steering, auto-yaw, horizontal speed cap (25 m/s). Networked sled physics object still requires FishNet `NetworkObject`.
- [x] Visual sled primitive (flat cube) spawned on pawn root when sledding; destroyed on unequip (`LocalCoopMovement.UpdateSledVisual`)
- [x] `RaceCheckpoint` overlap detection extended to guest pawns via `GuestRacingTracker` + `Patch_RaceCheckpoint_TriggerEnter`
- [x] Race lap/CP progress shown in per-slot IMGUI badge and inventory overlay
- [ ] Networked sled physics object spawned for guest slots (requires FishNet `NetworkObject` on clone — hard gap)
- [ ] `PlayerRacingController` native race tracking per guest slot (replaced locally by `GuestRacingTracker`)
- [ ] Race results / leaderboard injected with guest data (probe in `Patch_RaceLeaderboard_GuestData`; exact API not yet confirmed)
