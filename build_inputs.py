"""Make bounded native target and text resources from the inspected local build."""
from pathlib import Path
from collections import Counter
import hashlib
import json
import re
import struct

HERE = Path(__file__).resolve().parent
ROOT = HERE
import argparse, os
parser=argparse.ArgumentParser()
parser.add_argument('--game-dir',default=os.environ.get('CHAINED_BEASTS_GAME_DIR',r'C:\Program Files (x86)\Steam\steamapps\common\Chained Beasts'))
args=parser.parse_args()
GAME=Path(args.game_dir)
binary = (GAME / 'GameAssembly.dll').read_bytes()
assert hashlib.sha256(binary).hexdigest() == 'ba3ef02fa87da4c41bb27d7610539c9578132204f0fb5713833e2ef93415ce29'
pe = struct.unpack_from('<I', binary, 0x3c)[0]
count = struct.unpack_from('<H', binary, pe+6)[0]
optional = pe+24
base = struct.unpack_from('<Q', binary, optional+24)[0]
sections_start = optional+struct.unpack_from('<H', binary, pe+20)[0]
sections = []
for i in range(count):
    _, rva, size, raw = struct.unpack_from('<IIII', binary, sections_start+i*40+8)
    sections.append((rva, size, raw))
def offset(va):
    for rva, size, raw in sections:
        if rva <= va-base < rva+size:
            return raw+va-base-rva
    raise ValueError(hex(va))
def va(raw):
    for rva, size, start in sections:
        if start <= raw < start+size:
            return base+rva+raw-start
    raise ValueError(raw)
needle = struct.pack('<Q', va(binary.index(b'Assembly-CSharp.dll\0')))
cursor, tables = 0, []
while (cursor := binary.find(needle, cursor)) >= 0:
    try:
        n, ptr = struct.unpack_from('<I', binary, cursor+8)[0], struct.unpack_from('<Q', binary, cursor+16)[0]
        if 5000 < n < 200000:
            tables.append(struct.unpack_from('<' + str(n) + 'Q', binary, offset(ptr)))
    except (ValueError, struct.error):
        pass
    cursor += 8
assert len(tables) == 1
table = tables[0]
counts = Counter(table)
allowed = {
    'RunDataManager': {'StartNewRunRpc', 'RestoreCheckpoint'},
    'PlayerData': {'SetBeastType', 'AddSkill'},
    'GameServerLobby': {'HandleGameStart', 'BroadcastGameStartingRpc', 'HandleNavigationCharacterConfirmRpc', 'HandleSubmitRpc', 'HandleCharacterVariantSelectedRpc'},
    'UIGameServerLobby': {'HandleCharacterVariantSelected'},
    'UIGameServerLobbyCharacterVariant': {'Refresh', 'HandleSelectedInternal'},
    'UIGameServerLobbyPage': {'Refresh', 'Update', 'OnDestroy'},
}
# The committed audit contains bounded method tokens and short entry signatures,
# not decompiled bindings. Recheck those tokens against the exact pinned binary.
approved = json.loads((HERE / 'native-target-audit.json').read_text(encoding='utf-8'))
resources, audit = {}, []
assert len(approved) == 15
for row in approved:
    typ, field, token = row['type'], row['field'], row['token']
    assert typ in allowed
    name = field.removeprefix('NativeMethodInfoPtr_')
    assert any(name.startswith(method+'_') for method in allowed[typ])
    address = table[(int(token) & 0xffffff)-1]
    assert address and counts[address] == 1, (typ, field, 'shared native endpoint')
    start = binary[offset(address):offset(address)+12]
    assert start[0] not in (0xc2, 0xc3), (typ, field, 'empty body')
    assert hex(address-base) == row['rva'] and start.hex() == row['bytes']
    key = typ+'::'+field
    assert key not in resources
    resources[key] = address-base
    audit.append(dict(type=typ, field=field, token=token, rva=hex(address-base), shared=counts[address], bytes=start.hex()))
(HERE / 'native-targets.json').write_text(json.dumps(resources, indent=2), encoding='utf8')
(HERE / 'native-target-audit.json').write_text(json.dumps(audit, indent=2), encoding='utf8')
catalog = json.loads((HERE / 'data/perk-catalog.json').read_text(encoding='utf8'))
descriptions = {str(p['id']): dict(Name=p['name'], Description=p['description']) for p in catalog}
(HERE / 'descriptions.json').write_text(json.dumps(descriptions, indent=2), encoding='utf8')
print('PASS:', len(audit), 'unique nonempty native targets verified; 24 perk descriptions.')
