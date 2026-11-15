#!/usr/bin/env python3
"""Generate a custom Dalamud repo feed from an actual Release build.

Does not publish or access GitHub. Requires a built plugin and its SDK-generated
manifest. Must never generate a fake release from source alone.
"""
import argparse
import json
from pathlib import Path
import shutil
import time
import zipfile

p = argparse.ArgumentParser()
p.add_argument('--repo', required=True, help='GitHub OWNER/REPO')
p.add_argument('--tag', required=True, help='Release tag, e.g. v0.12.4-beta')
p.add_argument('--version', required=True, help='Actual DLL/manifest version')
p.add_argument('--build-root', default='AetherflussGuide/bin')
p.add_argument('--out', default='Release-Output')
a = p.parse_args()
if '/' not in a.repo or ' ' in a.repo or not a.repo.count('/') == 1:
    p.error('--repo must be OWNER/REPO')
root = Path(a.build_root)
out = Path(a.out)
out.mkdir(parents=True, exist_ok=True)

# Prefer official DalamudPackager output; supports runtime DLL dependencies.
packages = sorted((f for f in root.rglob('latest.zip') if 'release' in str(f).lower()),
                  key=lambda f: f.stat().st_mtime, reverse=True)
if not packages:
    raise SystemExit('No SDK-generated latest.zip in Release output. Check Dalamud.NET.Sdk package build; publishing aborted.')
package = packages[0]
with zipfile.ZipFile(package) as z:
    names = [n for n in z.namelist() if not n.endswith('/')]
    manifests = [n for n in names if Path(n).name == 'AetherflussGuide.json']
    dlls = [n for n in names if Path(n).name == 'AetherflussGuide.dll']
    if not manifests or not dlls:
        raise SystemExit('Build ZIP lacks AetherflussGuide.json or AetherflussGuide.dll; aborting.')
    manifest = json.loads(z.read(manifests[0]))
if manifest.get('AssemblyVersion') != a.version:
    raise SystemExit(f'Version mismatch: manifest={manifest.get("AssemblyVersion")} wanted={a.version}')
if manifest.get('InternalName') != 'AetherflussGuide':
    raise SystemExit(f'Unexpected InternalName: {manifest.get("InternalName")!r}')
api = manifest.get('DalamudApiLevel')
if not isinstance(api, int) or api <= 0:
    raise SystemExit('Missing DalamudApiLevel in generated manifest; aborting.')
shutil.copy2(package,out/'latest.zip')
url=f'https://github.com/{a.repo}/releases/download/{a.tag}/latest.zip'
entry=dict(manifest)
entry.update({
    'Name':'Aetherfluss Nexus',
    'Description':'FFXIV-Guides, EasyMedia-Videovorschauen sowie optionale Connect-Kontakte und Nachrichten.',
    'Punchline':'Guides, Videos und Connect direkt im Spiel.',
    'RepoUrl': f'https://github.com/{a.repo}',
    'ApplicableVersion':'any',
    'IsHide':False,
    'IsTestingExclusive':False,
    'TestingAssemblyVersion':None,
    'DownloadLinkInstall':url,
    'DownloadLinkUpdate':url,
    'DownloadLinkTesting':url,
    'LastUpdate':int(time.time()),
})
# Remove path-specific fields/manifest entries if present
for k in ('Path','WorkingPluginId'):
    entry.pop(k,None)
(out/'repo.json').write_text(json.dumps([entry], ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(f'PACKAGE={out / "latest.zip"}')
print(f'FEED={out / "repo.json"}')
print('CUSTOM_REPOSITORY_URL='+f'https://raw.githubusercontent.com/{a.repo}/main/repo.json')
print('VERIFIED_MANIFEST_VERSION='+entry['AssemblyVersion'])
