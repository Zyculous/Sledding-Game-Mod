# Current State

The current mod is mostly an in-process split-screen system. It starts a local
FishNet backend, clones the host pawn into `SledCoopP*` objects, creates extra
cameras/HUDs, suppresses many native callbacks, and substitutes local movement,
sledding, snowball, and save behavior where FishNet ownership is missing.

## Retire from the primary path

- `LocalPlayerSpawner` clone creation and `SledCoopP*` lifecycle.
- Split-screen camera/HUD duplication for extra local players.
- `LocalCoopMovement`, `LocalSledAdapter`, `LocalSledVisualFactory`, and local-only action substitutes.
- Clone-specific `PlayerControl`, `PlayerCameraControl`, and ownership suppressors.
- FishNet ownership getter patches used to make cloned objects look owned.

## Keep or reuse

- `LocalPlayerManager` as the host-side list of requested local players.
- `InputRouter` as the UI join/leave entry point.
- Tugboat transport setup logic from the offline custom server work.
- Existing diagnostics and AOT-safe reflection helpers.
- EOS/FishyEOS suppressors only for legacy or offline fallback modes.

## Why change

Recent logs show clone-path instability after gameplay starts, including FishNet
and Unity camera teardown errors. The root problem is that local clones are not
normal FishNet clients. The new architecture avoids that by making every extra
player a real connected client process.
