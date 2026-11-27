param(
    [Parameter(Mandatory)][Alias('OutputDirectory')][string]$ArtifactsDirectory,
    [string]$OriginalNativePath,
    [string]$ExpectedRevision,
    [string]$VisualStudioPath,
    [switch]$RecorderOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'Stage2 native variants require Windows x64 and MSVC.' }
$root = Split-Path $PSScriptRoot -Parent
$revision = (git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or ($ExpectedRevision -and $ExpectedRevision -cne $revision)) {
    throw 'Stage2 native build candidate differs from the requested checkout.'
}
$sourceId = '2026-10-16 14:45:20 555e07ef33f69a0957d24d8e555b702464a131d6f1607f4c3c1e4551e8e6ca76'
$sourceSha3 = 'E2B869D3003B33C942B7DD7063604FBC53553E1B66609570DF39B9B7B558EE81'
$originalSha = '922AB7FF7E47EF2694CA5F62ADBEFCC44C637BF5D082488C286816ED5D06363F'
$artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory)
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
if (!$OriginalNativePath) {
    $package = Join-Path $artifacts 'sqlite.3.54.1.nupkg'
    Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/sqlite/3.54.1/sqlite.3.54.1.nupkg' `
        -OutFile $package -TimeoutSec 60
    $OriginalNativePath = Join-Path $artifacts 'original\e_sqlite3.dll'
    New-Item -ItemType Directory -Path (Split-Path $OriginalNativePath -Parent) -Force | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entry = $archive.GetEntry('runtimes/win-x64/native/e_sqlite3.dll')
        if (!$entry) { throw 'Pinned SQLite NuGet package lacks the expected win-x64 asset.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $OriginalNativePath, $true)
    } finally { $archive.Dispose() }
}
$OriginalNativePath = (Resolve-Path -LiteralPath $OriginalNativePath).Path
if ((Get-FileHash $OriginalNativePath -Algorithm SHA256).Hash -cne $originalSha -or
    (Get-Item $OriginalNativePath).Length -ne 2009088) {
    throw 'Original win-x64 e_sqlite3.dll does not match the approved package asset.'
}
if (!$VisualStudioPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $VisualStudioPath = & $vswhere -latest -products '*' `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or !$VisualStudioPath) { throw 'MSVC x64 installation is unavailable.' }
}
$vcvars = Join-Path $VisualStudioPath 'VC\Auxiliary\Build\vcvarsall.bat'
if (!(Test-Path -LiteralPath $vcvars)) { throw 'MSVC environment setup is unavailable.' }
$script:vcvars = $vcvars
$script:artifacts = $artifacts
$script:compilerEnvironment = Join-Path $artifacts 'compiler-environment.txt'

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Build-Native([string]$Name, [string[]]$Sources, [string[]]$Flags,
                      [string]$Output, [switch]$Dll) {
    $directory = Join-Path $script:artifacts $Name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $log = Join-Path $directory 'compile.log'
    $cmdFile = Join-Path $directory 'build.cmd'
    $quotedFlags = @($Flags | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $quotedSources = @($Sources | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $dllFlag = if ($Dll) { '/LD' } else { '' }
    $link = if ($Dll) {
        '/link /DEBUG:FULL /PDB:"' + (Join-Path $directory 'e_sqlite3.pdb') +
        '" /IMPLIB:"' + (Join-Path $directory 'e_sqlite3.lib') + '"'
    } else { '' }
    Write-Utf8 $cmdFile @"
@echo off
call "$script:vcvars" x64 > "$directory\environment-setup.txt" 2>&1
if errorlevel 1 exit /b 1
set VCToolsVersion > "$script:compilerEnvironment"
set WindowsSDKVersion >> "$script:compilerEnvironment"
set VCToolsInstallDir >> "$script:compilerEnvironment"
set WindowsSdkDir >> "$script:compilerEnvironment"
cl /nologo $dllFlag $quotedFlags /Fo"$directory\\" /Fe"$Output" $quotedSources $link > "$log" 2>&1
if errorlevel 1 exit /b 1
exit /b 0
"@
    & $env:ComSpec /d /c "`"$cmdFile`""
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $Output)) {
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 40 }
        throw "Native $Name build failed; retained log: $log"
    }
}

Push-Location $artifacts
try {
    # Fixed, bounded public vendor-recipe attempts. Failure does not assert vendor parity.
    $vendor = [Collections.Generic.List[object]]::new()
    $vendorFile = Join-Path $artifacts 'vendor-recipe-attempts.json'
    if (Test-Path -LiteralPath $vendorFile) {
        foreach ($attempt in @(Get-Content -LiteralPath $vendorFile -Raw | ConvertFrom-Json)) {
            $vendor.Add($attempt)
        }
    } else {
        foreach ($url in @(
            'https://raw.githubusercontent.com/SourceGear/sqlite-builds/main/README.md',
            'https://raw.githubusercontent.com/ericsink/sqlite-builds/master/README.md',
            'https://raw.githubusercontent.com/SourceGear/cb/main/README.md'
        )) {
            $vendor.Add([ordered]@{
                url=$url;retrieved=$false;status='PriorBoundedRetrievalUnavailable';retainedFile=$null
            })
        }
    }
    Write-Utf8 $vendorFile ($vendor.ToArray() | ConvertTo-Json -Depth 4)
    $zip = Join-Path $artifacts 'sqlite-amalgamation-3540100.zip'
    if (!(Test-Path -LiteralPath $zip)) {
        Invoke-WebRequest 'https://sqlite.org/2026/sqlite-amalgamation-3540100.zip' `
            -OutFile $zip -TimeoutSec 60
    }
    $release = Join-Path $artifacts 'official-release.html'
    Invoke-WebRequest 'https://sqlite.org/releaselog/3_54_1.html' -OutFile $release -TimeoutSec 30
    $releaseText = [IO.File]::ReadAllText($release)
    if (!$releaseText.Contains($sourceId) -or
        !$releaseText.Contains($sourceSha3.ToLowerInvariant())) {
        throw 'Official release/source identity verification failed.'
    }
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $artifacts 'source') -Force
    $amalgamation = Join-Path $artifacts 'source\sqlite-amalgamation-3540100\sqlite3.c'
    $sha3 = [Convert]::ToHexString([Security.Cryptography.SHA3_256]::HashData([IO.File]::ReadAllBytes($amalgamation)))
    if ($sha3 -cne $sourceSha3 -or ![IO.File]::ReadAllText($amalgamation).Contains($sourceId)) {
        throw 'Official SQLite 3.54.1 amalgamation hash/source ID mismatch.'
    }
    $nativeSources = Join-Path $artifacts 'native-sources'
    New-Item -ItemType Directory -Path $nativeSources -Force | Out-Null
    foreach ($name in @('SqliteStage2Trace.c','SqliteStage2Trace.h','Patch-SqliteStage2Native.ps1','Build-SqliteStage2Native.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $nativeSources $name) -Force
    }
    $diagnosticSource = Join-Path $nativeSources 'sqlite3-diagnostic.c'
    $controlSource = Join-Path $nativeSources 'sqlite3-control.c'
    Copy-Item -LiteralPath $amalgamation -Destination $controlSource -Force
    & (Join-Path $PSScriptRoot 'Patch-SqliteStage2Native.ps1') -SourceFile $amalgamation `
        -DestinationFile $diagnosticSource -SchemaFile (Join-Path $artifacts 'event-schema.json') `
        -PatchManifestFile (Join-Path $artifacts 'patch-manifest.json')

    # This harness never calls sqlite3_open/prepare/step or launches an application fixture.
    $harnessSource = Join-Path $artifacts 'native-recorder-harness.c'
    Write-Utf8 $harnessSource @'
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <errno.h>
#include "SqliteStage2Trace.h"
typedef int (__cdecl *bind_fn)(int,unsigned,unsigned,const wchar_t*,const wchar_t*,const wchar_t*,const wchar_t*);
typedef void (__cdecl *mark_fn)(unsigned,uint64_t,uint64_t);
typedef const char *(__cdecl *string_fn)(void);
typedef const char *(__cdecl *option_fn)(int);
static mark_fn mark;
static DWORD WINAPI produce(void *value){
  unsigned i;
  for(i=0;i<600;i++) mark(1,(uint64_t)(uintptr_t)value,i);
  return 0;
}
int wmain(int argc,wchar_t **argv){
  HMODULE module;
  if(argc<3) return 2;
  module=LoadLibraryW(argv[2]);
  if(!module) return 3;
  if(wcscmp(argv[1],L"inspect")==0){
    string_fn version=(string_fn)GetProcAddress(module,"sqlite3_libversion");
    string_fn source=(string_fn)GetProcAddress(module,"sqlite3_sourceid");
    option_fn option=(option_fn)GetProcAddress(module,"sqlite3_compileoption_get");
    int i=0; const char *s;
    if(!version || !source || !option) return 4;
    printf("VERSION=%s\nSOURCE_ID=%s\n",version(),source());
    while((s=option(i++))!=0) printf("OPTION=%s\n",s);
    return 0;
  }else{
    HANDLE map; uint8_t *view; wchar_t name[128];
    HANDLE threads[4]; unsigned i, role=1, pid=GetCurrentProcessId(), parent=pid;
    int lifecycle=wcscmp(argv[1],L"memory-lifecycle")==0;
    int core=wcscmp(argv[1],L"core")==0 || lifecycle;
    bind_fn bind=core ? oe_stage2_bind : (bind_fn)GetProcAddress(module,"oe_stage2_bind");
    LONG64 reserved, committed, overflow, dropped;
    unsigned long dos;
    mark=core ? oe_stage2_mark : (mark_fn)GetProcAddress(module,"oe_stage2_mark");
    if(!bind || !mark) return 5;
    if(wcscmp(argv[1],L"child")==0){ role=2; parent=pid+1; }
    swprintf_s(name,128,L"Local\\OrgExplorer-SQLite-Stage2-%032x-%s",pid,role==1 ? L"parent" : L"child");
    map=CreateFileMappingW(INVALID_HANDLE_VALUE,0,PAGE_READWRITE,0,128+2048*256,name);
    if(!map || GetLastError()==ERROR_ALREADY_EXISTS) return 6;
    view=(uint8_t*)MapViewOfFile(map,FILE_MAP_ALL_ACCESS,0,0,128+2048*256);
    if(!view) return 7;
    *(uint32_t*)(view+0)=0x3245544F; *(uint32_t*)(view+4)=1;
    *(uint32_t*)(view+8)=256; *(uint32_t*)(view+12)=2048;
    *(uint32_t*)(view+16)=parent; *(uint32_t*)(view+24)=role; *(uint32_t*)(view+28)=1;
    if(bind(role,parent,pid+1,L"C:\\owned\\workspace.sqlite",L"C:\\owned\\workspace.sqlite-wal",L"C:\\owned\\workspace.sqlite-shm",name)==0) return 8;
    *(uint32_t*)(view+4)=2;
    if(bind(role,parent,pid,L"C:\\owned\\workspace.sqlite",L"C:\\owned\\workspace.sqlite-wal",L"C:\\owned\\workspace.sqlite-shm",name)==0) return 9;
    *(uint32_t*)(view+4)=1;
    if(core){
      wchar_t db[2048],wal[2048],shm[2048]; char utf8[4096]; int key=0;
      HANDLE owned; uint64_t oldToken,newToken,handleToken;
      if(argc<4) return 18;
      swprintf_s(db,2048,L"%s\\recorder-owned-%u.bin",argv[3],pid);
      swprintf_s(wal,2048,L"%s-wal",db); swprintf_s(shm,2048,L"%s-shm",db);
      if(bind(role,parent,pid,db,wal,shm,name)!=0) return 19;
      if(!WideCharToMultiByte(CP_UTF8,0,db,-1,utf8,4096,0,0)) return 20;
      owned=CreateFileW(db,GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,
                       0,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,0);
      if(owned==INVALID_HANDLE_VALUE) return 21;
      SetLastError(0x5A17); errno=ERANGE; _set_doserrno(4321);
      oe_stage2_file(utf8,&key,owned,0,0,0);
      _get_doserrno(&dos);
      if(GetLastError()!=0x5A17 || errno!=ERANGE || dos!=4321) return 22;
      if(lifecycle){
        int connection=0, mainPager=0, memoryPager[2]={0,0}, unknown=0;
        LONG64 count;
        oe_stage2_btree_binding(&connection,&mainPager,&key,1,0);
        for(i=0;i<2;i++){
          uint64_t pagerToken,walToken;
          oe_stage2_btree_binding(&connection,&memoryPager[i],0,0,1);
          pagerToken=oe_stage2_token(&memoryPager[i],OE_PAGER);
          oe_stage2_event(OE_PAGER_CLOSE_BEGIN,&memoryPager[i],OE_PAGER,0,0,0,0,0,0);
          walToken=oe_stage2_wal_close_begin(0,0,0,4096);
          oe_stage2_wal_close_end(0,walToken,0,0,4096,0,0);
          oe_stage2_event_token(OE_PAGER_CLOSE_END,pagerToken,0,0,0,0,0,0);
          oe_stage2_retire(pagerToken);
        }
        count=*(LONG64*)(view+64);
        printf("MEMORY_LIFECYCLE unbound=%lld expected=0 memory_pagers=2\n",count);
        if(count!=0){
          CloseHandle(owned); DeleteFileW(db);
          return 34;
        }
        count=*(LONG64*)(view+32);
        if(count!=8 || *(LONG64*)(view+40)!=8) return 35;
        for(i=0;i<8;i++){
          uint8_t *r=view+128+i*256;
          unsigned code=*(uint32_t*)(r+24);
          if(*(uint64_t*)r!=i+1 || code==OE_WAL_CLOSE_BEGIN || code==OE_WAL_CLOSE_END) return 36;
          if(code==OE_PAGER_CLOSE_BEGIN || code==OE_PAGER_CLOSE_END){
            if(*(uint64_t*)(r+40)!=oe_stage2_token(&key,OE_FILE)) return 37;
          }
        }
        /* Truly unknown NON-NULL objects still refuse pre-reservation. */
        oe_stage2_btree_binding(&unknown,&unknown,0,0,1);
        oe_stage2_event(OE_PAGER_CLOSE_BEGIN,&unknown,OE_PAGER,11,22,33,0,0,0);
        oldToken=oe_stage2_wal_close_begin(&unknown,11,22,33);
        oe_stage2_wal_close_end(&unknown,oldToken,11,22,33,0,0);
        if(*(LONG64*)(view+64)!=4 || *(LONG64*)(view+32)!=count) return 38;
        CloseHandle(owned); DeleteFileW(db);
        printf("RECORDER_MEMORY_LIFECYCLE_OK scoped_memory_unbound=0 null_wal_events=0 negative_unknown=4\n");
        UnmapViewOfFile(view); CloseHandle(map); FreeLibrary(module);
        return 0;
      }
      oldToken=oe_stage2_token(&key,OE_FILE);
      handleToken=oe_stage2_token(owned,OE_HANDLE);
      oe_stage2_retire(oldToken); oe_stage2_retire(handleToken);
      CloseHandle(owned);
      owned=CreateFileW(db,GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,
                       0,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,0);
      if(owned==INVALID_HANDLE_VALUE) return 23;
      oe_stage2_file(utf8,&key,owned,0,0,0);
      newToken=oe_stage2_token(&key,OE_FILE);
      if(!oldToken || !newToken || oldToken==newToken) return 24;
      oe_stage2_event_token(OE_CLOSE_RESULT,oldToken,77,0,0,0,0,0);
      oe_stage2_event(OE_READ_BEGIN,(const void*)1,OE_FILE,111,222,333,0,0,0);
      if(*(LONG64*)(view+32)!=3 || *(LONG64*)(view+64)!=1) return 25;
      mark(6,0,0);
      oe_stage2_event(OE_READ_BEGIN,&key,OE_FILE,111,222,333,0,0,0);
      if(*(LONG64*)(view+32)!=4 || *(LONG64*)(view+64)!=1) return 26;
      mark(7,0,0);
      if(*(LONG64*)(view+32)!=5 || *(LONG64*)(view+40)!=5) return 27;
      if(*(uint64_t*)(view+128+2*256+32)!=oldToken ||
         *(uint32_t*)(view+128+3*256+28)!=6 || *(uint32_t*)(view+128+4*256+28)!=7) return 28;
      for(i=0;i<5;i++){
        unsigned j; uint8_t *r=view+128+i*256;
        if(*(uint64_t*)r!=i+1) return 29;
        for(j=112;j<256;j++) if(r[j]!=0) return 30;
      }
      if((*(uint64_t*)(view+128+104)&256)==0 || (*(uint64_t*)(view+128+256+104)&256)==0) return 31;
      if(*(uint64_t*)(view+128+80)!=*(uint64_t*)(view+128+256+80) ||
         *(uint64_t*)(view+128+88)!=*(uint64_t*)(view+128+256+88) ||
         *(uint64_t*)(view+128+96)!=*(uint64_t*)(view+128+256+96)) return 32;
      CloseHandle(owned); DeleteFileW(db);
      printf("RECORDER_CORE_OK lifetime_tokens=distinct unknown_binding=private cutoff=verified\n");
      UnmapViewOfFile(view); CloseHandle(map); FreeLibrary(module);
      return 0;
    }
    if(wcscmp(argv[1],L"registered")==0) *(uint32_t*)(view+20)=pid;
    SetLastError(0x5A17); errno=ERANGE; _set_doserrno(4321);
    if(bind(role,parent,pid,L"C:\\owned\\workspace.sqlite",L"C:\\owned\\workspace.sqlite-wal",L"C:\\owned\\workspace.sqlite-shm",name)!=0) return 10;
    _get_doserrno(&dos);
    if(GetLastError()!=0x5A17 || errno!=ERANGE || dos!=4321) return 11;
    for(i=0;i<4;i++){ threads[i]=CreateThread(0,0,produce,(void*)(uintptr_t)(i+1),0,0); if(!threads[i]) return 12; }
    if(WaitForMultipleObjects(4,threads,TRUE,10000)!=WAIT_OBJECT_0) return 13;
    for(i=0;i<4;i++) CloseHandle(threads[i]);
    reserved=*(LONG64*)(view+32); committed=*(LONG64*)(view+40);
    overflow=*(LONG64*)(view+48); dropped=*(LONG64*)(view+72);
    if(reserved!=2048 || committed!=2048 || overflow!=352 || dropped!=352) return 14;
    for(i=0;i<2048;i++){
      uint8_t *r=view+128+i*256;
      if(*(uint64_t*)r!=i+1 || *(uint32_t*)(r+16)!=pid || *(uint32_t*)(r+24)!=1 ||
         *(uint64_t*)(r+32)!=0 || *(uint64_t*)(r+48)<1 || *(uint64_t*)(r+48)>4) return 15;
    }
    *(uint32_t*)(view+80)=1; mark(7,0,0);
    if(*(LONG64*)(view+32)!=2048) return 16;
    *(uint32_t*)(view+80)=0; *(uint32_t*)(view+28)=0; mark(7,0,0);
    if(*(LONG64*)(view+32)!=2048) return 17;
    printf("RECORDER_OK role=%u records=2048 overflow=352 publication=verified\n",role);
    UnmapViewOfFile(view); CloseHandle(map); FreeLibrary(module);
    return 0;
  }
}
'@
    $harness = Join-Path $artifacts 'native-recorder-harness.exe'
    $trace = Join-Path $nativeSources 'SqliteStage2Trace.c'
    Build-Native harness @($harnessSource,$trace) @('/std:c11','/O2','/MD',
        '/D_CRT_SECURE_NO_WARNINGS',"/I$nativeSources") $harness
    $lifecycleOutput = @(& $harness memory-lifecycle $OriginalNativePath $artifacts)
    $lifecycleExit = $LASTEXITCODE
    $lifecycleResult = [ordered]@{
        diagnosticOnly=$true;candidate=$revision;sqliteReaderStarted=$false
        test='Known-DB memory Btree -> PagerClose -> NULL WalClose -> PagerClose exit'
        actualStdout=$lifecycleOutput;exitCode=$lifecycleExit
        traceSourceSha256=(Get-FileHash $trace -Algorithm SHA256).Hash
    }
    Write-Utf8 (Join-Path $artifacts 'memory-lifecycle-check.json') ($lifecycleResult | ConvertTo-Json -Depth 4)
    if ($lifecycleExit -ne 0) {
        throw "Non-SQLite memory-pager lifecycle binding RED (exit $lifecycleExit): $($lifecycleOutput -join ' ')"
    }
    if ($RecorderOnly) { return $lifecycleResult }

    function Inspect([string]$Path, [string]$Name) {
        $output = @(& $harness inspect $Path)
        if ($LASTEXITCODE -ne 0) { throw "Native static identity inspection failed: $Name" }
        Write-Utf8 (Join-Path $artifacts "$Name-identity.txt") ($output -join "`n")
        $id = @($output | Where-Object { $_.StartsWith('SOURCE_ID=') })
        $version = @($output | Where-Object { $_.StartsWith('VERSION=') })
        if ($id.Count -ne 1 -or $id[0].Substring(10) -cne $sourceId -or
            $version.Count -ne 1 -or $version[0] -cne 'VERSION=3.54.1') {
            throw "Native $Name version/source identity mismatch."
        }
        [ordered]@{
            path = $Path
            nativeDllPath = $Path
            sha256 = (Get-FileHash $Path -Algorithm SHA256).Hash
            sizeBytes = (Get-Item $Path).Length
            sourceId = $sourceId
            version = '3.54.1'
            compileOptions = @($output | Where-Object { $_.StartsWith('OPTION=') } | ForEach-Object { $_.Substring(7) })
        }
    }
    $original = Inspect $OriginalNativePath original
    $defines = @($original.compileOptions | Where-Object {
        $_ -match '^ENABLE_\w+(?:=.*)?$' -or $_ -match '^OMIT_\w+(?:=.*)?$' -or
        $_ -match '^(SOUNDEX|LIKE_DOESNT_MATCH_BLOBS|DEFAULT_FOREIGN_KEYS)$' -or
        $_ -match '^(DQS|THREADSAFE|TEMP_STORE|USE_URI|MAX_VARIABLE_NUMBER)='
    } | ForEach-Object { '/DSQLITE_' + $_ })
    $flags = @('/std:c11','/Gd','/O2','/MT','/Zi','/DNDEBUG','/DWIN32_LEAN_AND_MEAN',
        '/D_WIN32_WINNT=0x0602','/D_CRT_SECURE_NO_WARNINGS','/DSQLITE_API=__declspec(dllexport)',
        "/I$nativeSources") + $defines
    $controlDll = Join-Path $artifacts 'control\e_sqlite3.dll'
    $diagnosticDll = Join-Path $artifacts 'diagnostic\e_sqlite3.dll'
    Build-Native control @($controlSource,$trace) $flags $controlDll -Dll
    Build-Native diagnostic @($diagnosticSource,$trace) $flags $diagnosticDll -Dll
    $control = Inspect $controlDll control
    $diagnostic = Inspect $diagnosticDll diagnostic
    if (($control.compileOptions -join "`n") -cne ($diagnostic.compileOptions -join "`n")) {
        throw 'Matched control and diagnostic compile options differ.'
    }
    $checks = [Collections.Generic.List[object]]::new()
    $checks.Add([ordered]@{variant='same-source-recorder';mode='memory-lifecycle';result=$lifecycleOutput -join "`n"})
    $coreOutput = @(& $harness core $diagnosticDll $artifacts)
    if ($LASTEXITCODE -ne 0) { throw "Non-SQLite recorder core/lifetime/cutoff check failed (exit $LASTEXITCODE)." }
    $checks.Add([ordered]@{variant='same-source-recorder';mode='core';result=$coreOutput -join "`n"})
    foreach ($variant in @(@('control',$controlDll),@('diagnostic',$diagnosticDll))) {
        foreach ($mode in @('recorder','registered','child')) {
            $output = @(& $harness $mode $variant[1])
            if ($LASTEXITCODE -ne 0) { throw "Non-SQLite recorder check failed: $($variant[0])/$mode (exit $LASTEXITCODE)." }
            $checks.Add([ordered]@{variant=$variant[0];mode=$mode;result=$output -join "`n"})
        }
    }
    $compilerEnvironment = Get-Content -LiteralPath $script:compilerEnvironment
    $abiCommand = Join-Path $artifacts 'abi-check.cmd'
    Write-Utf8 $abiCommand @"
@echo off
call "$vcvars" x64 > "$artifacts\abi-environment-setup.txt" 2>&1
if errorlevel 1 exit /b 1
dumpbin /nologo /exports "$OriginalNativePath" > "$artifacts\original-exports.txt"
if errorlevel 1 exit /b 1
dumpbin /nologo /exports "$controlDll" > "$artifacts\control-exports.txt"
if errorlevel 1 exit /b 1
dumpbin /nologo /exports "$diagnosticDll" > "$artifacts\diagnostic-exports.txt"
if errorlevel 1 exit /b 1
dumpbin /nologo /dependents "$OriginalNativePath" > "$artifacts\original-dependents.txt"
if errorlevel 1 exit /b 1
dumpbin /nologo /dependents "$diagnosticDll" > "$artifacts\diagnostic-dependents.txt"
if errorlevel 1 exit /b 1
exit /b 0
"@
    & $env:ComSpec /d /c "`"$abiCommand`""
    if ($LASTEXITCODE -ne 0) { throw 'Native export/dependency inspection failed.' }
    function Exports([string]$Name) {
        @(Get-Content (Join-Path $artifacts "$Name-exports.txt") | ForEach-Object {
            if ($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\w+)(?:\s|$)') { $Matches[1] }
        })
    }
    $originalExports = Exports original
    $controlExports = Exports control
    $diagnosticExports = Exports diagnostic
    $missingExports = @($originalExports | Where-Object {
        $_ -notin $controlExports -or $_ -notin $diagnosticExports
    })
    $vendorEncryptionExports = @('sqlite3_key','sqlite3_key_v2','sqlite3_rekey','sqlite3_rekey_v2')
    if (!$originalExports.Count -or
        @($missingExports | Where-Object { $_ -notin $vendorEncryptionExports }).Count) {
        throw 'A native variant is missing a standard original e_sqlite3 export.'
    }
    $differences = @(Compare-Object $original.compileOptions $control.compileOptions | ForEach-Object {
        [ordered]@{option=$_.InputObject;side=$_.SideIndicator}
    })
    $manifest = [ordered]@{
        schemaVersion=1;stage='SqliteNativeTraceStage2';diagnosticOnly=$true;normalCiAttested=$false
        candidate=$revision;runId=$env:GITHUB_RUN_ID;runAttempt=$env:GITHUB_RUN_ATTEMPT
        recipe='Authorized SAME-SOURCE best-effort matched control + diagnostic; vendor parity not established'
        parityKnown=$false;shippingNativeChanged=$false;sourceRelease='3.54.1';sourceId=$sourceId
        sourceUrl='https://sqlite.org/2026/sqlite-amalgamation-3540100.zip'
        sourceZipSha256=(Get-FileHash $zip -Algorithm SHA256).Hash
        sourceAmalgamationSha3_256=$sha3;sourceAmalgamationSha256=(Get-FileHash $amalgamation -Algorithm SHA256).Hash
        compilerEnvironment=$compilerEnvironment;compiler='MSVC x64 via vswhere/vcvarsall in SAME cmd.exe as cl'
        compilerVersion=@($control.compileOptions | Where-Object { $_.StartsWith('COMPILER=') })
        compilerRuntime='/MT';callingConvention='cdecl';flags=$flags;vendorRecipeAttempts=$vendor.ToArray()
        original=$original;control=$control;diagnostic=$diagnostic
        controlNativeDll=$controlDll;diagnosticNativeDll=$diagnosticDll
        eventSchemaSha256=(Get-FileHash (Join-Path $artifacts 'event-schema.json') -Algorithm SHA256).Hash
        patchManifestSha256=(Get-FileHash (Join-Path $artifacts 'patch-manifest.json') -Algorithm SHA256).Hash
        buildScriptSha256=(Get-FileHash (Join-Path $PSScriptRoot 'Build-SqliteStage2Native.ps1') -Algorithm SHA256).Hash
        compileOptionDifferencesFromOriginal=$differences;recorderChecks=$checks.ToArray()
        originalExportCount=$originalExports.Count;controlExportCount=$controlExports.Count
        diagnosticExportCount=$diagnosticExports.Count
        standardOriginalExportSurfacePreserved=$true;originalExportSurfacePreserved=($missingExports.Count -eq 0)
        missingOriginalExports=$missingExports
        observationCoverage='Incomplete: bounded decision/IO/lifetime metadata, not content-through-read proof'
        missingObservations=@(
            'Native SHM mapping/unmapping and CloseHandle/DeleteFile original Boolean outcomes are not individually emitted.'
            'Checkpoint nBackfill/read-mark gate snapshots are not separately captured; no extra SHM reads are added.'
            'Read-time payload digests and exclusion of outside actors are not established.'
        )
        originalDependencies=(Get-Content (Join-Path $artifacts 'original-dependents.txt') -Raw)
        diagnosticDependencies=(Get-Content (Join-Path $artifacts 'diagnostic-dependents.txt') -Raw)
        differences=@(
            'Vendor source patches/recipe, exact compiler/SDK/CRT and flags are not verified.'
            'Control contains no native SQLite hooks; both variants link identical bind/phase recorder exports.'
            'Diagnostic patches only the verified official 3.54.1 amalgamation; shipping package/version are unchanged.'
            'Original asset inspection and harness tests call static identity/recorder exports only, never a SQLite reader.'
            'Official amalgamation lacks vendor-only sqlite3_key/key_v2/rekey/rekey_v2 exports; no encryption stubs are invented.'
            'Added hook calls, atomics and once-per-already-owned-handle FileIdInfo queries may perturb scheduling.'
        )
        originalFixtureExecuted=$false
    }
    $manifestFile = Join-Path $artifacts 'native-build.json'
    Write-Utf8 $manifestFile ($manifest | ConvertTo-Json -Depth 10)
    [ordered]@{
        manifest=$manifestFile;manifestSha256=(Get-FileHash $manifestFile -Algorithm SHA256).Hash
        eventSchema=(Join-Path $artifacts 'event-schema.json');controlDll=$controlDll;controlSha256=$control.sha256
        diagnosticDll=$diagnosticDll;diagnosticSha256=$diagnostic.sha256;parityKnown=$false
    }
}
finally { Pop-Location }
