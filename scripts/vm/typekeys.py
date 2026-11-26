# Reads text on stdin and prints one "qm sendkey <vmid> <key>" line per character (US layout),
# then one for Enter. Pipe the output into a shell on the Proxmox host:
#   python3 typekeys.py [vmid] < secret-file | ssh root@192.168.1.98 "sh -s"
# Use it for a password: the text never shows on a command line or in a log.
import sys

vmid = sys.argv[1] if len(sys.argv) > 1 else '102'
special = {'!':'shift-1','@':'shift-2','#':'shift-3','$':'shift-4','%':'shift-5','^':'shift-6','&':'shift-7','*':'shift-8','(':'shift-9',')':'shift-0',
           '-':'minus','_':'shift-minus','=':'equal','+':'shift-equal','.':'dot',',':'comma','/':'slash','?':'shift-slash',';':'semicolon',':':'shift-semicolon',
           "'":'apostrophe','"':'shift-apostrophe',' ':'spc','[':'bracket_left',']':'bracket_right','\\':'backslash','`':'grave_accent','~':'shift-grave_accent',
           '<':'shift-comma','>':'shift-dot','{':'shift-bracket_left','}':'shift-bracket_right','|':'shift-backslash'}
text = sys.stdin.read().rstrip('\r\n')
for c in text:
    if c.isascii() and (c.islower() or c.isdigit()): k = c
    elif c.isascii() and c.isupper(): k = 'shift-' + c.lower()
    elif c in special: k = special[c]
    else: sys.exit('unsupported character')
    print(f'qm sendkey {vmid} {k}')
print(f'qm sendkey {vmid} ret')
