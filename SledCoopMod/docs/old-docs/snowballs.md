# Snowballs

Game scripts for snow pickup, snowball throwing, cannons, and snowmen.

## Snow & Throwing

| Type | Path | Notes |
|------|------|-------|
| `PlayerHoldingController` | (on player pawn) | Manages held-item state. Entry point for snowball actions: `RpcLogic___Cmd_StartPickingUpSnow___`, `RpcLogic___Cmd_StopPickingUpSnow___`, `RpcLogic___Cmd_StartChargeThrow___`, `RpcLogic___Cmd_CancelChargeThrow___`, `RpcLogic___Cmd_ThrowObject___`. All invoked via reflection in `LocalCoopActions.HandleSnowballAction()`. |
| `Snowball` | `Snowball.cs` | Projectile component on a thrown snowball. Server-spawned by FishNet via `RpcLogic___Cmd_ThrowObject___`. |
| `SnowballFightInteractable` | `SnowballFightInteractable.cs` | World object that triggers a snowball fight activity. |
| `SnowController` | `SnowController.cs` | World snow coverage state; tracks pickable snow volumes. |
| `Throwable` | `Throwable.cs` | Generic base for throwable objects (snowball inherits or uses this). |
| `ThrowableSpawnsParticles` | `ThrowableSpawnsParticles.cs` | Visual particle system spawned on throw/impact. |
| `BF_PlayerSnow` | `BF_PlayerSnow.cs` | Behaviour Flow node for snow state on a player. |
| `BF_AddSnow` | `BF_AddSnow.cs` | Behaviour Flow node for adding snow to a player or area. |

## Cannon

| Type | Path | Notes |
|------|------|-------|
| `Cannon` | `Cannon.cs` | World cannon that fires snowballs or players. Server-authoritative. |

## Snowman

| Type | Path | Notes |
|------|------|-------|
| `PlayerSnowmanRollingController` | `PlayerSnowmanRollingController.cs` | Player rolls into a snowball to grow it into a snowman base. |
| `SnowmanBall` | `SnowmanBall.cs` | Rolling snowball (snowman body part). |
| `SnowmanState` | `SnowmanState.cs` | State machine for snowman construction progress. |

## Mod Implementation Status

- [x] Snow pickup → charge → throw flow via `RpcLogic___` reflection in `LocalCoopActions`
- [x] Correct throw velocity: `origin = position + forward*0.5 + up*1.4`, `velocity = (forward + up*0.25).normalized * 18`
- [x] Local snowball proxy (`LocalSnowball`) spawned on throw **when offline / server not started**: Rigidbody sphere with `ContinuousDynamic` collision, 5 s lifetime, awards +50 pts on player hit via `GuestSaveManager.AddPoints`.
- [x] Player hit detection in local proxy: checks `root.GetComponent<PlayerControl>() != null || root.name.StartsWith("SledCoopP")` to identify host and clone pawns; self-hit guard (0.15 s ignore window + Thrower reference check)
- [x] **Networked snowball when online/host**: `PlayerHoldingController` FishNet initialization now allowed via `Patch_CloneGameplayOnlySuppressor` (gameplay-only suppression) — `OnStartServer/OnStartClient` run → `RpcLogic___Cmd_ThrowObject___` can call `ServerManager.Spawn(snowballNOB)` for a real FishNet snowball. `LocalSnowball` proxy is skipped when `_isNetworked = true`.
- [ ] Impact hit detection via native `PointsController` (currently bypassed; points go to `GuestSaveManager` directly)
- [ ] `SnowController` volume pickup tracked per guest pawn
- [ ] Cannon interaction functional for guest slots
- [ ] Snowman rolling functional for guest slots (`PlayerSnowmanRollingController`)
