# Architecture

Networked instance mode uses one Unity process per local player.

```text
Host process
  - role=host
  - starts Tugboat server on 0.0.0.0:7770
  - starts local client to 127.0.0.1:7770
  - launches child processes

Child process N
  - role=client
  - connects to 127.0.0.1:7770
  - owns one native player prefab
```

## FishNet ownership model

Each process owns exactly one local player through the game's normal FishNet flow.
`PlayerSpawner` and the native player prefab create real `NetworkObject` instances.
RPCs and SyncVars run normally because the client connection and ownership are real.

## Transport strategy

v1 uses Tugboat loopback/LAN because it does not require multiple platform identities.
FishyEOS and Steam lobby support remain a v2 target because those systems expect
unique authenticated platform users.

## Runtime roles

Command-line arguments:

- `--sledcoop-role=host|client`
- `--sledcoop-host=127.0.0.1`
- `--sledcoop-port=7770`
- `--sledcoop-slot=N`
- `--sledcoop-profile=GuestN`
- `--sledcoop-device=GamepadN`

Host is the default role when no role argument is present.
