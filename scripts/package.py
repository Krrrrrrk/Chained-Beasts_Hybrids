"""Package the built Hybrids DLL and player guides. Never installs or launches."""
from pathlib import Path
import hashlib,json,zipfile

root=Path(__file__).resolve().parents[1]
version='0.1.2'
dll=root/'bin/Release/net6.0/ChainedBeasts.Hybrids.dll'
out=root/'artifacts'
out.mkdir(exist_ok=True)
entries={'BepInEx/plugins/Hybrids/ChainedBeasts.Hybrids.dll':dll.read_bytes()}
for name in ('README.md','LICENSE','THIRD_PARTY_NOTICES.md'):
    entries[name]=(root/name).read_bytes()
entries['BUILD_INFO.json']=(json.dumps(dict(version=version,gameVersion='1.0.2',steamBuild='26369255',
    foundationMinimum='1.1.2',runtimeVerified=False,gameLaunched=False,
    dllSha256=hashlib.sha256(dll.read_bytes()).hexdigest()),indent=2)+'\n').encode()
archive=out/f'Chained-Beasts-Hybrids-{version}.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
    for name,data in entries.items():z.writestr(name,data)
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None and set(z.namelist())==set(entries)
    for name,data in entries.items():assert z.read(name)==data
print(archive)
