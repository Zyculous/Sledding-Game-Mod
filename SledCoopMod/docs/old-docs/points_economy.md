# Points Economy

Game scripts that handle the points/score system, achievements, and HUD display of points.

## Core Types

| Type | Path | Notes |
|------|------|-------|
| `CharacterPoints` | `CharacterPoints.cs` | MonoBehaviour on each player pawn; holds live `PointsCurrent`. Source of truth for in-game score. Must be intercepted per guest slot to forward points to `GuestSaveManager`. |
| `PointsController` | `PointsController.cs` | Singleton/manager that distributes points to players based on game events. |
| `PointsReasons` | `PointsReasons.cs` | Enum of all reasons a player can earn points (sled trick, fish catch, snowball hit, etc.). |
| `PointsType` | `PointsType.cs` | Enum distinguishing point categories (XP, currency, etc.). |
| `GetPointsForHittingObject` | `GetPointsForHittingObject.cs` | Component on world objects; awards points when hit. |

## Achievements

| Type | Path | Notes |
|------|------|-------|
| `AchievementController` | (Assembly-CSharp) | Tracks achievement progress per player. Relies on `PlayerSavedStats`. Guest slots need a proxy or bypass. |
| `AchievementType` | (Assembly-CSharp) | Enum of all achievement IDs. |

## HUD

| Type | Path | Notes |
|------|------|-------|
| `UIHUD_Points` | `UIHUD_Points.cs` | Canvas component that displays the live point counter. Cloned per extra player by `HudManager`, but bindings to `CharacterPoints` are for P1 only — needs rebinding per guest slot. |

## Mod Implementation Status

- [x] `GuestPlayerStats` stores `PointsCurrent` + `PointsLifetime` for guest slots
- [x] `GuestSaveManager.AddPoints` / `GuestSaveManager.AddFish` API ready
- [x] IMGUI inventory overlay shows guest points and fish summary
- [x] `CharacterPoints` on clone pawn intercepted and forwarded to `GuestSaveManager` (`Patch_CharacterPoints_AddPoints`)
- [x] `UIHUD_Points` on cloned HUD canvas rebound to guest slot's `CharacterPoints` (`Patch_UIHUD_Points_Bind`)
- [x] `AchievementController` suppressed on clone pawns — prevents achievement writes against P1's save. Guest achievements are not tracked (acceptable for local guests).
