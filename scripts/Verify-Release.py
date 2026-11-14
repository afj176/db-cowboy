"""Checks a published release zip: python -I scripts/Verify-Release.py <version>

Run it after scripts/Publish.ps1. It checks that the zip is intact, that the app's own files are present,
that no profile data, excluded component or debug symbol is inside, and that any .sha256 files next to
the zip and installer match.
"""
from pathlib import Path
import hashlib, json, re, sys, zipfile

root = Path(__file__).resolve().parents[1]
if len(sys.argv) != 2:
    sys.exit('usage: Verify-Release.py <version>')
version = sys.argv[1]
payload = root / 'artifacts' / f'DbCowboy-{version}-win-x64'
zip_path = Path(str(payload) + '.zip')

REQUIRED = ['DbCowboy.exe', 'DbCowboy.dll', 'DbCowboy.pri', 'App.xbf', 'README.md', 'THIRD-PARTY-NOTICES.md',
            'Assets/Rowdy.ico', 'Assets/DbCowboy.ico', 'Assets/Manrope.ttf']
# Profile data, the WebView2 libraries, the Windows AI and ML components of the full Windows App SDK
# package (the app references only the components it uses) and debug symbols.
FORBIDDEN = re.compile(r'^(data|ui-qa-data)/'
                       r'|(^|/)(connections|workspace|history)\.json(\.tmp)?$|(^|/)crash\.txt$'
                       r'|WebView2|onnxruntime|DirectML\.dll$|Microsoft\.ML\.|Microsoft\.Windows\.AI\.|(^|/)workloads[^/]*\.json$'
                       r'|\.pdb$', re.IGNORECASE)

with zipfile.ZipFile(zip_path) as archive:
    assert archive.testzip() is None, 'zip CRC check failed'
    # Compress-Archive puts everything under the payload folder name.
    names = [n.replace('\\', '/').split('/', 1)[1] for n in archive.namelist() if '/' in n.replace('\\', '/') and not n.endswith(('/', '\\'))]
    missing = [r for r in REQUIRED if r not in names]
    assert not missing, f'missing from the zip: {missing}'
    forbidden = [n for n in names if FORBIDDEN.search(n)]
    assert not forbidden, f'must not ship: {forbidden[:20]}'

checked = []
for artifact in [zip_path, root / 'artifacts' / f'DbCowboy-{version}-Setup-x64.exe']:
    sidecar = Path(str(artifact) + '.sha256')
    if artifact.exists() and sidecar.exists():
        assert sidecar.read_text().split()[0].lower() == hashlib.sha256(artifact.read_bytes()).hexdigest(), f'{sidecar.name} does not match'
        checked.append(sidecar.name)

result = {'version': version, 'zip_crc': 'PASS', 'required_files': 'PASS', 'nothing_forbidden': 'PASS',
          'sha256_checked': checked, 'zip_sha256': hashlib.sha256(zip_path.read_bytes()).hexdigest()}
(root / 'artifacts' / f'DbCowboy-{version}-release-validation.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
print(json.dumps(result))
