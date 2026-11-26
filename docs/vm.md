# Test VM 102

The rules for tests on the VM are in `CLAUDE.md` ("Test VM"). This file has the setup details and the method for a console test without the user.

## Machine

- Windows 10 22H2 (19045), 8 cores, 6 GB RAM (balloon 3 GB), 64 GB disk.
- Proxmox host pve2 (`root@192.168.1.98`), VM ID 102.
- IP 192.168.1.63 from DHCP. If the address changes, update `HostName` of `winvm` in `~/.ssh/config`, or reserve the address in OPNsense.
- The VM config has a USB device Canon `04a9:1908`. pve2 does not have this device. Ignore the warning.

## Access

- SSH: `ssh winvm`. The user is `nick` (an administrator) with the key `~/.ssh/id_ed25519_winvm`. Commands run in `cmd`.
- The VM has no Git. `scripts/vm-test.sh` copies the working tree with `tar`.
- The VM has the .NET SDK 8.0.428.
- The guest agent runs. From pve2: `qm guest exec 102 -- <command>`.

## Snapshots

| Snapshot | Content |
|---|---|
| `test-base` (2026-11-26) | Windows, OpenSSH, .NET SDK 8.0.428, `Kid`. No Parental Control. Use this one for tests. |
| `clean-before-parental` | Windows only: no OpenSSH, no SDK, no `Kid`. After a rollback to it, SSH does not work. |

Roll back: `ssh root@192.168.1.98 "qm rollback 102 test-base"`.

Make sure that you know what a snapshot contains before you roll back to it.

## Sound

The snapshots have no sound card. To test the mute of a Suspended App, add one and do a full stop and start of the VM:

```sh
ssh root@192.168.1.98 "qm set 102 -audio0 device=ich9-intel-hda,driver=none"
```

With `driver=none` nobody hears the sound. Read the mute state of the audio session in the VM instead (for example with NAudio `AudioSessionManager`).

## Console test without the user

A session lock or a logon must happen in the console, not over RDP. You can drive the console from the Mac through pve2. The scripts for this are in `scripts/vm/` (see its README):

- **Screenshot:** `qm monitor 102` with `screendump /tmp/vm102.ppm`. Copy the file to the Mac with `scp` and convert it with `magick` to PNG.
- **Keys:** `qm sendkey 102 <key>` (for example `ctrl-alt-delete`, `ret`, `shift-a`). Type text as one `sendkey` for each character.
- **Kid logon:** the user has the password of `Kid`. Get it as a secret and send it only through `sendkey`. Do not show it or write it to a file in the repo.
- **Kid on the logon screen:** after a reboot, Windows shows the last user. To show `Kid`, set the LogonUI values in `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI` (`LastLoggedOnUser`, `LastLoggedOnSAMUser`, `LastLoggedOnDisplayName`, `LastLoggedOnUserSID`, `SelectedUserSID`) to `Kid` and its SID.
- **Programs as Kid:** a scheduled task with `/ru Kid /it` runs a program in the console session of `Kid`. Create it over SSH with `schtasks /create ... /ru Kid /it`, then start it with `schtasks /run`.
- **Admin RDP:** a `Kid` logon in the console ends the RDP session of `nick`. SSH keeps working.

## Service on the VM

- The service log is in `C:\ProgramData\ParentalControl\logs\` (the newest file).
- The database is in `C:\ProgramData\ParentalControl\`.
- Test hook for the suspend fallback: set `PARENTAL_CONTROL_TEST_SUSPEND_FAIL=<exe file name>` in the `Environment` value of the service key, then restart the service. Remove the value after the test.
