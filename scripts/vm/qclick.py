#!/usr/bin/env python3
# usage: python3 qclick.py <x> <y> [W H] [move]   (screen pixels of the VM console); runs on pve2
import json, socket, sys, time
a = [v for v in sys.argv[1:] if v != 'move']; move_only = 'move' in sys.argv[1:]
x, y = float(a[0]), float(a[1])
W = float(a[2]) if len(a) > 2 else 1024; H = float(a[3]) if len(a) > 3 else 768
s = socket.socket(socket.AF_UNIX); s.connect('/var/run/qemu-server/102.qmp'); f = s.makefile('rw')
def cmd(c):
    f.write(json.dumps(c) + '\n'); f.flush()
    while True:
        r = json.loads(f.readline())
        if 'return' in r or 'error' in r: return r
f.readline(); cmd({'execute': 'qmp_capabilities'})
ax, ay = int(x * 32767 / W), int(y * 32767 / H)
cmd({'execute': 'input-send-event', 'arguments': {'events': [{'type': 'abs', 'data': {'axis': 'x', 'value': ax}}, {'type': 'abs', 'data': {'axis': 'y', 'value': ay}}]}})
if not move_only:
    time.sleep(0.1)
    for down in (True, False):
        print(cmd({'execute': 'input-send-event', 'arguments': {'events': [{'type': 'btn', 'data': {'down': down, 'button': 'left'}}]}}))
        time.sleep(0.1)
