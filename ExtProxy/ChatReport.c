#include "ChatReport.h"
#include "PktIpc.h"
#include <windows.h>
#include <stdio.h>
#include <string.h>

void ProxyLogLine(const char* msg);
uint32_t PktIpcThisInstance(void);

static HANDLE g_map;
static ChatReportHeader* g_hdr;
static ChatReportSlot* g_slots;
static CRITICAL_SECTION g_cs;
static int g_ready;
static char g_map_name[96];

static void CopyTrunc(char* dst, int cap, const char* src)
{
    int i = 0;
    if (!dst || cap < 2) return;
    dst[0] = 0;
    if (!src) return;
    while (src[i] && i < cap - 1) {
        unsigned char c = (unsigned char)src[i];
        /* Keep printable UTF-8 bytes; drop NULs / controls except TAB. */
        if (c >= 0x20 || c == '\t')
            dst[i] = (char)c;
        else
            dst[i] = ' ';
        i++;
    }
    dst[i] = 0;
}

void ChatReportStart(uint32_t owner_pid)
{
    SECURITY_ATTRIBUTES sa;
    SECURITY_DESCRIPTOR sd;
    DWORD bytes;
    if (g_ready) return;
    InitializeCriticalSection(&g_cs);
    InitializeSecurityDescriptor(&sd, SECURITY_DESCRIPTOR_REVISION);
    SetSecurityDescriptorDacl(&sd, TRUE, NULL, FALSE);
    sa.nLength = sizeof(sa);
    sa.lpSecurityDescriptor = &sd;
    sa.bInheritHandle = FALSE;

    if (owner_pid == 0)
        owner_pid = GetCurrentProcessId();
    _snprintf(g_map_name, sizeof(g_map_name), "%s_%u", CHAT_REPORT_NAME_BASE, (unsigned)owner_pid);
    bytes = (DWORD)(sizeof(ChatReportHeader) + CHAT_REPORT_SLOTS * sizeof(ChatReportSlot));
    g_map = CreateFileMappingA(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE, 0, bytes, g_map_name);
    if (!g_map) {
        ProxyLogLine("chatrep: CreateFileMapping failed");
        return;
    }
    g_hdr = (ChatReportHeader*)MapViewOfFile(g_map, FILE_MAP_ALL_ACCESS, 0, 0, bytes);
    if (!g_hdr) {
        ProxyLogLine("chatrep: MapViewOfFile failed");
        CloseHandle(g_map);
        g_map = NULL;
        return;
    }
    g_slots = (ChatReportSlot*)(g_hdr + 1);
    if (g_hdr->magic != CHAT_REPORT_MAGIC || g_hdr->slot_count != CHAT_REPORT_SLOTS) {
        memset(g_hdr, 0, bytes);
        g_hdr->magic = CHAT_REPORT_MAGIC;
        g_hdr->slot_count = CHAT_REPORT_SLOTS;
        g_hdr->owner_pid = owner_pid;
        g_hdr->write_seq = 0;
    }
    g_ready = 1;
    {
        char line[128];
        _snprintf(line, sizeof(line), "chatrep: ready %s", g_map_name);
        ProxyLogLine(line);
    }
}

void ChatReportStop(void)
{
    if (!g_ready) return;
    EnterCriticalSection(&g_cs);
    if (g_hdr) { UnmapViewOfFile(g_hdr); g_hdr = NULL; g_slots = NULL; }
    if (g_map) { CloseHandle(g_map); g_map = NULL; }
    LeaveCriticalSection(&g_cs);
    DeleteCriticalSection(&g_cs);
    g_ready = 0;
}

int ChatReportPush(uint32_t kind, uint64_t guid, const char* sender, const char* channel,
                   const char* message, const char* extra,
                   int level, int class_id, int race, int gender)
{
    uint32_t seq, idx;
    ChatReportSlot* s;
    if (!g_ready || !g_hdr || !g_slots) return 0;
    EnterCriticalSection(&g_cs);
    seq = ++g_hdr->write_seq;
    if (seq == 0) seq = ++g_hdr->write_seq;
    idx = (seq - 1u) % CHAT_REPORT_SLOTS;
    s = &g_slots[idx];
    memset(s, 0, sizeof(*s));
    s->seq = seq;
    s->tick_ms = GetTickCount();
    s->kind = kind;
    s->instance_id = PktIpcThisInstance();
    s->guid = guid;
    s->level = level;
    s->class_id = class_id;
    s->race = race;
    s->gender = gender;
    CopyTrunc(s->sender, CHAT_REPORT_SENDER, sender);
    CopyTrunc(s->channel, CHAT_REPORT_CHANNEL, channel);
    CopyTrunc(s->message, CHAT_REPORT_MSG, message);
    CopyTrunc(s->extra, CHAT_REPORT_EXTRA, extra);
    LeaveCriticalSection(&g_cs);
    return 1;
}

const char* ChatReportMapName(void)
{
    return g_ready ? g_map_name : "";
}
