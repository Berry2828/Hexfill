"""Verify localhost proxy and remote IPv4/IPv6 connections from real CAD."""
from pathlib import Path
import json
import shutil
import socket
import subprocess
import tempfile
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'verify'))
from cad_runtime_guard import require_cad_runtime
require_cad_runtime()
CAD=Path(r'D:\Program Files\Autodesk\AutoCAD 2018')
OUT=ROOT/'obj/fixes-20261014/offline'
OUT.mkdir(parents=True,exist_ok=True)
helper=OUT/'OfflineNetworkChecks.dll'
subprocess.run([str(ROOT/'packages/Microsoft.Net.Compilers/tools/csc.exe'),'/nologo','/target:library','/platform:x64',
                '/out:'+str(helper),*['/r:'+str(CAD/n) for n in ('acdbmgd.dll','acmgd.dll','accoremgd.dll')],
                str(ROOT/'tools/offline/OfflineNetworkChecks.cs')],cwd=ROOT,check=True)
with socket.socket(socket.AF_INET) as v4,socket.socket(socket.AF_INET6) as v6:
    v4.bind(('127.0.0.1',0)); v6.bind(('::1',0)); v4.listen(); v6.listen()
    # Positive controls: same machine and ports stay available to non-CAD apps.
    for host,server in [('127.0.0.1',v4),('::1',v6)]:
        with socket.create_connection((host,server.getsockname()[1]),timeout=2):
            connection,_=server.accept(); connection.close()
    with tempfile.TemporaryDirectory(prefix='hexfill-offline-') as directory:
        scratch=Path(directory); shutil.copy2(ROOT/'tests/load_quit.dwg',scratch/'test.dwg')
        result=OUT/'network-results.txt'; script=scratch/'check.scr'
        script.write_text('\n'.join(['_.NETLOAD','"'+helper.as_posix()+'"','HNETOFFTEST','"'+result.as_posix()+'"',
                                     str(v4.getsockname()[1]),str(v6.getsockname()[1]),'_.QUIT','_Yes'])+'\n',encoding='ascii')
        with (OUT/'network-console.log').open('wb') as log:
            run=subprocess.run([str(CAD/'accoreconsole.exe'),'/i',str(scratch/'test.dwg'),'/s',str(script),'/l','en-US',
                                '/isolate','HexFillHclnSmoke',str(scratch/'profile')],stdout=log,stderr=subprocess.STDOUT,cwd=scratch,
                               timeout=60,creationflags=subprocess.CREATE_NO_WINDOW | subprocess.BELOW_NORMAL_PRIORITY_CLASS)
        rows=result.read_text(encoding='utf-8').splitlines() if result.exists() else []
        assert run.returncode==0 and len(rows)==4 and all(r.startswith('PASS ') for r in rows),(run.returncode,rows)
        for server in (v4,v6):
            server.settimeout(.1)
            try:
                connection,_=server.accept();connection.close();raise AssertionError('CAD reached the proxy listener')
            except socket.timeout: pass
        summary={'checks':rows,'other_apps_localhost':'passed','CAD_proxy_connections_accepted':0,'exit':run.returncode}
        (OUT/'network-results.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
        print(json.dumps(summary,indent=2))
