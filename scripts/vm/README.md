# VM test helpers

These helpers run a console test on the test VM (VM 102) from the Mac, without a person at the console. `docs/vm.md` describes the VM and the method. Use them on the test VM only: they change the service database and install the service without the installer.

The Mac scripts use the SSH alias `winvm` and the Proxmox host `root@192.168.1.98`. Set `PVE` and `VMID` to use another host or VM.

## Mac scripts

| Script | What it does |
|---|---|
| `shot.sh <out.png>` | Takes a screenshot of the VM console (`screendump`) and converts it to PNG with `magick`. |
| `qclick.py <x> <y> [move]` | Runs on pve2: an absolute mouse click (or move) on the VM console through the QMP socket. See `docs/vm.md`. |
| `typekeys.py [vmid]` | Reads text on stdin and prints one `qm sendkey` line per character. Pipe the output to the Proxmox host. |
| `kid-run.sh <script.ps1> [args]` | Copies a script from `guest/` to `C:\Users\Public` and runs it as `Kid` in the console session, through a scheduled task with `/ru Kid /it`. |
| `spike.sh deploy\|conf\|kid\|kidcmd\|log` | The spike of `session-probe-and-agent-channel` (task group 1): publishes the spike tools, sets the config of the spike service, runs `SpikeKid` or a command line as `Kid`, prints the service log. The usage is at the top of the script. |

Type the `Kid` password from a secret file. Do not put it on a command line, because the shell history and the logs keep it:

```sh
python3 scripts/vm/typekeys.py < "$SECRET_FILE" | ssh root@192.168.1.98 "sh -s"
```

## Guest scripts (`guest/`)

`scripts/vm-test.sh` copies the working tree to `C:\dev\parental-control`. Run these scripts over SSH as `nick` with `powershell -NoProfile -ExecutionPolicy Bypass -File C:\dev\parental-control\scripts\vm\guest\<script>.ps1`, except where the table says "as Kid".

| Script | What it does |
|---|---|
| `deploy.ps1` | Publishes the service, the agent and the admin from the working tree to `C:\Program Files\ParentalControl`, creates the service like the installer does, and starts it. |
| `kidstart.ps1` | As Kid: starts each program of `kidstart.txt` (copy `kidstart.example.txt`) and brings each to the front for 25 s. |
| `front.ps1 <name>` | As Kid: brings the window of a process to the front. |
| `spike-attack.ps1 <pid> [pipe]` | As Kid: tries to end the spike probe and to open or re-create a channel pipe (task 1.1, 1.3). |
| `resume-process.ps1 <name>` | As Kid: resumes a suspended process with `NtResumeProcess`, as a child with a tool does. |
| `samples.ps1 <exe path>` | Sends agent samples for an exe to `/api/agent/activity`. |
| `kid-study-samples.ps1 [count]` | As Kid: sends a Study sample to `/api/agent/activity` every 5 s and logs each HTTP status to `C:\Users\Public\kid-study-samples.log` (task 8.1: the route gives 404). |
| `decline.ps1 [user] [minutes]` | Sends a Grace Request for the user and declines it through the admin API, so that the agent shows the decline toast. Run `VmTool totp setup` one time before. |
| `kid-mutex-runkey.ps1` | As Kid: adds a `Run` value that starts `kid-mutex.ps1` (copy it to `C:\Users\Public` first) at each sign-in of Kid. |
| `grant.ps1 [userId] [minutes]` | Signs in with the next TOTP code from `VmTool totp code`, then grants Bonus Minutes through the admin API. Run `VmTool totp setup` one time before. |
| `state.ps1` | Shows the service state, the suspended threads of the test programs and the end of the service log. |
| `watch.ps1` | Shows the current app of the session from the dashboard API every 20 s. |

## Tools (`tools/`)

| Tool | What it does |
|---|---|
| `VmTool` | Reads and changes the service database: `restrict`, `usage`, `set-usage`, `records`, `limits`, `sql`, `totp setup [secret]` (sets up the admin sign-in as the Admin app does, with a fixed test secret by default), `totp code` (prints the next code that the login accepts), `msg`. Run it as `nick`: `dotnet run --project scripts\vm\tools\VmTool -- usage Kid`. |
| `TestGame` | A window with a running clock and a 440 Hz tone. A suspended copy shows a frozen clock. The argument `sound=off` starts it without the tone. |
| `AudioState` | Writes the audio sessions of the user with pid and mute state to a file. Run it as Kid to check the mute of a Suspended App. |
| `EndSessionSpike` | Checks the end-session behaviour of Windows (task group 1 of `resume-suspended-apps-at-session-end`): a hidden agent window that logs `WM_QUERYENDSESSION`, test games that log it, suspend, resume and minimize modes, and a test service that logs `PRESHUTDOWN`. The usage is at the top of `Program.cs`. |

| `ProbeSpike` | The spike of the Session Probe (design D2–D4 of `session-probe-and-agent-channel`): a Windows service as LocalSystem that starts the sampler as SYSTEM in the session of `Kid`, reads its lines and reads each entry as D3b states, and a client for the Agent Channel test. Config and log: see `Host.cs`. |
| `SpikeKid` | Helpers that run as `Kid` for the same spike: decoy, hung and long-title windows, `SetParent`, `WS_EX_NOACTIVATE`, desktops, a DACL on a process, CPU load, mouse input, a non-activating widget, a WebView2 app, a game window on a new thread again and again (`rethread`), a window of the class `Ghost` (`ghostclass`). The usage is at the top of `Program.cs`. |
| `ChannelAttack` | As `Kid`: a test client of the Agent Channel (tasks 5.3 and 6.4 of `session-probe-and-agent-channel`). It copies the channel handle of the agent with `DuplicateHandle`, suspends the agent and sends hostile frames: `status [user]`, `prompt <id>`, `sessionend <sessionId> [count] [gap]`, `huge`, `noread [seconds]`, `resume`. Log: `C:\Users\Public\channel-attack.log`. |
| `SpikeHook` | A startup hook DLL that writes a line when a process loads it (task 1.3). |
| `SpikeWinHook` | A native hook DLL and a program that sets a global `WH_GETMESSAGE` and `WH_CBT` hook with it (task 1.3). Build it on the VM with `zig cc` (see the top of each file). |

Install the test programs on the VM (the default `kidstart.example.txt` expects these paths):

```bat
cd /d C:\dev\parental-control
dotnet publish scripts\vm\tools\TestGame -c Release -o C:\Games\TestGame
dotnet publish scripts\vm\tools\TestGame -c Release -p:AssemblyName=Other -o C:\Games\Other
```

Then add the folder rule `C:\Games\` → Games in the web admin.

## A typical run

1. Roll back to `test-base-net10`. Run `scripts/vm-test.sh` to copy the tree.
2. Run `deploy.ps1`, then `VmTool restrict Kid 300`. Run `VmTool totp setup` if the test needs the web admin: without the setup, each admin route gives 401.
3. Log on as `Kid` with `qm sendkey` and `typekeys.py`. Check the screen with `shot.sh`.
4. Run `kid-run.sh kidstart.ps1`.
5. Move the usage with `VmTool set-usage Kid Games <minutes>`. Check the result with `state.ps1`, `VmTool records` and `shot.sh`.
