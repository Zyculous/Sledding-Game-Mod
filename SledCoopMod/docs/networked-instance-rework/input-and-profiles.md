# Input and Profiles

## Input isolation

Each child process receives one assigned device through `--sledcoop-device`.
The host should ignore devices assigned to child processes. Each child should ignore
devices not assigned to it.

Initial mapping:

- slot 1 -> `Gamepad0`
- slot 2 -> `Gamepad1`
- slot 3 -> `Gamepad2`

## Runtime patch target

`PlayerLocalInput` is the safest high-level hook point. The new mode should filter
its movement, look, pause, interact, inventory, and action reads by process role and
assigned device.

## Profile isolation

Each child gets `--sledcoop-profile=GuestN`. Later implementation should redirect
guest save/settings/stat paths under a mod-owned profile directory.

Suggested layout:

```text
Application.persistentDataPath/SledCoopMod/NetworkedProfiles/Guest1/
Application.persistentDataPath/SledCoopMod/NetworkedProfiles/Guest2/
Application.persistentDataPath/SledCoopMod/NetworkedProfiles/Guest3/
```
