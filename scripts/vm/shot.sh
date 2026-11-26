#!/bin/sh
# Takes a screenshot of the VM console and saves it as PNG: shot.sh <out.png>
# Needs ImageMagick (magick) on the Mac and SSH access to the Proxmox host as root.
set -eu
PVE=${PVE:-root@192.168.1.98}
VMID=${VMID:-102}
out=$1
tmp=$(mktemp -d)
ssh "$PVE" "echo 'screendump /tmp/vm$VMID.ppm' | qm monitor $VMID >/dev/null"
scp -q "$PVE:/tmp/vm$VMID.ppm" "$tmp/vm.ppm"
magick "$tmp/vm.ppm" -resize 60% "$out"
rm -rf "$tmp"
