param(
    [Parameter(Mandatory)][string]$SourceFile,
    [Parameter(Mandatory)][string]$DestinationFile,
    [Parameter(Mandatory)][string]$SchemaFile,
    [Parameter(Mandatory)][string]$PatchManifestFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceBytes = [IO.File]::ReadAllBytes($SourceFile)
$sourceSha3 = [Convert]::ToHexString([Security.Cryptography.SHA3_256]::HashData($sourceBytes))
if ($sourceSha3 -cne 'E2B869D3003B33C942B7DD7063604FBC53553E1B66609570DF39B9B7B558EE81') {
    throw 'Native instrumentation requires the official, unmodified SQLite 3.54.1 amalgamation.'
}
$script:text = [Text.Encoding]::UTF8.GetString($sourceBytes).Replace("`r`n", "`n")
$script:changes = [Collections.Generic.List[object]]::new()

function Get-Function([string]$Name) {
    $escaped = [regex]::Escape($Name)
    $pattern = "(?ms)^(?:static|SQLITE_PRIVATE)(?: [\w*]+)*\s+$escaped\([^;{}]*?\)\s*\{"
    $match = [regex]::Match($script:text, $pattern)
    if (!$match.Success) { throw "Missing native definition: $Name" }
    $end = [regex]::new('(?m)^}').Match($script:text, $match.Index + $match.Length)
    if (!$end.Success) { throw "Missing native definition end: $Name" }
    [pscustomobject]@{
        Start = $match.Index
        Length = $end.Index + 1 - $match.Index
        Signature = $match.Value.Substring(0, $match.Value.LastIndexOf('{')).TrimEnd()
        Text = $script:text.Substring($match.Index, $end.Index + 1 - $match.Index)
    }
}

function Set-Function([string]$Name, [object]$Function, [string]$Replacement) {
    $script:text = $script:text.Substring(0, $Function.Start) + $Replacement +
        $script:text.Substring($Function.Start + $Function.Length)
    $script:changes.Add([ordered]@{
        function = $Name
        originalSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($Function.Text)))
        patchedSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($Replacement)))
    })
}

function Wrap([string]$Name, [string]$Arguments, [string]$Before, [string]$After,
              [string]$ReturnType = 'int') {
    $f = Get-Function $Name
    $impl = "oe_native_$Name"
    $body = [regex]::new("\b$Name(?=\()").Replace($f.Text, $impl, 1)
    $call = if ($ReturnType -eq 'void') { "$impl($Arguments);" }
        else { "$ReturnType oe_rc = $impl($Arguments);" }
    $return = if ($ReturnType -eq 'void') { '' } else { 'return oe_rc;' }
    $replacement = @"
$($f.Signature);
$body

$($f.Signature){
$Before
  $call
  DWORD oe_error = GetLastError();
$After
  SetLastError(oe_error);
  $return
}
"@
    Set-Function $Name $f $replacement
}

function Edit([string]$Name, [string]$Old, [string]$New, [int]$Count = 1) {
    $f = Get-Function $Name
    $actual = [regex]::Matches($f.Text, [regex]::Escape($Old)).Count
    if ($actual -ne $Count) { throw "Native anchor count for $Name expected $Count, got $actual." }
    Set-Function $Name $f ($f.Text.Replace($Old, $New))
}

function Event([string]$Code, [string]$Key, [string]$Kind,
               [string]$A = '0', [string]$B = '0', [string]$C = '0',
               [string]$Rc = '0', [string]$Error = '0', [string]$Flags = '0') {
    "  oe_stage2_event($Code, $Key, $Kind, (uint64_t)($A), (uint64_t)($B), (uint64_t)($C), $Rc, $Error, (uint64_t)($Flags));"
}

Wrap winOpen 'pVfs,zName,id,flags,pOutFlags' '' @'
  oe_stage2_file(zName,id,oe_rc==SQLITE_OK ? ((winFile*)id)->h : INVALID_HANDLE_VALUE,
                 flags,oe_rc,oe_error);
'@
Wrap winRead 'id,pBuf,amt,offset' (Event OE_READ_BEGIN id OE_FILE offset amt) `
    (Event OE_READ_END id OE_FILE offset amt '((winFile*)id)->lastErrno' oe_rc oe_error)
Wrap winWrite 'id,pBuf,amt,offset' (Event OE_WRITE_BEGIN id OE_FILE offset amt) `
    (Event OE_WRITE_END id OE_FILE offset amt '((winFile*)id)->lastErrno' oe_rc oe_error)
Wrap winTruncate 'id,nByte' (Event OE_TRUNCATE_BEGIN id OE_FILE nByte) `
    (Event OE_TRUNCATE_END id OE_FILE nByte '((winFile*)id)->szChunk' '((winFile*)id)->lastErrno' oe_rc oe_error)
Wrap winClose 'id' @'
  uint64_t oe_handle = oe_stage2_token(((winFile*)id)->h,OE_HANDLE);
  oe_stage2_event(OE_FILE_CLOSE_BEGIN,id,OE_FILE,0,0,0,0,0,0);
'@ @'
  oe_stage2_event(OE_FILE_CLOSE_END,id,OE_FILE,0,0,0,oe_rc,oe_error,0);
  if(oe_rc==SQLITE_OK){
    oe_stage2_retire(oe_handle);
    oe_stage2_forget(id,OE_FILE);
  }
'@
Wrap winOpenSharedMemory 'pDbFd' (Event OE_SHM_OPEN_BEGIN pDbFd OE_FILE) `
    (Event OE_SHM_OPEN_END pDbFd OE_FILE 0 0 0 oe_rc oe_error)
Wrap winCloseSharedMemory 'pDbFd,deleteFlag' @'
  uint64_t oe_shm = oe_stage2_token(pDbFd->pShm,OE_SHM);
  uint64_t oe_handle = oe_stage2_token(pDbFd->pShm ? pDbFd->pShm->hShm : INVALID_HANDLE_VALUE,OE_HANDLE);
  oe_stage2_event(OE_SHM_CLOSE_BEGIN,pDbFd,OE_FILE,deleteFlag,0,0,0,0,0);
'@ @'
  oe_stage2_event(OE_SHM_CLOSE_END,pDbFd,OE_FILE,deleteFlag,0,0,oe_rc,oe_error,0);
  oe_stage2_retire(oe_shm);
  oe_stage2_retire(oe_handle);
'@
Wrap winLockSharedMemory 'pShmNode,nMs' `
    (Event OE_DMS_BEGIN pShmNode OE_NODE 'pShmNode->isUnlocked' 'pShmNode->isReadonly' nMs) `
    (Event OE_DMS_END pShmNode OE_NODE 'pShmNode->isUnlocked' 'pShmNode->isReadonly' nMs oe_rc oe_error)
Wrap winZeroSharedMemory 'h' (Event OE_ZERO_BEGIN h OE_HANDLE 8) `
    (Event OE_ZERO_END h OE_HANDLE 8 0 0 oe_rc oe_error)
Wrap winHandleTruncate 'h,nByte' (Event OE_HANDLE_TRUNCATE_BEGIN h OE_HANDLE nByte) `
    (Event OE_HANDLE_TRUNCATE_END h OE_HANDLE nByte 0 0 oe_rc oe_error)

# Results are captured immediately after the ORIGINAL dispatch, not by acquiring probe locks.
Edit winHandleLockTimeout '  ret = osLockFileEx(hFile, flags, 0, nByte, 0, &ovlp);' @'
  ret = osLockFileEx(hFile, flags, 0, nByte, 0, &ovlp);
  oe_stage2_event(OE_LOCK_RESULT,hFile,OE_HANDLE,offset,nByte,ret,
                 OE_STAGE2_NO_SQLITE_RC,GetLastError(),(uint64_t)flags<<32);
'@
Edit winLockFile '  return osLockFileEx(*phFile, flags, 0, numBytesLow, numBytesHigh, &ovlp);' @'
  {
    BOOL oe_result = osLockFileEx(*phFile, flags, 0, numBytesLow, numBytesHigh, &ovlp);
    DWORD oe_error = GetLastError();
    oe_stage2_event(OE_LOCK_RESULT,*phFile,OE_HANDLE,
        ((uint64_t)offsetHigh<<32)|offsetLow,
        ((uint64_t)numBytesHigh<<32)|numBytesLow,oe_result,
        OE_STAGE2_NO_SQLITE_RC,oe_error,(uint64_t)flags<<32);
    SetLastError(oe_error);
    return oe_result;
  }
'@
Edit winUnlockFile '  return osUnlockFileEx(*phFile, 0, numBytesLow, numBytesHigh, &ovlp);' @'
  {
    BOOL oe_result = osUnlockFileEx(*phFile,0,numBytesLow,numBytesHigh,&ovlp);
    DWORD oe_error = GetLastError();
    oe_stage2_event(OE_UNLOCK_RESULT,*phFile,OE_HANDLE,
        ((uint64_t)offsetHigh<<32)|offsetLow,
        ((uint64_t)numBytesHigh<<32)|numBytesLow,oe_result,
        OE_STAGE2_NO_SQLITE_RC,oe_error,0);
    SetLastError(oe_error);
    return oe_result;
  }
'@

Edit oe_native_winOpenSharedMemory '  if( pShmNode==0 ){' @'
  oe_stage2_link(pShmNode ? pShmNode : pNew,OE_NODE,pDbFd,OE_FILE);
  oe_stage2_event(OE_NODE_CHOICE,pShmNode ? pShmNode : pNew,OE_NODE,
                 pShmNode!=0,0,0,0,0,0);
  if( pShmNode==0 ){
'@
Edit oe_native_winOpenSharedMemory '      pShmNode->hSharedShm = h;' @'
      pShmNode->hSharedShm = h;
      if(rc==SQLITE_OK){
        oe_stage2_handle(pNew->zFilename,pShmNode,OE_NODE,h,pDbFd,OE_FILE,OE_NODE_HANDLE);
      }
'@
Edit oe_native_winOpenSharedMemory '    pDbFd->pShm = p;' @'
    pDbFd->pShm = p;
    oe_stage2_link(p,OE_SHM,pShmNode,OE_NODE);
    oe_stage2_event(OE_SHM_LINK,p,OE_SHM,pShmNode->isUnlocked,
                   pShmNode->nRegion,pShmNode->bUseSharedLockHandle,0,0,0);
'@
Edit oe_native_winOpenSharedMemory '    rc = winHandleOpen(pShmNode->zFilename, &p->bReadonly, &p->hShm);' @'
    rc = winHandleOpen(pShmNode->zFilename, &p->bReadonly, &p->hShm);
    if(rc==SQLITE_OK){
      oe_stage2_handle(pShmNode->zFilename,p,OE_SHM,p->hShm,pDbFd,OE_FILE,OE_SHM_HANDLE);
    }
'@
Edit winShmPurge '      winHandleClose(p->hSharedShm);' @'
      oe_stage2_event(OE_NODE_PURGE,p,OE_NODE,deleteFlag,p->nRegion,0,0,0,0);
      winHandleClose(p->hSharedShm);
      oe_stage2_forget(p->hSharedShm,OE_HANDLE);
      oe_stage2_forget(p,OE_NODE);
'@
Edit oe_native_winRead '      memcpy(pBuf, &((u8 *)(pFile->pMapRegion))[offset], amt);' @'
      memcpy(pBuf, &((u8 *)(pFile->pMapRegion))[offset], amt);
      oe_stage2_event(OE_READ_MAPPED,id,OE_FILE,offset,amt,0,0,0,0);
'@
Edit oe_native_winRead '      memcpy(pBuf, &((u8 *)(pFile->pMapRegion))[offset], nCopy);' @'
      memcpy(pBuf, &((u8 *)(pFile->pMapRegion))[offset], nCopy);
      oe_stage2_event(OE_READ_MAPPED,id,OE_FILE,offset,nCopy,0,0,0,0);
'@
Edit oe_native_winWrite '      memcpy(&((u8 *)(pFile->pMapRegion))[offset], pBuf, amt);' @'
      memcpy(&((u8 *)(pFile->pMapRegion))[offset], pBuf, amt);
      oe_stage2_event(OE_WRITE_MAPPED,id,OE_FILE,offset,amt,0,0,0,0);
'@
Edit oe_native_winWrite '      memcpy(&((u8 *)(pFile->pMapRegion))[offset], pBuf, nCopy);' @'
      memcpy(&((u8 *)(pFile->pMapRegion))[offset], pBuf, nCopy);
      oe_stage2_event(OE_WRITE_MAPPED,id,OE_FILE,offset,nCopy,0,0,0,0);
'@
Edit oe_native_winTruncate '    return SQLITE_OK;' @'
    oe_stage2_event(OE_TRUNCATE_NOOP,id,OE_FILE,nByte,pFile->nFetchOut,0,0,0,0);
    return SQLITE_OK;
'@

# Keep the original syscall table dispatch, including its original Boolean result.
$f = Get-Function oe_native_winHandleTruncate
$script:text = $script:text.Insert($f.Start, @'
static BOOL oe_winSetEndOfFile(HANDLE h, sqlite3_int64 size){
  BOOL result = osSetEndOfFile(h);
  DWORD error = GetLastError();
  oe_stage2_event(OE_SET_END_RESULT,h,OE_HANDLE,size,result,0,
                 OE_STAGE2_NO_SQLITE_RC,error,0);
  SetLastError(error);
  return result;
}

'@)
Edit oe_native_winHandleTruncate 'osSetEndOfFile(h)' 'oe_winSetEndOfFile(h,nByte)'
Edit oe_native_winTruncate 'osSetEndOfFile(pFile->h)' 'oe_winSetEndOfFile(pFile->h,nByte)'

Wrap sqlite3WalOpen 'pVfs,pDbFd,zWalName,bNoShm,mxWalSize,ppWal' '' @'
  if(oe_rc==SQLITE_OK && *ppWal){
    oe_stage2_link(*ppWal,OE_WAL,pDbFd,OE_FILE);
    oe_stage2_link((*ppWal)->pWalFd,OE_FILE,pDbFd,OE_FILE);
    oe_stage2_event(OE_WAL_OPEN,*ppWal,OE_WAL,bNoShm,mxWalSize,0,oe_rc,oe_error,0);
  }
'@

foreach ($hook in @(
    @('walIndexReadHdr','pWal,pChanged','OE_HEADER_READ_BEGIN','OE_HEADER_READ_END'),
    @('walIndexRecover','pWal','OE_RECOVER_BEGIN','OE_RECOVER_END'),
    @('walTryBeginRead','pWal,pChanged,useWal,pCnt','OE_BEGIN_READ_BEGIN','OE_BEGIN_READ_END'),
    @('walRestartLog','pWal','OE_RESTART_LOG_BEGIN','OE_RESTART_LOG_END')
)) {
    Wrap $hook[0] $hook[1] `
        (Event $hook[2] pWal OE_WAL 'pWal->hdr.mxFrame' 'pWal->hdr.nPage' 'pWal->readLock') `
        (Event $hook[3] pWal OE_WAL 'pWal->hdr.mxFrame' 'pWal->hdr.nPage' 'pWal->readLock' oe_rc oe_error)
}
Wrap walIndexWriteHdr 'pWal' `
    (Event OE_HEADER_WRITE_BEGIN pWal OE_WAL 'pWal->hdr.mxFrame' 'pWal->hdr.nPage' 'pWal->hdr.iVersion') `
    (Event OE_HEADER_WRITE_END pWal OE_WAL 'pWal->hdr.mxFrame' 'pWal->hdr.nPage' 'pWal->hdr.iVersion' 0 oe_error) void
Wrap walRestartHdr 'pWal,salt1' `
    (Event OE_RESTART_HDR_BEGIN pWal OE_WAL 'pWal->hdr.mxFrame' 'pWal->hdr.nPage' salt1) `
    (Event OE_RESTART_HDR_END pWal OE_WAL 'pWal->hdr.mxFrame' 'pWal->hdr.nPage' salt1 0 oe_error) void
Wrap sqlite3WalFindFrame 'pWal,pgno,piRead' '' `
    (Event OE_FIND_FRAME pWal OE_WAL pgno 'oe_rc==SQLITE_OK ? *piRead : 0' 'pWal->readLock' oe_rc oe_error)
Wrap sqlite3WalReadFrame 'pWal,iRead,nOut,pOut' `
    (Event OE_FRAME_READ_BEGIN pWal OE_WAL iRead nOut 'pWal->hdr.mxFrame') `
    (Event OE_FRAME_READ_END pWal OE_WAL iRead nOut 'pWal->hdr.mxFrame' oe_rc oe_error)
Wrap sqlite3WalFrames 'pWal,szPage,pList,nTruncate,isCommit,sync_flags' `
    (Event OE_FRAMES_BEGIN pWal OE_WAL szPage nTruncate isCommit) `
    (Event OE_FRAMES_END pWal OE_WAL 'pWal->hdr.mxFrame' nTruncate isCommit oe_rc oe_error)
Wrap walCheckpoint 'pWal,db,eMode,xBusy,pBusyArg,sync_flags,zBuf' `
    (Event OE_CHECKPOINT_BEGIN pWal OE_WAL eMode 'pWal->hdr.mxFrame' 'pWal->hdr.nPage') `
    (Event OE_CHECKPOINT_END pWal OE_WAL eMode 'pWal->hdr.mxFrame' 'pWal->hdr.nPage' oe_rc oe_error)
Wrap sqlite3WalClose 'pWal,db,sync_flags,nBuf,zBuf' @'
  uint64_t oe_token = oe_stage2_token(pWal,OE_WAL);
  uint64_t oe_frame = pWal ? pWal->hdr.mxFrame : 0;
  oe_stage2_event(OE_WAL_CLOSE_BEGIN,pWal,OE_WAL,oe_frame,sync_flags,nBuf,0,0,0);
'@ @'
  oe_stage2_event_token(OE_WAL_CLOSE_END,oe_token,oe_frame,sync_flags,nBuf,oe_rc,oe_error,0);
  oe_stage2_retire(oe_token);
'@
Edit oe_native_sqlite3WalClose '    walIndexClose(pWal, isDelete);' @'
    oe_stage2_event(OE_WAL_DELETE,pWal,OE_WAL,isDelete,pWal->hdr.mxFrame,0,rc,0,0);
    walIndexClose(pWal, isDelete);
'@
Edit oe_native_walIndexRecover '        isValid = walDecodeFrame(pWal, &pgno, &nTruncate, aData, aFrame);' @'
        isValid = walDecodeFrame(pWal, &pgno, &nTruncate, aData, aFrame);
        oe_stage2_event(OE_RECOVER_FRAME,pWal,OE_WAL,iFrame,
                       isValid ? pgno : 0,isValid ? nTruncate : 0,rc,0,
                       (uint64_t)(isValid!=0)<<32);
'@
foreach ($decision in @(
    @('    return 1;   /* Dirty read */',1),
    @('    return 1;   /* Malformed header - probably all zeros */',2),
    @('    return 1;   /* Checksum does not match */',3),
    @('  return 0;',0)
)) {
    Edit walIndexTryHdr $decision[0] @"
    oe_stage2_event(OE_HEADER_DECISION,pWal,OE_WAL,$($decision[1]),h1.mxFrame,h1.nPage,0,0,
                   ((uint64_t)h1.iVersion<<32)|((uint64_t)h1.isInit<<16));
$($decision[0])
"@
}
Edit readDbPage '  if( iFrame ){' @'
  oe_stage2_link(pPager,OE_PAGER,pPager->fd,OE_FILE);
  oe_stage2_event(OE_PAGE_SOURCE,pPager,OE_PAGER,pPg->pgno,iFrame,
                 pPager->pageSize,rc,0,0);
  if( iFrame ){
'@
Edit sqlite3PagerOpen '  *ppPager = pPager;' @'
  *ppPager = pPager;
  if(isOpen(pPager->fd)){
    oe_stage2_link(pPager,OE_PAGER,pPager->fd,OE_FILE);
    oe_stage2_event(OE_PAGER_BIND,pPager,OE_PAGER,0,0,0,0,0,0);
  }
'@
Edit sqlite3BtreeOpen '  *ppBtree = p;' @'
  *ppBtree = p;
  if(pBt && pBt->pPager && isOpen(pBt->pPager->fd)){
    oe_stage2_link(db,OE_DB,pBt->pPager->fd,OE_FILE);
    oe_stage2_event(OE_DB_BIND,db,OE_DB,0,0,0,0,0,0);
  }
'@
Wrap sqlite3Close 'db,forceZombie' @'
  uint64_t oe_token = oe_stage2_token(db,OE_DB);
  oe_stage2_event(OE_CLOSE_REQUEST,db,OE_DB,forceZombie,0,0,0,0,0);
'@ @'
  oe_stage2_event_token(OE_CLOSE_RESULT,oe_token,forceZombie,0,0,oe_rc,oe_error,0);
'@
Wrap sqlite3PagerClose 'pPager,db' @'
  uint64_t oe_token = oe_stage2_token(pPager,OE_PAGER);
  oe_stage2_event(OE_PAGER_CLOSE_BEGIN,pPager,OE_PAGER,0,0,0,0,0,0);
'@ @'
  oe_stage2_event_token(OE_PAGER_CLOSE_END,oe_token,0,0,0,oe_rc,oe_error,0);
  oe_stage2_retire(oe_token);
'@
Edit sqlite3LeaveMutexAndCloseZombie '    sqlite3_mutex_leave(db->mutex);' @'
    oe_stage2_event(OE_ZOMBIE_DEFERRED,db,OE_DB,db->eOpenState,0,0,0,0,0);
    sqlite3_mutex_leave(db->mutex);
'@
Edit sqlite3LeaveMutexAndCloseZombie '  sqlite3RollbackAll(db, SQLITE_OK);' @'
  oe_stage2_event(OE_ZOMBIE_FINAL,db,OE_DB,db->eOpenState,0,0,0,0,0);
  oe_stage2_forget(db,OE_DB);
  sqlite3RollbackAll(db, SQLITE_OK);
'@

$script:text = "#include `"SqliteStage2Trace.h`"`n" + $script:text
[IO.File]::WriteAllText($DestinationFile, $script:text, [Text.UTF8Encoding]::new($false))
$codes = [ordered]@{}
$value = 0
$header = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SqliteStage2Trace.h'))
$enum = [regex]::Match($header, '(?s)enum oe_stage2_code \{(.*?)\};').Groups[1].Value
foreach ($part in $enum.Split(',')) {
    $m = [regex]::Match($part.Trim(), '^(OE_\w+)(?:\s*=\s*(\d+))?$')
    if (!$m.Success) { throw 'Invalid native event enumeration.' }
    if ($m.Groups[2].Success) { $value = [int]$m.Groups[2].Value }
    $codes[$m.Groups[1].Value] = $value
    $value++
}
$schema = [ordered]@{
    schemaVersion = 1
    diagnosticOnly = $true
    normalCiAttested = $false
    magic = '0x3245544F'
    headerSize = 128
    recordSize = 256
    capacity = 2048
    byteOrder = 'little-endian'
    publication = 'sequence written last via InterlockedExchange64; sequence is reservation index + 1'
    headerOffsets = [ordered]@{
        magic=0;schema=4;recordSize=8;capacity=12;expectedParentPid=16;registeredSelfPid=20
        role=24;armed=28;reserved=32;committed=40;overflow=48;errors=56;unbound=64;dropped=72
        expired=80;configurationState=84
    }
    recordOffsets = [ordered]@{
        publishedSequence=0; qpc=8;pid=16;tid=20;eventCode=24;phase=28;objectToken=32
        relatedDbToken=40;arg0=48;arg1=56;arg2=64;sqliteRc=72;winError=76
        volume=80;fileIdLow=88;fileIdHigh=96;flags=104
    }
    eventCodes = $codes
    phases = [ordered]@{none=0;oldReader=1;childMutation=2;rawSampleBegin=3;rawSampleEnd=4;reopenBegin=5;firstEquality=6;cleanup=7}
    objectKinds = [ordered]@{file=1;handle=2;shmNode=3;shmConnection=4;wal=5;pager=6;connection=7}
    flags = [ordered]@{
        fileRoleMask = 255;db=1;wal=2;shm=3;fileIdKnown=256;fileIdQueryFailed=512
        upper32Bits = 'event-specific: original native lock flags, header version, or valid recovered frame'
        headerInitBit = 16
    }
    sqliteRcNotReturned = [int]::MinValue
    argumentConventions = [ordered]@{
        IO = 'arg0=offset/requested bytes, arg1=amount/chunk size, arg2=sticky lastErrno (supplementary only)'
        nativeLock = 'arg0=offset, arg1=byte count, arg2=ORIGINAL Win32 BOOL; winError captured immediately'
        headerDecision = 'arg0=0 trusted/1 mismatched copies/2 uninitialized/3 checksum mismatch; arg1=mxFrame; arg2=nPage'
        recoveryFrame = 'arg0=frame; arg1=page iff originally valid; arg2=commit nTruncate iff originally valid'
        pageSource = 'arg0=page; arg1=original selected WAL frame (0=physical DB); arg2=page size'
        fileHandle = 'arg0=related native object token; arg1=parent object token; volume/fileId identify already-owned hFile'
        nodeChoice = 'arg0=1 reused existing process-global node, 0 new'
    }
    limitations = @(
        'No payload, raw path, pointer, extra SQLite reader, probe lock, repair or checkpoint.'
        'FileIdInfo query on each already-owned native file handle once is a declared scheduling perturbation.'
        'Wrapper function exit GetLastError is not necessarily the originating syscall error; raw lock/SetEnd events are immediate.'
        'Header decisions use the ORIGINAL local header copies; no added SHM-header read.'
        'Private native state and owned IO parameters do not prove committed bytes unchanged through read or exclude outside actors.'
        'Missing association, native metadata errors, overflow, unpublished reservation or expiry mean Incomplete.'
        'Fixed 512 object lifetimes per producer; no reuse of token slots.'
        'Phase6 first equality publishes then closes native emission; only the explicit phase7 terminal marker remains allowed.'
    )
}
[IO.File]::WriteAllText($SchemaFile, ($schema | ConvertTo-Json -Depth 9), [Text.UTF8Encoding]::new($false))
$manifest = [ordered]@{
    schemaVersion=1;sourceAmalgamationSha3_256=$sourceSha3;nativeHooks=$script:changes.ToArray()
    patchedSourceSha256=(Get-FileHash $DestinationFile -Algorithm SHA256).Hash
    traceHeaderSha256=(Get-FileHash (Join-Path $PSScriptRoot 'SqliteStage2Trace.h') -Algorithm SHA256).Hash
    traceImplementationSha256=(Get-FileHash (Join-Path $PSScriptRoot 'SqliteStage2Trace.c') -Algorithm SHA256).Hash
    eventSchemaSha256=(Get-FileHash $SchemaFile -Algorithm SHA256).Hash
}
[IO.File]::WriteAllText($PatchManifestFile, ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
