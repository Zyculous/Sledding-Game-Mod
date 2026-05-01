# Building

Game scripts for the build mode where players place items in the world.

## Core Types

| Type | Path | Notes |
|------|------|-------|
| `PlayerBuildingController` | `PlayerBuildingController.cs` | Per-player build mode controller. `StartPlacingItem(BuildableItemType)` and `StopPlacingItem(bool)` called via reflection in `LocalCoopActions.HandleBuildAction()`. |
| `BuildableItemType` | `BuildableItemType.cs` | Enum of all placeable item types (Ramp=1, BowlingPin, etc., up to ~32 values). Resolved at runtime via `AccessTools.TypeByName("BuildableItemType")` in `LocalCoopActions`. |
| `BuildableObject` | `BuildableObject.cs` | Component on a buildable world object after placement. Tracks owner, type, and network state. |
| `BuiltObjectManager` | `BuiltObjectManager.cs` | Server-side registry of all placed objects. Handles placement validation and destruction. Requires FishNet server authority. |

## UI

| Type | Path | Notes |
|------|------|-------|
| `BuildableItemButtonUI` | `BuildableItemButtonUI.cs` | Button in the build menu for a specific item type. Relevant for replicating the build item picker UI per guest slot. |
| `FillBuildableItemsActionListHelper` | `FillBuildableItemsActionListHelper.cs` | Populates the build item picker list. |

## Special Buildables

| Type | Path | Notes |
|------|------|-------|
| `BowlingPinBuildable` | `BowlingPinBuildable.cs` | Physics-simulated bowling pin placed via build mode. |
| `PlaceableRaceInteractable` | `PlaceableRaceInteractable.cs` | Race gate/checkpoint placed via build mode. |

## Mod Implementation Status

- [x] `StartPlacingItem` / `StopPlacingItem` invoked via reflection for guest slots
- [x] `BuildableItemType` enum resolved at runtime for safe argument boxing
- [x] `CycleBuildItem(int delta)` API on `LocalCoopActions` to change active item type
- [ ] Placement validated/confirmed by `BuiltObjectManager` (server-authoritative; needs FishNet NO on clone)
- [ ] Build item picker UI per guest slot (IMGUI or world-space panel)
- [ ] `BuildableObject` ownership tracked per guest slot
