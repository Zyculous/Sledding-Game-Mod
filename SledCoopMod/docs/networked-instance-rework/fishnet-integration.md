# FishNet Integration

## Host startup

1. Find the active `NetworkManager`.
2. Replace FishyEOS with Tugboat for networked instance mode.
3. Bind Tugboat to `0.0.0.0`.
4. Start `ServerManager`.
5. Start the host `ClientManager` against `127.0.0.1`.
6. Invoke the game's lobby transition bridge so the native scene flow continues.

## Child startup

1. Parse command-line role and connection args.
2. Find the active `NetworkManager`.
3. Replace FishyEOS with Tugboat.
4. Set Tugboat client address to the host address.
5. Start `ClientManager` against host and port.

## Native systems expected to work

- `PlayerSpawner`
- `PlayerControl`
- `PlayerMovement`
- `PlayerCameraControl`
- `PlayerSledController`
- `PlayerReferenceManager`
- snowball/build/race controllers

## Legacy suppressors

Clone suppressors should not be needed in networked instance mode because no
`SledCoopP*` objects are created. Keep them only for legacy clone mode until the
new path is proven.
