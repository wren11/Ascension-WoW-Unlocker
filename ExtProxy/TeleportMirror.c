#include "TeleportMirror.h"
#include <windows.h>
#include <string.h>

void ProxyLogLine(const char* msg);
uint32_t PktIpcOwnerPid(void);
uint32_t PktIpcThisInstance(void);

static HANDLE g_map;
static TeleMirrorSlot* g_slot;
static CRITICAL_SECTION g_cs;
static int g_ready;

void TeleMirrorStart(void)
{
    SECURITY_ATTRIBUTES sa;
    SECURITY_DESCRIPTOR sd;
    if (g_ready) return;
    InitializeCriticalSection(&g_cs);
    InitializeSecurityDescriptor(&sd, SECURITY_DESCRIPTOR_REVISION);
    SetSecurityDescriptorDacl(&sd, TRUE, NULL, FALSE);
    sa.nLength = sizeof(sa);
    sa.lpSecurityDescriptor = &sd;
    sa.bInheritHandle = FALSE;

    g_map = CreateFileMappingA(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE,
        0, (DWORD)sizeof(TeleMirrorSlot), TELE_MIRROR_NAME);
    if (!g_map) {
        ProxyLogLine("telemirror: CreateFileMapping failed");
        return;
    }
    g_slot = (TeleMirrorSlot*)MapViewOfFile(g_map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(TeleMirrorSlot));
    if (!g_slot) {
        ProxyLogLine("telemirror: MapViewOfFile failed");
        CloseHandle(g_map);
        g_map = NULL;
        return;
    }
    if (g_slot->magic != TELE_MIRROR_MAGIC) {
        memset(g_slot, 0, sizeof(*g_slot));
        g_slot->magic = TELE_MIRROR_MAGIC;
        g_slot->seq = 0;
    }
    g_ready = 1;
    ProxyLogLine("telemirror V6: ready (pose+combatGuid)");
}

void TeleMirrorStop(void)
{
    if (!g_ready) return;
    EnterCriticalSection(&g_cs);
    if (g_slot) { UnmapViewOfFile(g_slot); g_slot = NULL; }
    if (g_map) { CloseHandle(g_map); g_map = NULL; }
    LeaveCriticalSection(&g_cs);
    DeleteCriticalSection(&g_cs);
    g_ready = 0;
}

uint32_t TeleMirrorPublishEx(uint32_t map, float x, float y, float z, float o, uint32_t flags,
                             uint64_t combat_guid, uint32_t publisher_instance)
{
    uint32_t seq;
    if (!g_ready || !g_slot) return 0;
    EnterCriticalSection(&g_cs);
    seq = (uint32_t)InterlockedIncrement((volatile LONG*)&g_slot->seq);
    if (seq == 0)
        seq = (uint32_t)InterlockedIncrement((volatile LONG*)&g_slot->seq);
    g_slot->map = map;
    g_slot->x = x;
    g_slot->y = y;
    g_slot->z = z;
    g_slot->o = o;
    g_slot->leader_pid = PktIpcOwnerPid();
    g_slot->flags = flags;
    g_slot->tick_ms = GetTickCount();
    g_slot->combat_guid = combat_guid;
    g_slot->publisher_instance = publisher_instance ? publisher_instance : PktIpcThisInstance();
    g_slot->magic = TELE_MIRROR_MAGIC;
    LeaveCriticalSection(&g_cs);
    return seq;
}

uint32_t TeleMirrorPublish(uint32_t map, float x, float y, float z, float o, uint32_t flags)
{
    return TeleMirrorPublishEx(map, x, y, z, o, flags, 0, 0);
}

int TeleMirrorPeek(TeleMirrorSlot* out)
{
    if (!g_ready || !g_slot || !out) return 0;
    EnterCriticalSection(&g_cs);
    *out = *g_slot;
    LeaveCriticalSection(&g_cs);
    return out->magic == TELE_MIRROR_MAGIC;
}

uint32_t TeleMirrorSeq(void)
{
    if (!g_ready || !g_slot) return 0;
    return g_slot->seq;
}
