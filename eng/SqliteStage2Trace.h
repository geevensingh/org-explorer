#ifndef OE_SQLITE_STAGE2_TRACE_H
#define OE_SQLITE_STAGE2_TRACE_H

#include <windows.h>
#include <stdint.h>

#define OE_STAGE2_MAGIC 0x3245544Fu
#define OE_STAGE2_CAPACITY 2048u
#define OE_STAGE2_RECORD_SIZE 256u
#define OE_STAGE2_HEADER_SIZE 128u
#define OE_STAGE2_NO_SQLITE_RC INT32_MIN

enum oe_stage2_kind {
  OE_FILE = 1, OE_HANDLE = 2, OE_NODE = 3, OE_SHM = 4,
  OE_WAL = 5, OE_PAGER = 6, OE_DB = 7
};

enum oe_stage2_code {
  OE_PHASE = 1,
  OE_FILE_OPEN = 10, OE_READ_BEGIN, OE_READ_END, OE_WRITE_BEGIN, OE_WRITE_END,
  OE_TRUNCATE_BEGIN, OE_TRUNCATE_END, OE_FILE_CLOSE_BEGIN, OE_FILE_CLOSE_END,
  OE_READ_MAPPED, OE_WRITE_MAPPED, OE_TRUNCATE_NOOP, OE_SET_END_RESULT,
  OE_NODE_CHOICE = 30, OE_NODE_HANDLE, OE_SHM_HANDLE, OE_SHM_LINK,
  OE_SHM_OPEN_BEGIN, OE_SHM_OPEN_END, OE_DMS_BEGIN, OE_DMS_END,
  OE_LOCK_RESULT, OE_UNLOCK_RESULT, OE_ZERO_BEGIN, OE_ZERO_END,
  OE_HANDLE_TRUNCATE_BEGIN, OE_HANDLE_TRUNCATE_END, OE_NODE_PURGE,
  OE_SHM_CLOSE_BEGIN, OE_SHM_CLOSE_END,
  OE_WAL_OPEN = 50, OE_HEADER_DECISION, OE_HEADER_READ_BEGIN, OE_HEADER_READ_END,
  OE_RECOVER_BEGIN, OE_RECOVER_END, OE_RECOVER_FRAME,
  OE_HEADER_WRITE_BEGIN, OE_HEADER_WRITE_END, OE_BEGIN_READ_BEGIN, OE_BEGIN_READ_END,
  OE_FIND_FRAME, OE_FRAME_READ_BEGIN, OE_FRAME_READ_END, OE_PAGE_SOURCE,
  OE_CHECKPOINT_BEGIN, OE_CHECKPOINT_END, OE_RESTART_HDR_BEGIN, OE_RESTART_HDR_END,
  OE_RESTART_LOG_BEGIN, OE_RESTART_LOG_END, OE_FRAMES_BEGIN, OE_FRAMES_END,
  OE_WAL_CLOSE_BEGIN, OE_WAL_CLOSE_END, OE_WAL_DELETE,
  OE_DB_BIND = 80, OE_PAGER_BIND, OE_CLOSE_REQUEST, OE_CLOSE_RESULT,
  OE_ZOMBIE_DEFERRED, OE_ZOMBIE_FINAL, OE_PAGER_CLOSE_BEGIN, OE_PAGER_CLOSE_END
};

/* Pointers are private association keys. Only generated tokens leave this DLL. */
void oe_stage2_file(const char *, const void *, HANDLE, int, int, DWORD);
void oe_stage2_link(const void *, unsigned, const void *, unsigned);
void oe_stage2_handle(const char *, const void *, unsigned, HANDLE,
                     const void *, unsigned, unsigned);
void oe_stage2_forget(const void *, unsigned);
uint64_t oe_stage2_token(const void *, unsigned);
void oe_stage2_retire(uint64_t);
void oe_stage2_event_token(unsigned, uint64_t, uint64_t, uint64_t, uint64_t,
                          int, DWORD, uint64_t);
void oe_stage2_btree_binding(const void *, const void *, const void *, int, int);
uint64_t oe_stage2_wal_close_begin(const void *, uint64_t, uint64_t, uint64_t);
void oe_stage2_wal_close_end(const void *, uint64_t, uint64_t, uint64_t, uint64_t,
                            int, DWORD);
void oe_stage2_event(unsigned, const void *, unsigned,
                    uint64_t, uint64_t, uint64_t, int, DWORD, uint64_t);

__declspec(dllexport) int __cdecl oe_stage2_bind(
    int, unsigned, unsigned, const wchar_t *, const wchar_t *,
    const wchar_t *, const wchar_t *);
__declspec(dllexport) void __cdecl oe_stage2_mark(unsigned, uint64_t, uint64_t);

#endif
