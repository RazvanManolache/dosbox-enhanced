"""Maintainer packaging/publication with source allowlists and private-file audits."""
from pathlib import Path
import argparse,hashlib,json,os,re,subprocess,urllib.request,urllib.parse,zipfile

ROOT=Path(__file__).resolve().parents[1]
VERSION='0.6'
REPO='RazvanManolache/dosbox-enhanced'
OUT=ROOT/'release'/('v'+VERSION)/'prepared'

def git(*args):return subprocess.check_output(['git',*args],cwd=ROOT).decode().strip()
def sha(data):return hashlib.sha256(data).hexdigest()
def tracked():
    for row in subprocess.check_output(['git','ls-tree','-rz','HEAD'],cwd=ROOT).split(b'\0'):
        if row:
            meta,path=row.split(b'\t');mode,kind,oid=meta.split()
            if kind==b'blob':yield path.decode(),oid.decode()

def scan(archive,source=False):
    count=0
    with zipfile.ZipFile(archive) as z:
        for name in z.namelist():
            path=Path(name);base=path.name.lower()
            assert '..' not in path.parts and not path.is_absolute(),name
            assert not any(p.lower() in ('data','.git','obj','bin') for p in path.parts),name
            assert base not in ('graphics.json','nvidia.json','environments.json','preferences.json'),name
            assert not base.startswith('nvngx_'),name
            if source:assert path.suffix.lower() not in ('.exe','.dll','.lib','.cubin','.fatbin','.zip','.rar','.7z','.weights','.onnx','.safetensors'),name
            content=z.read(name)
            for encoding in ('utf-8','utf-16le'):
                text=content.decode(encoding,errors='ignore')
                assert not re.search(r"[A-Za-z]:[/\\]+(?:Users|AI|Games)[/\\]+",text),f'Host-specific path in {name}'
            assert not re.search(rb'(?:ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})',content),name
            count+=1
    return count

def package():
    assert not git('status','--porcelain'),'Commit the reviewed source first.'
    OUT.mkdir(parents=True,exist_ok=True)
    source=OUT/f'DOSBox-Enhanced-{VERSION}-source.zip'
    with zipfile.ZipFile(source,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
        for path,oid in tracked():z.writestr(f'DOSBox-Enhanced-{VERSION}-source/'+path,subprocess.check_output(['git','cat-file','blob',oid],cwd=ROOT))
    portable=OUT/f'DOSBox-Enhanced-{VERSION}-win-x64.zip'
    paths=['app/DosboxEnhanced.exe']+['runtime/'+n for n in ('dosbox_pure_libretro.dll','dosbox_nvngx.dll','phoenix_native.dll')]
    native=ROOT/'runtime/native'
    for file in native.rglob('*'):
        if file.is_file():
            assert file.suffix.lower() in ('.json','.cubin','.fatbin','.ptx'),file.name
            paths.append(file.relative_to(ROOT).as_posix())
    for path,_ in tracked():
        if path.startswith(('docs/','licenses/')) or path in ('README.md','LICENSE','THIRD_PARTY_NOTICES.md','CHANGELOG.md','RELEASE_NOTES.md','DOSBox Enhanced.cmd','DOS Games.cmd','Setup graphics.cmd','setup-graphics.ps1','runtime.lock.json'):
            paths.append(path)
    manifest=[]
    with zipfile.ZipFile(portable,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
        for path in sorted(set(paths)):
            data=(ROOT/path).read_bytes();manifest.append(dict(path=path,bytes=len(data),sha256=sha(data)))
            z.writestr(f'DOSBox-Enhanced-{VERSION}-win-x64/'+path,data)
        z.writestr(f'DOSBox-Enhanced-{VERSION}-win-x64/FILES.json',json.dumps(manifest,indent=2)+'\n')
    archives=[]
    for path,is_source in ((source,True),(portable,False)):
        count=scan(path,is_source);archives.append(dict(name=path.name,bytes=path.stat().st_size,sha256=sha(path.read_bytes()),files=count))
    (OUT/'SHA256SUMS.txt').write_text(''.join(f"{a['sha256']}  {a['name']}\n" for a in archives),encoding='utf-8')
    audit=dict(passed=True,commit=git('rev-parse','HEAD'),archives=archives,models_games_settings_included=False)
    (OUT/'RELEASE_AUDIT.json').write_text(json.dumps(audit,indent=2)+'\n',encoding='utf-8');print(json.dumps(audit,indent=2))

def api(method,path,data=None,raw=None,auth=False):
    headers={'User-Agent':'DOSBox-Enhanced-release','Accept':'application/vnd.github+json','X-GitHub-Api-Version':'2022-11-28'}
    if auth:
        environment=os.environ|{'GIT_TERMINAL_PROMPT':'0','GCM_INTERACTIVE':'Never'}
        result=subprocess.run(['git','credential','fill'],input='protocol=https\nhost=github.com\n\n',capture_output=True,text=True,env=environment,check=True,timeout=40)
        fields=dict(line.split('=',1) for line in result.stdout.splitlines() if '=' in line);headers['Authorization']='Bearer '+fields['password']
    url=path if path.startswith('https://') else 'https://api.github.com'+path
    body=raw if raw is not None else json.dumps(data).encode() if data is not None else None
    if body is not None:headers['Content-Type']='application/octet-stream' if raw is not None else 'application/json'
    with urllib.request.urlopen(urllib.request.Request(url,body,headers,method=method),timeout=300) as response:return json.load(response)

def status():
    runs=api('GET',f'/repos/{REPO}/actions/runs?head_sha='+git('rev-parse','HEAD'))['workflow_runs']
    print(json.dumps([dict(id=r['id'],status=r['status'],conclusion=r['conclusion'],url=r['html_url']) for r in runs]))
    return runs

def publish():
    assert not git('status','--porcelain'),'Source worktree must be clean.'
    assert any(r['conclusion']=='success' and r['event']=='push' for r in status()),'Wait for CI success.'
    audit=json.loads((OUT/'RELEASE_AUDIT.json').read_text());assert audit['passed'] and audit['commit']==git('rev-parse','HEAD')
    prefix=f'/repos/{REPO}'
    release=api('POST',prefix+'/releases',dict(tag_name='v'+VERSION,target_commitish=audit['commit'],name=f'v{VERSION} · Phoenix 0.17 controls and graphics setup',body=(ROOT/'RELEASE_NOTES.md').read_text(encoding='utf-8-sig'),draft=True,prerelease=False),auth=True)
    for name in [a['name'] for a in audit['archives']]+['SHA256SUMS.txt']:
        data=(OUT/name).read_bytes();asset=api('POST',release['upload_url'].split('{')[0]+'?name='+urllib.parse.quote(name),raw=data,auth=True)
        assert asset['size']==len(data) and asset['digest']=='sha256:'+sha(data);print('Uploaded and hash-verified:',name,flush=True)
    release=api('PATCH',prefix+'/releases/'+str(release['id']),dict(draft=False,make_latest='true'),auth=True)
    assert not release['draft'];print('Published:',release['html_url'])
    (OUT/'PUBLICATION.json').write_text(json.dumps(dict(url=release['html_url'],commit=audit['commit'],release_id=release['id']),indent=2)+'\n')

def verify():
    release=api('GET',f'/repos/{REPO}/releases/tags/v{VERSION}');assert not release['draft']
    audit=json.loads((OUT/'RELEASE_AUDIT.json').read_text());expected={a['name']:a['sha256'] for a in audit['archives']}
    expected['SHA256SUMS.txt']=sha((OUT/'SHA256SUMS.txt').read_bytes())
    checks=[]
    for asset in release['assets']:
        assert asset['name'] in expected
        request=urllib.request.Request(asset['browser_download_url'],headers={'User-Agent':'DOSBox-Enhanced-public-verification','Cache-Control':'no-cache'})
        digest=hashlib.sha256();size=0
        with urllib.request.urlopen(request,timeout=300) as response:
            while data:=response.read(1024*1024):digest.update(data);size+=len(data)
        assert size==asset['size'] and digest.hexdigest()==expected[asset['name']]
        checks.append(dict(name=asset['name'],bytes=size,sha256=digest.hexdigest()));print('Public download verified:',asset['name'],flush=True)
    assert len(checks)==3
    (OUT/'PUBLIC_DOWNLOADS.json').write_text(json.dumps(dict(passed=True,release=release['html_url'],assets=checks),indent=2)+'\n')

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('command',choices=['package','status','publish','verify']);args=parser.parse_args()
    globals()[args.command]()
