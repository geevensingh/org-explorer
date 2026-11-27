#include "SqliteStage2Trace.h"
#include <errno.h>
#include <stddef.h>
#include <string.h>
#include <wchar.h>

#define OE_OBJECTS 512
#define OE_PATH_BYTES 4096

typedef struct oe_header {
  uint32_t magic, schema, recordSize, capacity, parentPid, selfPid, role, armed;
  volatile LONG64 reserved, committed, overflow, errors, unbound, dropped;
  volatile LONG expired, configurationState;
  uint8_t padding[40];
} oe_header;

typedef struct oe_record {
  volatile LONG64 sequence;
  uint64_t qpc;
  uint32_t pid, tid, code, phase;
  uint64_t token, dbToken, arg0, arg1, arg2;
  int32_t rc;
  uint32_t winError;
  uint64_t volume, idLow, idHigh, flags;
  uint8_t padding[144];
} oe_record;

typedef struct oe_object {
  const void *key;
  unsigned kind, fileRole;
  volatile LONG state;
  uint64_t token;
  volatile LONG64 dbToken;
  HANDLE handle;
  uint64_t volume, low, high, identityFlags;
} oe_object;

typedef char oe_header_size[(sizeof(oe_header)==128) ? 1 : -1];
typedef char oe_record_size[(sizeof(oe_record)==256) ? 1 : -1];
typedef char oe_record_flags_offset[(offsetof(oe_record,flags)==104) ? 1 : -1];

static oe_header *oe_map;
static HANDLE oe_mapping;
static unsigned oe_pid, oe_role;
static volatile LONG oe_bound, oe_phase, oe_nextObject, oe_ended;
static oe_object oe_objects[OE_OBJECTS];
static char oe_paths[3][OE_PATH_BYTES];

/* All telemetry, including failure handling, preserves the original Win32/CRT state. */
#define OE_GUARD_BEGIN \
  DWORD oe_savedError = GetLastError(); int oe_savedErrno = errno; \
  unsigned long oe_savedDos = 0; \
  _get_doserrno(&oe_savedDos); \
  __try {
#define OE_GUARD_END \
  } __except(EXCEPTION_EXECUTE_HANDLER) { \
    __try { if(oe_map) InterlockedIncrement64(&oe_map->errors); } \
    __except(EXCEPTION_EXECUTE_HANDLER) {} \
  } \
  _set_doserrno(oe_savedDos); errno = oe_savedErrno; SetLastError(oe_savedError)

static int oe_enabled(void){
  return oe_bound==2 && oe_map && GetCurrentProcessId()==oe_pid &&
    oe_map->magic==OE_STAGE2_MAGIC && oe_map->schema==1 &&
    oe_map->recordSize==OE_STAGE2_RECORD_SIZE &&
    oe_map->capacity==OE_STAGE2_CAPACITY &&
    oe_map->selfPid==oe_pid && oe_map->role==oe_role &&
    oe_map->armed==1 && oe_map->expired==0;
}

static int oe_active(void){
  return oe_enabled() && oe_ended==0;
}

static unsigned oe_path(const char *path){
  unsigned i;
  if( !path ) return 0;
  for(i=0; i<3; ++i){
    if( strcmp(path,oe_paths[i])==0 ) return i+1;
  }
  return 0;
}

static oe_object *oe_find(const void *key, unsigned kind){
  int i, n = oe_nextObject;
  if( !key ) return 0;
  if( n>OE_OBJECTS ) n=OE_OBJECTS;
  for(i=0; i<n; ++i){
    oe_object *o = &oe_objects[i];
    if( o->state==1 && o->key==key && o->kind==kind ) return o;
  }
  return 0;
}

static oe_object *oe_new(const void *key, unsigned kind, unsigned fileRole,
                         uint64_t dbToken){
  LONG slot;
  oe_object *o = oe_find(key,kind);
  if( o ) return o;
  if( !key ) return 0;
  slot = InterlockedIncrement(&oe_nextObject)-1;
  if( slot>=OE_OBJECTS ){
    InterlockedIncrement64(&oe_map->unbound);
    return 0;
  }
  o = &oe_objects[slot];
  o->key=key; o->kind=kind; o->fileRole=fileRole;
  o->token=((uint64_t)oe_role<<56) | (uint64_t)(slot+1);
  o->dbToken=(kind==OE_FILE && fileRole==1) ? o->token : dbToken;
  MemoryBarrier();
  InterlockedExchange(&o->state,1);
  return o;
}

static void oe_identity(oe_object *o, HANDLE handle){
  FILE_ID_INFO id;
  if( !o || !handle || handle==INVALID_HANDLE_VALUE || o->handle ) return;
  o->handle=handle;
  /* Exactly one metadata query on this already-owned native handle lifetime. */
  if( GetFileInformationByHandleEx(handle,FileIdInfo,&id,sizeof(id)) ){
    o->volume=id.VolumeSerialNumber;
    memcpy(&o->low,id.FileId.Identifier,8);
    memcpy(&o->high,id.FileId.Identifier+8,8);
    o->identityFlags=0x100;
  }else{
    o->identityFlags=0x200;
    InterlockedIncrement64(&oe_map->errors);
  }
}

static void oe_emit(unsigned code, oe_object *o, uint64_t a, uint64_t b,
                    uint64_t c, int rc, DWORD error, uint64_t flags){
  LONG64 slot;
  LARGE_INTEGER now;
  oe_record *r;
  if( !oe_enabled() ) return;
  if( code!=OE_PHASE && oe_ended ) return;
  if( !o && code!=OE_PHASE ){
    InterlockedIncrement64(&oe_map->unbound);
    return;
  }
  slot=InterlockedIncrement64(&oe_map->reserved)-1;
  if( slot<0 || slot>=OE_STAGE2_CAPACITY ){
    InterlockedDecrement64(&oe_map->reserved);
    InterlockedIncrement64(&oe_map->overflow);
    InterlockedIncrement64(&oe_map->dropped);
    return;
  }
  r=(oe_record*)((uint8_t*)oe_map+OE_STAGE2_HEADER_SIZE)+slot;
  memset((uint8_t*)r+sizeof(r->sequence),0,sizeof(*r)-sizeof(r->sequence));
  QueryPerformanceCounter(&now);
  r->qpc=(uint64_t)now.QuadPart; r->pid=oe_pid; r->tid=GetCurrentThreadId();
  r->code=code; r->phase=(uint32_t)oe_phase;
  r->arg0=a; r->arg1=b; r->arg2=c; r->rc=rc; r->winError=error;
  if( o ){
    r->token=o->token; r->dbToken=(uint64_t)o->dbToken;
    r->volume=o->volume; r->idLow=o->low; r->idHigh=o->high;
    flags|=o->fileRole|o->identityFlags;
  }
  r->flags=flags;
  MemoryBarrier();
  InterlockedExchange64(&r->sequence,slot+1);
  InterlockedIncrement64(&oe_map->committed);
}

void oe_stage2_event(unsigned code, const void *key, unsigned kind,
                     uint64_t a, uint64_t b, uint64_t c,
                     int rc, DWORD error, uint64_t flags){
  OE_GUARD_BEGIN
    if( oe_active() ) oe_emit(code,oe_find(key,kind),a,b,c,rc,error,flags);
  OE_GUARD_END;
}

void oe_stage2_file(const char *path, const void *key, HANDLE handle,
                    int flags, int rc, DWORD error){
  OE_GUARD_BEGIN
    if( oe_active() ){
      unsigned role=oe_path(path);
      if( role ){
        oe_object *o=oe_new(key,OE_FILE,role,0);
        if( rc==0 ){
          oe_object *h;
          oe_identity(o,handle);
          h=oe_new((const void*)handle,OE_HANDLE,role,o ? (uint64_t)o->dbToken : 0);
          if( o && h ){
            h->handle=handle; h->volume=o->volume; h->low=o->low; h->high=o->high;
            h->identityFlags=o->identityFlags;
          }
        }
        oe_emit(OE_FILE_OPEN,o,(uint64_t)flags,0,0,rc,error,0);
        if( rc!=0 && o ) InterlockedExchange(&o->state,2);
      }
    }
  OE_GUARD_END;
}

void oe_stage2_link(const void *key, unsigned kind,
                    const void *parent, unsigned parentKind){
  OE_GUARD_BEGIN
    if( oe_active() ){
      oe_object *p=oe_find(parent,parentKind);
      if( p ){
        oe_object *o=oe_new(key,kind,p->fileRole,(uint64_t)p->dbToken);
        if( o ){
          InterlockedExchange64(&o->dbToken,p->dbToken);
          if( kind==OE_FILE && o->handle ){
            oe_object *h=oe_find((const void*)o->handle,OE_HANDLE);
            if( h ) InterlockedExchange64(&h->dbToken,p->dbToken);
          }
        }
      }else{
        InterlockedIncrement64(&oe_map->unbound);
      }
    }
  OE_GUARD_END;
}

void oe_stage2_handle(const char *path, const void *key, unsigned kind,
                      HANDLE handle, const void *parent, unsigned parentKind,
                      unsigned code){
  OE_GUARD_BEGIN
    if( oe_active() && oe_path(path)==3 ){
      oe_object *p=oe_find(parent,parentKind);
      if( p ){
        oe_object *o=oe_new(key,kind,3,(uint64_t)p->dbToken);
        oe_object *h=oe_new((const void*)handle,OE_HANDLE,3,(uint64_t)p->dbToken);
        oe_identity(h,handle);
        if( o && h ){
          oe_emit(code,h,o->token,p->token,0,0,0,0);
        }
      }else{
        InterlockedIncrement64(&oe_map->unbound);
      }
    }
  OE_GUARD_END;
}

void oe_stage2_forget(const void *key, unsigned kind){
  OE_GUARD_BEGIN
    if( oe_active() ){
      oe_object *o=oe_find(key,kind);
      if( o ) InterlockedExchange(&o->state,2);
    }
  OE_GUARD_END;
}

uint64_t oe_stage2_token(const void *key, unsigned kind){
  uint64_t token=0;
  OE_GUARD_BEGIN
    if( oe_active() ){
      oe_object *o=oe_find(key,kind);
      if( o ) token=o->token;
    }
  OE_GUARD_END;
  return token;
}

static oe_object *oe_from_token(uint64_t token){
  uint64_t index=(token & UINT64_C(0x00ffffffffffffff));
  if( index==0 || index>OE_OBJECTS || (token>>56)!=oe_role ) return 0;
  return oe_objects[index-1].token==token ? &oe_objects[index-1] : 0;
}

void oe_stage2_retire(uint64_t token){
  OE_GUARD_BEGIN
    if( oe_active() ){
      oe_object *o=oe_from_token(token);
      if( o ) InterlockedExchange(&o->state,2);
    }
  OE_GUARD_END;
}

void oe_stage2_event_token(unsigned code, uint64_t token,
                           uint64_t a, uint64_t b, uint64_t c,
                           int rc, DWORD error, uint64_t flags){
  OE_GUARD_BEGIN
    if( oe_active() ) oe_emit(code,oe_from_token(token),a,b,c,rc,error,flags);
  OE_GUARD_END;
}

static int oe_mapping_name(const wchar_t *name, unsigned role){
  static const wchar_t prefix[]=L"Local\\OrgExplorer-SQLite-Stage2-";
  const wchar_t *suffix=role==1 ? L"-parent" : L"-child";
  size_t n=wcslen(prefix), i;
  if( !name || wcsncmp(name,prefix,n)!=0 ) return 0;
  for(i=0; i<32; ++i){
    wchar_t c=name[n+i];
    if( !((c>=L'0' && c<=L'9') || (c>=L'a' && c<=L'f')) ) return 0;
  }
  return wcscmp(name+n+32,suffix)==0;
}

__declspec(dllexport) int __cdecl oe_stage2_bind(
    int role, unsigned parentPid, unsigned selfPid, const wchar_t *db,
    const wchar_t *wal, const wchar_t *shm, const wchar_t *mappingName){
  int result=1;
  OE_GUARD_BEGIN
    if( (role==1 || role==2) && parentPid!=0 && selfPid==GetCurrentProcessId() &&
        (role!=1 || selfPid==parentPid) &&
        (role!=2 || selfPid!=parentPid) &&
        oe_mapping_name(mappingName,(unsigned)role) &&
        InterlockedCompareExchange(&oe_bound,1,0)==0 ){
      const wchar_t *paths[3]={db,wal,shm};
      unsigned i;
      int valid=1;
      HANDLE mapping=0;
      oe_header *view=0;
      for(i=0; i<3 && valid; ++i){
        if( !paths[i] || !((paths[i][0] && paths[i][1]==L':' &&
             paths[i][2]==L'\\') || (paths[i][0]==L'\\' && paths[i][1]==L'\\')) ||
            !WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,paths[i],-1,
                                oe_paths[i],OE_PATH_BYTES,0,0) ) valid=0;
      }
      if( valid && (strcmp(oe_paths[0],oe_paths[1])==0 ||
          strcmp(oe_paths[0],oe_paths[2])==0 ||
          strcmp(oe_paths[1],oe_paths[2])==0) ) valid=0;
      if( valid ) mapping=OpenFileMappingW(FILE_MAP_READ|FILE_MAP_WRITE,FALSE,mappingName);
      if( mapping ) view=(oe_header*)MapViewOfFile(mapping,FILE_MAP_READ|FILE_MAP_WRITE,
                        0,0,OE_STAGE2_HEADER_SIZE+OE_STAGE2_CAPACITY*OE_STAGE2_RECORD_SIZE);
      if( view && view->magic==OE_STAGE2_MAGIC && view->schema==1 &&
          view->recordSize==OE_STAGE2_RECORD_SIZE &&
          view->capacity==OE_STAGE2_CAPACITY && view->parentPid==parentPid &&
          view->role==(unsigned)role && view->armed==1 && view->expired==0 &&
          view->reserved==0 && view->committed==0 &&
          (view->selfPid==0 || view->selfPid==selfPid) &&
          ((unsigned)InterlockedCompareExchange((volatile LONG*)&view->selfPid,
                                                (LONG)selfPid,0)==0 ||
           view->selfPid==selfPid) ){
        oe_mapping=mapping; oe_map=view; oe_pid=selfPid; oe_role=(unsigned)role;
        InterlockedExchange(&view->configurationState,1);
        MemoryBarrier(); InterlockedExchange(&oe_bound,2);
        result=0;
      }else{
        if( view ) UnmapViewOfFile(view);
        if( mapping ) CloseHandle(mapping);
        InterlockedExchange(&oe_bound,0);
      }
    }
  OE_GUARD_END;
  return result;
}

__declspec(dllexport) void __cdecl oe_stage2_mark(unsigned phase,
                                                uint64_t a, uint64_t b){
  OE_GUARD_BEGIN
    if( oe_enabled() ){
      if( phase<=7 && (!oe_ended || phase==7) ){
        InterlockedExchange(&oe_phase,(LONG)phase);
        oe_emit(OE_PHASE,0,a,b,0,0,0,0);
        if( phase>=6 ) InterlockedExchange(&oe_ended,1);
      }else{
        InterlockedIncrement64(&oe_map->errors);
      }
    }
  OE_GUARD_END;
}
