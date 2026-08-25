

#include "MovementConfig.h"
#include "PktIpc.h"
#include "ObjectMgr.h"
#include "FogExplore.h"
#include "NavHeight.h"
#include "NavPath.h"
#include "OverlayD3d9.h"
#include "TeleportMirror.h"
#include "InstanceBus.h"
#include "ChatReport.h"
#include "OffsetResolve.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <math.h>
#include <float.h>
#include <stdlib.h>

#if defined(__clang__) || defined(__GNUC__)
#define THISCALL __attribute__((thiscall))
#else
#define THISCALL __thiscall
#endif

typedef struct CDataStore {
    void* vtable;
    uint8_t* buffer;
    uint32_t base;
    uint32_t alloc;
    uint32_t size;
    uint32_t read_pos;
} CDataStore;

typedef void(__cdecl* QueuePacketFn)(CDataStore* packet);
static int PtrReadable(const void* p, size_t n);
static int ProxyAntiAfkPulse(int force);
static int InstallAfkIdlePatch(void);
static void RestoreAfkIdlePatch(void);
static int __cdecl GmAntiAfk_Lua(void* L);
static int __cdecl GmCharCreateUnlock_Lua(void* L);
static int __cdecl GmCharCreateForce_Lua(void* L);
static int __cdecl GmCharCreateChaos_Lua(void* L);
static void CcInstallSafeSelectWrappers(void);

static HMODULE g_self = NULL;
static HMODULE g_real = NULL;
static uint8_t* g_real_base = NULL;
static uint8_t* g_ascension = NULL;

static uint8_t* g_queue_stub = NULL;
static QueuePacketFn g_queue_tramp = NULL;
static uint8_t g_queue_stolen[16];
static size_t g_queue_stolen_len = 0;
int g_hooked = 0;

static uint8_t* g_send_stub = NULL;
static uint8_t g_send_stolen[16];
static size_t g_send_stolen_len = 0;
int g_send_hooked = 0;
volatile LONG g_send_hook_calls = 0;

static uint8_t* g_in_stub = NULL;
static uint8_t* g_in_detour = NULL;
static uint8_t g_in_stolen[16];
static size_t g_in_stolen_len = 0;
static int g_in_hooked = 0;
static volatile LONG g_in_hook_calls = 0;

static volatile PVOID g_last_recv_ctx = NULL;
static volatile DWORD g_last_recv_ctx_tick = 0;

static void* g_fn_reset = NULL;

enum { kInjBufCount = 16, kInjBufSize = 256 };

enum { kRepBufCount = 4 };
static uint8_t g_rep_bufs[kRepBufCount][PKT_REPLAY_MAX];
static CDataStore g_rep_pkts[kRepBufCount];
static volatile LONG g_rep_buf_i = 0;

static CRITICAL_SECTION g_cast_cs;

static volatile LONG g_always_unrestricted = 1;
static volatile LONG g_fire_lua_cast = 0;

enum { kLuaScriptMax = 24576, kLuaQueueSlots = 64 };
static char g_lua_queue[kLuaQueueSlots][kLuaScriptMax];
static volatile LONG g_lua_q_head = 0;
static volatile LONG g_lua_q_tail = 0;
static volatile LONG g_lua_q_count = 0;
static DWORD g_last_taint_clear = 0;
static DWORD g_popup_seed_tick = 0;
/* Seed once after UI hook — never from HandleAscInject (that self-looped WakeUi→drain→seed). */
static volatile LONG g_popup_seeded = 0;

typedef struct SecureCtx {
    uint32_t taint;
    uint32_t depth;
    uint32_t lock;
} SecureCtx;

static void HandleAscInject(void);
static void ConsumePendingLuaCast(void);
static void RegisterLuaApis(void);
static void* ProxyLuaState(void);

static int InjectClientPacket(const uint8_t* bytes, uint32_t n);
static void SanitizeTpMoveBuf(uint8_t* buf, uint32_t len);
static void PatchReplayLegitimacy(uint8_t* raw, uint32_t n);
static int __cdecl GmSendBookmark_Lua(void* L);
static int __cdecl SendPacketAct_Lua(void* L);
static int __cdecl GmPacketLoop_Lua(void* L);
static int __cdecl GmPacketBurst_Lua(void* L);
static int __cdecl GmBookmarkInfo_Lua(void* L);
static int __cdecl GmSharedCount_Lua(void* L);
static int __cdecl GmSharedObject_Lua(void* L);
static int __cdecl GmSharedObjects_Lua(void* L);
static int __cdecl GmSharedPlayers_Lua(void* L);
static int __cdecl GmSharedPlayer_Lua(void* L);
static int __cdecl GmInstanceInfo_Lua(void* L);
static int __cdecl GmGetInstanceCount_Lua(void* L);
static int __cdecl GmGetInstanceObject_Lua(void* L);
static int __cdecl GmGetInstance_Lua(void* L);
static int __cdecl GmSharedNearby_Lua(void* L);
static int __cdecl GmSharedNearbyObject_Lua(void* L);
static int __cdecl GmPublishName_Lua(void* L);
static int __cdecl GmResolveInstance_Lua(void* L);
static int __cdecl GmRemoteCall_Lua(void* L);
static int __cdecl GmRpcCapture_Lua(void* L);
static int __cdecl GmRpcFail_Lua(void* L);
static int __cdecl GmListInstances_Lua(void* L);
static int __cdecl GmUnitName_Lua(void* L);
static int __cdecl GmReportChat_Lua(void* L);
static int __cdecl GmReportPlayer_Lua(void* L);
static int __cdecl GmSetClipboard_Lua(void* L);

static uint64_t ParseHexGuid(const char* s);
static uint64_t ProxyReadGuidSlot(uint32_t rva);

static uint32_t ProxyInteractOpcodeFor(uint64_t guid);

static uint32_t EnsureMoverGuid(void);
static int EnsureMoveTemplate(void);
static void KickLearnMove(void);

typedef int(__cdecl* CastSpellByNameLuaFn)(void* L);
static uint8_t* g_cast_by_name_stub = NULL;
static CastSpellByNameLuaFn g_cast_by_name_tramp = NULL;
static uint8_t g_cast_by_name_stolen[16];
static size_t g_cast_by_name_stolen_len = 0;
static int g_cast_by_name_hooked = 0;

static uint8_t* g_cast_by_id_stub = NULL;
static CastSpellByNameLuaFn g_cast_by_id_tramp = NULL;
static uint8_t g_cast_by_id_stolen[16];
static size_t g_cast_by_id_stolen_len = 0;
static int g_cast_by_id_hooked = 0;

static volatile LONG g_reentrancy = 0;

static volatile PVOID g_last_net_client = NULL;

static uint32_t g_move_quad_off = 0;
static volatile float g_player_facing = 0.0f;
static volatile LONG g_facing_valid = 0;

static volatile float g_last_player_x = 0.f;
static volatile float g_last_player_y = 0.f;
static volatile float g_last_player_z = 0.f;
static volatile LONG g_last_player_valid = 0;

static uint8_t g_move_template[64];
static volatile LONG g_move_template_len = 0;
static volatile LONG g_move_counter = 0;

static volatile LONG g_mover_guid = 0;
static volatile LONG g_sniff_writes = 0;

/* Last loot-family packet (CMSG 0x15D-0x15F / SMSG 0x160-0x166 + interact).
 * Recv/send threads write; Lua reads. Interlocked so Hunt can wait for a
 * gen bump as a loot "receipt" without racing the sniff path. */
static volatile LONG g_loot_pkt_op = 0;
static volatile LONG g_loot_pkt_dir = 0;
static volatile LONG g_loot_pkt_len = 0;
static volatile LONG g_loot_pkt_gen = 0;
static volatile LONG g_loot_pkt_tick = 0;
static volatile LONG g_loot_pkt_guid_lo = 0;
static volatile LONG g_loot_pkt_guid_hi = 0;

enum { kMoveOpStop = 0, kMoveOpForward = 1, kMoveOpBackward = 2, kMoveOpJump = 3 };
static volatile LONG g_move_op = kMoveOpStop;
static volatile DWORD g_move_until = 0;
static volatile float g_move_target_x = 0.f;
static volatile float g_move_target_y = 0.f;
static volatile float g_move_target_z = 0.f;
static volatile LONG g_move_tick = 0;
enum { kMoveStepMs = 120u };
#define RUN_SPEED_YPS 7.0f

static volatile LONG g_tp_lock = 0;
static float g_tp_lock_x = 0.f, g_tp_lock_y = 0.f, g_tp_lock_z = 0.f, g_tp_lock_o = 0.f;
static volatile DWORD g_tp_lock_until = 0;
static float g_tp_lock_radius = 28.f;
static volatile LONG g_tp_lock_rewrites = 0;

static volatile LONG g_tp_lock_default_ms = 100;

/* After a hop the real client still emits MSG_MOVE_FALL_LAND (0xC9).
 * Server HandleFall uses the opcode + last-Z vs packet-Z — not the extra
 * fall bytes. Truncating the payload does not save you. Rewrite to heartbeat. */
enum { kAntiFallMs = 8000u };
static volatile DWORD g_antifall_until = 0;

static void ArmAntiFall(void)
{
    InterlockedExchange((volatile LONG*)&g_antifall_until,
        (LONG)(GetTickCount() + kAntiFallMs));
}

static int AntiFallActive(void)
{
    DWORD until = (DWORD)InterlockedCompareExchange(
        (volatile LONG*)&g_antifall_until, 0, 0);
    DWORD now;
    if (!until)
        return 0;
    now = GetTickCount();
    if (now > until) {
        InterlockedExchange((volatile LONG*)&g_antifall_until, 0);
        return 0;
    }
    return 1;
}

__declspec(dllexport) volatile MovementConfig g_config = {
    MOVE_CONFIG_MAGIC, 0,
    0.f, 0.f, 0.f, 0.f, 0.f, 0.f,
    kDefaultMoveOpcode, 0, 0, 0,
    0,
    0,
    kMapIdUnknown,
    0,
    0,
    1,
    1.0f,
    0,
    0
};
static volatile LONG g_fly_airborne = 0;

static float EffectiveRunSpeed(void)
{
    float s = g_config.speed_scale;
    if (s < kSpeedScaleMin) s = kSpeedScaleMin;
    if (s > kSpeedScaleMax) s = kSpeedScaleMax;
    return RUN_SPEED_YPS * s;
}

enum {
    kAscMoveSize = 38u,
    kAscOffGuid = 4u,
    kAscOffFlags = 8u,
    kAscOffFlags2 = 12u,
    kAscOffTime = 14u,
    kAscOffX = 18u,
    kAscOffY = 22u,
    kAscOffZ = 26u,
    kAscOffO = 30u,
    kAscOffCounter = 34u
};

static HHOOK g_msg_hook = NULL;
static HHOOK g_cwp_hook = NULL;
static DWORD g_ui_tid = 0;
static HWND g_hwnd = NULL;
static volatile LONG g_stop = 0;
static HANDLE g_nudge_thread = NULL;

#define WM_ASC_INJECT (WM_APP + 0x61)

enum {
    kRvaLastHardwareAction = 0x7499A4u,
    kRvaPerfCounter        = 0x8D76ACu,
    kRvaTimeStamp          = 0x71D618u,
    kRvaAfkIdleSub         = 0x12B251u,
    /* Character-create client unlocks (Ascension.exe lab build). */
    kRvaValidateName          = 0x2B0F90u, /* mov eax,0x57; ret */
    kRvaIsRaceClassRestricted = 0xDFA70u,  /* fldz; ret → not restricted */
    kRvaIsRaceClassValid      = 0xE0D00u,  /* mov eax,1; ret */
    kRvaRaceClassAllowed      = 0xDFFE0u,  /* mov al,1; ret — ChrRaces×Classes table */
    kRvaCreateCharAppearLoad  = 0xE03EBu,  /* mov eax,[gCharCreateObj] — null-crash site */
    kRvaCharCreateObjPtr      = 0x76B1A0u, /* VA 0xB6B1A0 → appearance object* */
    /* VA 0xB6B208 — per-race class bitmasks. Stock clears 10 dwords
     * (B6B208..B6B22C). Class UI vector lives at VA 0xB6B238 — NEVER write past +0x2F. */
    kRvaRaceClassMasks        = 0x76B208u,
    kCcRaceClassMaskDwords    = 10u,
    kCcRaceClassMaskBytes     = 40u,
    kRvaClientServicesPtr     = 0x879CF4u, /* VA 0xC79CF4 — ClientConnection* */
    kOffRealmExpansion        = 0x2F2Du,   /* byte vs ChrClasses.RequiredExpansion */
    kRvaSelectedSex           = 0x6C4220u, /* VA 0xAC4220 — selected sex dword */
    kRvaSetSelectedClassOob   = 0xE1B3Fu,  /* xor eax,eax; mov eax,[eax] crash */
    kRvaAvailClassExpCheck    = 0xE0890u,  /* cmp eax,[esi+0x2C]; setge cl */
    kRvaClassesForRaceExpChk  = 0xE19F9u,  /* cmp edx,[esi+0x2C]; setge al */
    kAntiAfkDefaultMs      = 15000u,
    kAntiAfkMinMs          = 1000u,
    kAntiAfkMaxMs          = 60000u
};
static volatile LONG g_anti_afk_enabled = 1;
static volatile DWORD g_anti_afk_interval_ms = kAntiAfkDefaultMs;
static volatile DWORD g_anti_afk_last_pulse = 0;
static volatile LONG g_anti_afk_pulses = 0;
static volatile LONG g_anti_afk_patched = 0;
static uint8_t g_afk_sub_saved[6];
static int g_afk_sub_have_saved = 0;
static volatile LONG g_charcreate_unlock = 0;
static uint8_t g_validate_name_saved[8];
static int g_validate_name_have = 0;
static uint8_t g_raceclass_saved[8];
static int g_raceclass_have = 0;
static uint8_t g_raceclass_valid_saved[8];
static int g_raceclass_valid_have = 0;
static uint8_t g_raceclass_allowed_saved[8];
static int g_raceclass_allowed_have = 0;
static uint8_t g_cc_masks_saved[40];
static int g_cc_masks_have = 0;
static int g_cc_appear_have = 0;
/* Fallback appearance blob when CharCreate object* is NULL (prevents [NULL+0x1C] crash). */
static uint8_t g_cc_dummy_appear[0x200]; /* larger sticky stand-in object */
static uint8_t* g_cc_appear_stub = NULL; /* VirtualAlloc RX get_obj stub */
static int g_cc_appear_stub_ready = 0;
static int g_cc_sticky_installed = 0;
enum { kCcNullSiteMax = 20 };
static struct {
    uint32_t rva;
    uint8_t kind; /* 0=A1 eax, 1=8B0D ecx, 2=8B15 edx, 3=8B1D ebx */
    uint8_t saved[6];
    int have;
} g_cc_null_sites[kCcNullSiteMax];
static int g_cc_null_site_count = 0;
static uint8_t g_cc_realm_exp_saved = 0;
static int g_cc_realm_exp_have = 0;
static uint8_t* g_cc_realm_exp_ptr = NULL;
static uint8_t g_cc_expcheck_saved[8];
static int g_cc_expcheck_have = 0;
static uint8_t g_cc_expcheck2_saved[8];
static int g_cc_expcheck2_have = 0;
static uint8_t g_cc_class_oob_saved[2];
static int g_cc_class_oob_have = 0;

enum { kLogMaxBytes = 2u * 1024u * 1024u, kLogCheckEvery = 64u };

static volatile LONG g_log_lines = 0;
static CRITICAL_SECTION g_log_cs;
static volatile LONG g_log_cs_ready = 0;

static void EnsureLogLock(void)
{
    if (InterlockedCompareExchange(&g_log_cs_ready, 1, 0) == 0)
        InitializeCriticalSection(&g_log_cs);
}

void ProxyLogLine(const char* msg)
{
    char path[MAX_PATH];
    FILE* f;
    char* slash;
    LONG count;
    const char* mode = "a";
    DWORD n = GetModuleFileNameA(g_self, path, MAX_PATH);
    if (!n || n >= MAX_PATH)
        return;
    slash = strrchr(path, '\\');
    if (!slash)
        return;
    lstrcpyA(slash + 1, "ExtProxy64.log");

    count = InterlockedIncrement(&g_log_lines);
    if ((count % (LONG)kLogCheckEvery) == 1) {
        WIN32_FILE_ATTRIBUTE_DATA fad;
        if (GetFileAttributesExA(path, GetFileExInfoStandard, &fad)
            && fad.nFileSizeHigh == 0 && fad.nFileSizeLow > kLogMaxBytes)
            mode = "w";
    }

    EnsureLogLock();
    EnterCriticalSection(&g_log_cs);
    f = fopen(path, mode);
    if (f) {
        if (mode[0] == 'w')
            fprintf(f, "%lu log truncated at %u bytes\n",
                (unsigned long)GetTickCount(), (unsigned)kLogMaxBytes);
        fprintf(f, "%lu %s\n", (unsigned long)GetTickCount(), msg ? msg : "");
        fflush(f);
        fclose(f);
    }
    LeaveCriticalSection(&g_log_cs);
}

static void LogLine(const char* msg)
{
    ProxyLogLine(msg);
}

static void tc0(void* fn, void* self)
{
    __asm__ __volatile__(
        "pushl %%ebx\n\t"
        "pushl %%esi\n\t"
        "pushl %%edi\n\t"
        "movl %[self], %%ecx\n\t"
        "call *%[fn]\n\t"
        "popl %%edi\n\t"
        "popl %%esi\n\t"
        "popl %%ebx\n\t"
        :
        : [self] "r"(self), [fn] "r"(fn)
        : "eax", "ecx", "edx", "memory");
}

static void tc_net_send(void* stub, void* netClient, void* packet)
{
    __asm__ __volatile__(
        "pushl %%ebx\n\t"
        "pushl %%esi\n\t"
        "pushl %%edi\n\t"
        "movl %[self], %%ecx\n\t"
        "pushl %[pkt]\n\t"
        "call *%[fn]\n\t"
        "popl %%edi\n\t"
        "popl %%esi\n\t"
        "popl %%ebx\n\t"
        :
        : [self] "r"(netClient), [pkt] "r"(packet), [fn] "r"(stub)
        : "eax", "ecx", "edx", "memory");
}

static void CallQueueTramp(CDataStore* packet)
{
    QueuePacketFn q = g_queue_tramp;
    if (!q)
        return;
    __asm__ __volatile__(
        "pushl %%ebx\n\t"
        "pushl %%esi\n\t"
        "pushl %%edi\n\t"
        "pushl %[pkt]\n\t"
        "call *%[fn]\n\t"
        "addl $4, %%esp\n\t"
        "popl %%edi\n\t"
        "popl %%esi\n\t"
        "popl %%ebx\n\t"
        :
        : [pkt] "r"(packet), [fn] "r"(q)
        : "eax", "ecx", "edx", "memory");
}

static void WakeUiForInjectAsync(void)
{
    HWND hw = g_hwnd;
    if (!hw)
        return;
    if (g_ui_tid && GetCurrentThreadId() == g_ui_tid) {
        HandleAscInject();
        return;
    }
    PostMessageW(hw, WM_ASC_INJECT, 0, 0);
    PostMessageW(hw, WM_NULL, 0, 0);
}

void ProxyWakeUiForInject(void)
{
    WakeUiForInjectAsync();
}

void ProxyConsumeLuaCast(void)
{
    ConsumePendingLuaCast();
}

static void WakeUiForInject(void)
{
    HWND hw = g_hwnd;
    DWORD_PTR done = 0;
    if (!hw) {
        LogLine("lua wake: no hwnd yet");
        return;
    }
    if (g_ui_tid && GetCurrentThreadId() == g_ui_tid) {
        HandleAscInject();
        return;
    }
    if (!SendMessageTimeoutW(hw, WM_ASC_INJECT, 0, 0, SMTO_ABORTIFHUNG | SMTO_NORMAL, 1000, &done)) {
        LogLine("lua wake: SendMessageTimeout failed — posting");
        WakeUiForInjectAsync();
    }
}

void ProxyWakeUiForInjectSync(void)
{
    WakeUiForInject();
}

static void ForceClearTaint(void)
{
    if (!g_ascension)
        return;
    *(uint32_t*)(g_ascension + kTaintPtrRva) = 0;
    *(uint32_t*)(g_ascension + kTaintLockRva) = 0;
}

static void MaybeClearTaint(void)
{
    if (!InterlockedCompareExchange(&g_always_unrestricted, 0, 0))
        return;
    ForceClearTaint();
    g_last_taint_clear = GetTickCount();
}

static void EnterUnrestricted(SecureCtx* ctx)
{
    uint32_t* taint;
    uint32_t* depth;
    uint32_t* lock;
    if (!g_ascension || !ctx)
        return;
    taint = (uint32_t*)(g_ascension + kTaintPtrRva);
    depth = (uint32_t*)(g_ascension + kTaintDepthRva);
    lock = (uint32_t*)(g_ascension + kTaintLockRva);
    ctx->taint = *taint;
    ctx->depth = *depth;
    ctx->lock = *lock;
    *taint = 0;
    *lock = 0;

}

static void LeaveUnrestricted(const SecureCtx* ctx)
{
    uint32_t* taint;
    uint32_t* lock;
    if (!g_ascension || !ctx)
        return;

    if (InterlockedCompareExchange(&g_always_unrestricted, 0, 0)) {
        ForceClearTaint();
        return;
    }
    taint = (uint32_t*)(g_ascension + kTaintPtrRva);
    lock = (uint32_t*)(g_ascension + kTaintLockRva);
    *taint = ctx->taint;
    *lock = ctx->lock;
}

static int LuaQueueEnqueue(const char* script, uint32_t len)
{
    LONG head, count;
    if (!script || !len)
        return 0;
    if (len >= kLuaScriptMax)
        len = kLuaScriptMax - 1;
    EnterCriticalSection(&g_cast_cs);
    count = g_lua_q_count;
    if (count >= kLuaQueueSlots) {
        LeaveCriticalSection(&g_cast_cs);
        LogLine("lua queue FULL — drop (caller must retry)");
        return 0;
    }
    head = g_lua_q_head % kLuaQueueSlots;
    memcpy(g_lua_queue[head], script, len);
    g_lua_queue[head][len] = 0;
    g_lua_q_head = (head + 1) % kLuaQueueSlots;
    InterlockedIncrement(&g_lua_q_count);
    InterlockedExchange(&g_fire_lua_cast, 1);
    LeaveCriticalSection(&g_cast_cs);
    return 1;
}

static int LuaQueueDequeue(char* out, size_t out_cap)
{
    LONG tail, count;
    size_t n;
    if (!out || out_cap < 2)
        return 0;
    EnterCriticalSection(&g_cast_cs);
    count = g_lua_q_count;
    if (count <= 0) {
        InterlockedExchange(&g_fire_lua_cast, 0);
        LeaveCriticalSection(&g_cast_cs);
        out[0] = 0;
        return 0;
    }
    tail = g_lua_q_tail % kLuaQueueSlots;
    n = strlen(g_lua_queue[tail]);
    if (n >= out_cap)
        n = out_cap - 1;
    memcpy(out, g_lua_queue[tail], n);
    out[n] = 0;
    g_lua_queue[tail][0] = 0;
    g_lua_q_tail = (tail + 1) % kLuaQueueSlots;
    if (InterlockedDecrement(&g_lua_q_count) <= 0)
        InterlockedExchange(&g_fire_lua_cast, 0);
    LeaveCriticalSection(&g_cast_cs);
    return 1;
}

static int PatchShortJeToJmp(uint32_t rva, const char* tag)
{
    uint8_t* p;
    DWORD old = 0;
    uint8_t before;
    char msg[96];
    if (!g_ascension || !rva)
        return 0;
    p = (uint8_t*)(g_ascension + rva);
    before = *p;
    if (before == 0xEBu) {
        _snprintf(msg, sizeof(msg), "unrestrict %s: already JMP @ RVA 0x%X", tag, rva);
        LogLine(msg);
        return 1;
    }
    if (before != 0x74u && before != 0x75u) {
        _snprintf(msg, sizeof(msg), "unrestrict %s: unexpected opcode 0x%02X @ RVA 0x%X", tag, before, rva);
        LogLine(msg);
        return 0;
    }
    if (!VirtualProtect(p, 1, PAGE_EXECUTE_READWRITE, &old)) {
        _snprintf(msg, sizeof(msg), "unrestrict %s: VirtualProtect fail", tag);
        LogLine(msg);
        return 0;
    }
    *p = 0xEBu;
    VirtualProtect(p, 1, old, &old);
    FlushInstructionCache(GetCurrentProcess(), p, 1);
    _snprintf(msg, sizeof(msg), "unrestrict %s: 0x%02X→EB @ RVA 0x%X", tag, before, rva);
    LogLine(msg);
    return 1;
}

static int InstallUnrestrictProtectedApis(void)
{
    int ok = 1;
    if (!PatchShortJeToJmp(kProtectedActionGateRva, "action-gate"))
        ok = 0;
    if (!PatchShortJeToJmp(kCastTaintGateRva, "cast-gate"))
        ok = 0;
    if (ok)
        LogLine("unrestrict: JE→JMP gates OK + stay-unlocked (no taint restore)");
    else
        LogLine("unrestrict: partial — ForceClearTaint still active");
    ForceClearTaint();
    return ok;
}

static void RunFrameScriptExecute(const char* script)
{
    typedef void(__cdecl* FrameScriptExecuteFn)(const char* body, const char* name, void* ctx);
    FrameScriptExecuteFn fn;
    SecureCtx ctx;
    char msg[160];
    if (!g_ascension || !script || !script[0])
        return;
    /* Same null-L crash as RegisterFunction: mov eax,[esi+0x14] @ RVA 0x44E408. */
    if (!ProxyLuaState())
        return;
    fn = (FrameScriptExecuteFn)(g_ascension + kFrameScriptExecuteRva);
    EnterUnrestricted(&ctx);

    fn(script, "FrameXML", NULL);
    LeaveUnrestricted(&ctx);
    ForceClearTaint();
    /* Mute high-frequency / diagnostic noise that flooded ExtProxy64.log and woke UI. */
    if (strncmp(script, "if GmUnlock_UpdatePositions", 27) == 0)
        return;
    if (strncmp(script, "local BAD={ADDON_ACTION_FORBIDDEN", 33) == 0)
        return;
    if (strncmp(script, "local r=40 if type(GmNearestLootable)", 37) == 0)
        return;
    _snprintf(msg, sizeof(msg), "FrameScript_Execute(serial): %.100s", script);
    LogLine(msg);
}

static void ConsumePendingLuaCast(void)
{
    static char script[kLuaScriptMax];
    SecureCtx ctx;
    int ran = 0;
    while (LuaQueueDequeue(script, sizeof(script))) {
        EnterUnrestricted(&ctx);
        RunFrameScriptExecute(script);
        LeaveUnrestricted(&ctx);
        ran = 1;
    }
    /* Quiet — drained every UI wake; logging it flooded ExtProxy64.log at login. */
    (void)ran;
}

static void SeedPopupSuppress(void)
{
    static const char kSuppress[] =
        "local BAD={ADDON_ACTION_FORBIDDEN=1,ADDON_ACTION_BLOCKED=1,MACRO_ACTION_FORBIDDEN=1,MACRO_ACTION_BLOCKED=1}"
        "local function isTaint(m) if type(m)~='string' then return false end "
        "m=m:lower() return m:find('tainted',1,true) or m:find('secure function',1,true) "
        "or m:find('protected function',1,true) or m:find('addon_action',1,true) "
        "or m:find('macro_action',1,true) or m:find('blocked from using',1,true) "
        "or m:find('blocked from calling',1,true) or m:find('has been blocked',1,true) "
        "or m:find('unknown()',1,true) "
        "or m:find(\"tainted the call\",1,true) "
        "or (m:find('realm:',1,true) and m:find('locale:',1,true)) end "
        "if DEFAULT_CHAT_FRAME and DEFAULT_CHAT_FRAME.AddMessage and not GmChatTaintHook then "
        "local oa=DEFAULT_CHAT_FRAME.AddMessage "
        "DEFAULT_CHAT_FRAME.AddMessage=function(self,m,...) if isTaint(tostring(m or '')) then return end "
        "return oa(self,m,...) end GmChatTaintHook=true end "
        "if UIParent and UIParent.UnregisterEvent then "
        "UIParent:UnregisterEvent('ADDON_ACTION_FORBIDDEN') UIParent:UnregisterEvent('ADDON_ACTION_BLOCKED') "
        "UIParent:UnregisterEvent('MACRO_ACTION_FORBIDDEN') UIParent:UnregisterEvent('MACRO_ACTION_BLOCKED') end "
        "if type(StaticPopupDialogs)=='table' then for k in pairs(BAD) do local d=StaticPopupDialogs[k] "
        "if d then d.text='' d.OnShow=function(s) if s and s.Hide then s:Hide() end end "
        "d.timeout=0.01 d.hideOnEscape=1 end end end "
        "if type(StaticPopup_Show)=='function' then local o=GmPopupOrig or StaticPopup_Show "
        "GmPopupOrig=o StaticPopup_Show=function(w,...) if BAD[w] then return end return o(w,...) end end "
        "if type(seterrorhandler)=='function' and type(geterrorhandler)=='function' then "
        "if not GmTaintEH then "
        "function GmTaintEH(msg) if isTaint(msg) then return end "
        "local p=GmTaintPrev if type(p)=='function' and p~=GmTaintEH then "
        "local ok,err=pcall(p,msg) if not ok and DEFAULT_CHAT_FRAME then "
        "DEFAULT_CHAT_FRAME:AddMessage('|cffff6666[err]|r '..tostring(err)) end end end end "
        "local cur=geterrorhandler() if cur~=GmTaintEH then GmTaintPrev=cur seterrorhandler(GmTaintEH) end end "
        "local function nukeErrFrame(f) if not f then return end "
        "if f.Hide then f:Hide() end "
        "if f.SetScript then pcall(function() f:SetScript('OnShow',function(self) "
        "local t=(self.text and self.text.GetText and self.text:GetText()) "
        "or (self.ScrollFrame and self.ScrollFrame.Text and self.ScrollFrame.Text.GetText and self.ScrollFrame.Text:GetText()) "
        "or (ScriptErrors_Message and ScriptErrors_Message.GetText and ScriptErrors_Message:GetText()) "
        "if isTaint(t) then self:Hide() end end) end) end end "
        "nukeErrFrame(ScriptErrors) nukeErrFrame(ScriptErrorsFrame) nukeErrFrame(_G.ScriptErrorsFrame) "
        "if ScriptErrors_Message and ScriptErrors_Message.SetText then "
        "local os=ScriptErrors_Message.SetText ScriptErrors_Message.SetText=function(self,t,...) "
        "if isTaint(t) then if ScriptErrorsFrame and ScriptErrorsFrame.Hide then ScriptErrorsFrame:Hide() end "
        "if ScriptErrors and ScriptErrors.Hide then ScriptErrors:Hide() end return end return os(self,t,...) end end "
        "if UIErrorsFrame and UIErrorsFrame.AddMessage and not GmUIErrHooked then "
        "local oa=UIErrorsFrame.AddMessage UIErrorsFrame.AddMessage=function(self,m,...) "
        "if isTaint(m) then return end return oa(self,m,...) end GmUIErrHooked=true end "
        "if not GmPopupWatch and type(CreateFrame)=='function' then "
        "GmPopupWatch=CreateFrame('Frame') GmPopupWatch.t=0 "
        "GmPopupWatch:SetScript('OnUpdate',function(self,dt) "
        "self.t=(self.t or 0)+(dt or 0) if self.t < 0.50 then return end self.t=0 "
        "for i=1,6 do local f=_G['StaticPopup'..i] if f and f.IsShown and f:IsShown() and BAD[f.which] then f:Hide() end end "
        "local se=ScriptErrorsFrame or ScriptErrors if se and se.IsShown and se:IsShown() then "
        "local t=(ScriptErrors_Message and ScriptErrors_Message.GetText and ScriptErrors_Message:GetText()) or '' "
        "if isTaint(t) then se:Hide() end end "
        "if type(message)=='function' and not GmMsgHooked then local om=message "
        "message=function(m,...) if isTaint(tostring(m or '')) then return end return om(m,...) end GmMsgHooked=true end "
        "if type(BugGrabber)=='table' and not BugGrabber._gmHooked then "
        "local function wrap(n) local o=BugGrabber[n] if type(o)~='function' then return end "
        "BugGrabber[n]=function(self,err,...) local m=err if type(err)=='table' then "
        "m=err.message or err.msg or err.error or err[1] end "
        "if isTaint(tostring(m or '')) then return end return o(self,err,...) end end "
        "wrap('GrabError') wrap('StoreError') wrap('AddError') BugGrabber._gmHooked=true end "
        "end) end "
        "for i=1,6 do local f=_G['StaticPopup'..i] if f and f.IsShown and f:IsShown() and BAD[f.which] then f:Hide() end end";
    if (!ProxyLuaState())
        return;
    if (InterlockedCompareExchange(&g_popup_seeded, 1, 0) != 0)
        return;
    g_popup_seed_tick = GetTickCount();
    if (!LuaQueueEnqueue(kSuppress, (uint32_t)strlen(kSuppress))) {
        InterlockedExchange(&g_popup_seeded, 0);
        return;
    }
    WakeUiForInjectAsync();
}

int ProxyRequestRunLua(const char* script, uint32_t len)
{
    char msg[160];
    if (!script || !len)
        return 0;
    if (!LuaQueueEnqueue(script, len))
        return 0;
    WakeUiForInject();
    _snprintf(msg, sizeof(msg), "run-lua queued (%u bytes) q=%ld: %.80s",
        len, (long)g_lua_q_count, script);
    LogLine(msg);
    return 1;
}

int ProxyRunSelfTest(char* out, uint32_t out_cap)
{
    char script[kLuaScriptMax];
    char msg[192];
    DWORD pid = GetCurrentProcessId();

    if (!out || out_cap < 32)
        return 0;
    _snprintf(script, sizeof(script),
        "if DEFAULT_CHAT_FRAME then DEFAULT_CHAT_FRAME:AddMessage('|cff00ff00[ExtProxy] LUA OK pid=%lu|r') end; "
        "print('[ExtProxy] LUA OK pid=%lu')",
        (unsigned long)pid, (unsigned long)pid);
    ProxyRequestRunLua(script, (uint32_t)strlen(script));

    _snprintf(out, out_cap,
        "pid=%lu send_hooks=%ld recv_sniffs=%ld send_hooked=%d queue_hooked=%d "
        "in_hooked=%d sniff_on=%d objmgr_posoff=0x%X hwnd=%p ctm_ready=%d ctm_moving=%d",
        (unsigned long)pid,
        (long)g_send_hook_calls, (long)g_sniff_writes,
        g_send_hooked, g_hooked, g_in_hooked,
        PktIpcSniffEnabled(), (unsigned)ObjMgrPositionOffset(), (void*)g_hwnd,
        (g_move_template_len > 0) ? 1 : 0,
        (InterlockedCompareExchange(&g_move_op, 0, 0) != kMoveOpStop) ? 1 : 0);
    _snprintf(msg, sizeof(msg), "self-test: %s", out);
    LogLine(msg);
    return 1;
}

uint32_t ProxyGetMapObjects(char* out, uint32_t out_cap)
{
    return ObjMgrSnapshot(out, out_cap);
}

int ProxyNavHeight(uint32_t map, float x, float y, float z_hint, float* out_z)
{
    return NavHeightAt(map, x, y, z_hint, out_z);
}

enum {
    kScriptTargetUnitRva = 0x125A30u,
    kScriptInteractUnitRva = 0x127F00u,
    kLuaStatePtrRva = 0x93F78Cu,
    kCurrentTargetGuidRva = 0x7D07B0u,
    kMouseoverGuidRva = 0x7D07A0u,
    kInteractTargetGuidRva = 0x7D07A8u,
    kLastEnemyGuidRva = 0x7D07C0u,

    kGameUiSetTargetRva = 0x124BF0u,


    kLootSourceGuidRva = 0x7FA8D8u,



    kGameUiInteractGuidRva = 0x1277B0u,



    kGameUiTargetNearestRva = 0x124FC0u,



    kGameUiSetTargetValidatedRva = 0x1259E0u,



    kLastTargetGuidRva = 0x7D07B8u,



    kHwEventFlagsRva = 0x7EAF44u,
    kHwEventCtxArgRva = 0x7EAF48u,
    kHwEventCtxRva = 0x7EAF4Cu,
    kHwEventSecureScopeRva = 0x164DB0u,
    kHwBitTargetNearest = 0x20u,


    kNearestIndexRva = 0x7D08C8u,
    kNearestCycleMsRva = 0x7D08CCu,
    kNearestBuildMsRva = 0x7D08D0u,
    kNearestModeRva = 0x7D08D4u,
    kNearestCountRva = 0x7D0C00u,
    kNearestArrayRva = 0x7D0C04u,
};

enum {
    kOpGameObjUse = 0x0B1u,
    kOpAutostoreLootItem = 0x108u,
    kOpSetSelection = 0x13Du,
    kOpLoot = 0x15Du,
    kOpLootMoney = 0x15Eu,
    kOpLootRelease = 0x15Fu,
    kOpGossipHello = 0x17Bu,

    kOpGroupAccept = 0x072u,
    kOpGroupDisband = 0x07Bu,
    kOpResetInstances = 0x31Du,

    kOpBattlemasterJoin = 0x2EEu,
    kOpBattlemasterJoinArena = 0x358u,
    kOpBattlefieldPort = 0x2D5u,
    kOpLeaveBattlefield = 0x2E1u,
    kOpLfgJoin = 0x35Cu,
    kOpLfgLeave = 0x35Du,
    kOpLfgProposalResult = 0x362u,
    kOpRepairItem = 0x2A8u,
    kOpBfMgrEntryInviteRsp = 0x4DFu,
    kOpBfMgrQueueInviteRsp = 0x4E2u,
    kBgTypeRandom = 32u,
    kLfgDungeonRandomClassic = 258u,
    kLfgDungeonRandomBc = 259u,
    kLfgDungeonRandomWotlk = 261u,
};
typedef void(__cdecl* RegisterFunctionFn)(const char* name, void* fn);
typedef const char*(__cdecl* LuaToLStringFn)(void* L, int idx, unsigned* len);
typedef double(__cdecl* LuaToNumberFn)(void* L, int idx);
typedef void(__cdecl* LuaPushNumberFn)(void* L, double n);
typedef void(__cdecl* LuaPushStringFn)(void* L, const char* s);
typedef int(__cdecl* LuaCFunction)(void* L);

static DWORD g_lua_api_tick = 0;
/* Re-register often: login-screen L is wiped on world enter /reload; 5min left natives missing. */
enum { kLuaApiReseedMs = 2000u };
static volatile uint32_t g_last_map = 0;
static int g_lua_was_in_world = 0;
static int g_login_inflight = 0;

static void NavMapResolutionLog(uint32_t caller_map, uint32_t resolved, float x, float y, float z)
{
    static uint32_t logged[16];
    static uint32_t pairs = 0;
    uint32_t key = (caller_map & 0xFFFFu) | ((resolved & 0xFFFFu) << 16);
    uint32_t i;
    char msg[128];
    for (i = 0; i < pairs && i < 16; i++)
        if (logged[i] == key) return;
    if (pairs < 16) logged[pairs++] = key;
    _snprintf(msg, sizeof(msg),
        "navmap: caller map=%u resolved to continent %u at (%.0f,%.0f,%.0f) (zone->continent guess)",
        (unsigned)caller_map, (unsigned)resolved, x, y, z);
    LogLine(msg);
}

static uint32_t ResolveNavMap(uint32_t map, float x, float y, float z)
{
    float ztmp = 0.f;
    uint32_t guessed;


    if (NavIsContinentMap(map)) {
        if (NavHeightAt(map, x, y, z, &ztmp)) {
            InterlockedExchange((volatile LONG*)&g_last_map, (LONG)map);
            return map;
        }

        if (map != 0u)
            return map;
    } else if (map != 0u && map != 0xFFFFFFFFu) {

        if (NavHeightAt(map, x, y, z, &ztmp)) {
            InterlockedExchange((volatile LONG*)&g_last_map, (LONG)map);
            return map;
        }
    }


    guessed = NavGuessMapInclusive(x, y, z);
    if (guessed != 0xFFFFFFFFu) {
        if (map != guessed)
            NavMapResolutionLog(map, guessed, x, y, z);
        InterlockedExchange((volatile LONG*)&g_last_map, (LONG)guessed);
        return guessed;
    }


    if (g_last_map != 0u && NavHeightAt(g_last_map, x, y, z, &ztmp))
        return g_last_map;
    return map;
}

static void RefreshLastMapFromPlayer(void)
{
    void* self = ObjMgrPlayerObject();
    float x = 0, y = 0, z = 0, ztmp = 0.f;
    uint32_t guessed;
    if (!self || !ObjMgrPosition(self, &x, &y, &z, NULL))
        return;
    /* Keep a working instance/BG map — do not clobber with continent collision. */
    if (g_last_map != 0u && !NavIsContinentMap(g_last_map) &&
        NavHeightAt(g_last_map, x, y, z, &ztmp))
        return;
    guessed = NavGuessMapInclusive(x, y, z);
    if (guessed != 0xFFFFFFFFu)
        InterlockedExchange((volatile LONG*)&g_last_map, (LONG)guessed);
}

static uint32_t LearnNavMapForTeleport(float x, float y, float z)
{
    float ztmp = 0.f;
    uint32_t map;

    /* Prefer sticky instance/BG map when its mesh still hits. */
    if (g_last_map != 0u && !NavIsContinentMap(g_last_map) &&
        NavHeightAt(g_last_map, x, y, z, &ztmp))
        return g_last_map;

    map = NavGuessMapInclusive(x, y, z);
    if (map != 0xFFFFFFFFu) {
        InterlockedExchange((volatile LONG*)&g_last_map, (LONG)map);
        return map;
    }

    /* Continent sticky only if Inclusive found nothing. */
    if (g_last_map != 0u && NavHeightAt(g_last_map, x, y, z, &ztmp))
        return g_last_map;
    return ResolveNavMap(g_last_map, x, y, z);
}

static const char* LuaArgStr(void* L, int idx)
{
    LuaToLStringFn f;
    unsigned len = 0;
    if (!g_ascension)
        return NULL;
    f = (LuaToLStringFn)(g_ascension + kLuaToLStringRva);
    return f(L, idx, &len);
}

static double LuaArgNum(void* L, int idx)
{
    LuaToNumberFn f;
    if (!g_ascension)
        return 0.0;
    f = (LuaToNumberFn)(g_ascension + kLuaToNumberRva);
    return f(L, idx);
}

static void LuaPushNum(void* L, double v)
{
    LuaPushNumberFn f;
    if (!g_ascension)
        return;
    f = (LuaPushNumberFn)(g_ascension + kLuaPushNumberRva);
    f(L, v);
}

static void LuaPushStr(void* L, const char* s)
{
    LuaPushStringFn f;
    if (!g_ascension)
        return;
    f = (LuaPushStringFn)(g_ascension + kLuaPushStringRva);
    f(L, s);
}

static void SharedGuidHex(char* out, size_t cap, uint64_t g)
{
    _snprintf(out, (int)cap, "%016llX", (unsigned long long)g);
}

static int PushSharedViewObject(void* L, const SharedViewObject* o)
{
    char guid[24];
    if (!o) return 0;
    SharedGuidHex(guid, sizeof(guid), o->guid);
    LuaPushStr(L, guid);
    LuaPushNum(L, (double)o->entry);
    LuaPushNum(L, (double)o->x);
    LuaPushNum(L, (double)o->y);
    LuaPushNum(L, (double)o->z);
    LuaPushNum(L, (double)o->facing);
    LuaPushNum(L, (double)o->health);
    LuaPushNum(L, (double)o->max_health);
    LuaPushNum(L, (double)o->level);
    LuaPushNum(L, (double)o->faction);
    LuaPushNum(L, (double)o->type_mask);
    LuaPushNum(L, (double)o->src_instance);
    return 12;
}

static int __cdecl GmSharedCount_Lua(void* L)
{
    uint32_t n = 0;
    PktIpcSharedObjects(&n);
    LuaPushNum(L, (double)n);
    return 1;
}

static int __cdecl GmSharedObject_Lua(void* L)
{
    uint32_t n = 0;
    const SharedViewObject* arr = PktIpcSharedObjects(&n);
    int i = (int)LuaArgNum(L, 1);
    if (!arr || i < 1 || (uint32_t)i > n)
        return 0;
    return PushSharedViewObject(L, &arr[i - 1]);
}

static int __cdecl GmSharedObjects_Lua(void* L)
{
    return GmSharedCount_Lua(L);
}

static int __cdecl GmSharedPlayers_Lua(void* L)
{
    uint32_t n = 0, i, out = 0;
    const SharedViewObject* arr = PktIpcSharedObjects(&n);
    if (!arr) { LuaPushNum(L, 0); return 1; }
    for (i = 0; i < n; i++) {
        if (arr[i].type_mask & 0x10u)
            out++;
    }
    LuaPushNum(L, (double)out);
    return 1;
}

static int __cdecl GmSharedPlayer_Lua(void* L)
{
    uint32_t n = 0, i, seen = 0;
    const SharedViewObject* arr = PktIpcSharedObjects(&n);
    int want = (int)LuaArgNum(L, 1);
    if (!arr || want < 1) return 0;
    for (i = 0; i < n; i++) {
        if (!(arr[i].type_mask & 0x10u))
            continue;
        seen++;
        if ((int)seen == want)
            return PushSharedViewObject(L, &arr[i]);
    }
    return 0;
}

static int __cdecl GmInstanceInfo_Lua(void* L)
{
    LuaPushNum(L, (double)PktIpcThisInstance());
    LuaPushNum(L, (double)PktIpcTotalInstances());
    LuaPushNum(L, (double)PktIpcOwnerPid());
    return 3;
}

static int __cdecl GmGetInstanceCount_Lua(void* L)
{
    uint32_t want = (uint32_t)LuaArgNum(L, 1);
    uint32_t n = 0, i, out = 0;
    const SharedViewObject* arr = PktIpcSharedObjects(&n);
    if (!arr) { LuaPushNum(L, 0); return 1; }
    for (i = 0; i < n; i++) {
        if (arr[i].src_instance == want)
            out++;
    }
    LuaPushNum(L, (double)out);
    return 1;
}

static int __cdecl GmGetInstanceObject_Lua(void* L)
{
    uint32_t want = (uint32_t)LuaArgNum(L, 1);
    int idx = (int)LuaArgNum(L, 2);
    uint32_t n = 0, i, seen = 0;
    const SharedViewObject* arr = PktIpcSharedObjects(&n);
    if (!arr || idx < 1) return 0;
    for (i = 0; i < n; i++) {
        if (arr[i].src_instance != want)
            continue;
        seen++;
        if ((int)seen == idx)
            return PushSharedViewObject(L, &arr[i]);
    }
    return 0;
}

static int __cdecl GmGetInstance_Lua(void* L)
{
    return GmGetInstanceCount_Lua(L);
}

static int __cdecl GmPublishName_Lua(void* L)
{
    const char* name = LuaArgStr(L, 1);
    InstBusPublishName(name);
    LuaPushNum(L, name && name[0] ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmResolveInstance_Lua(void* L)
{
    const char* key = LuaArgStr(L, 1);
    uint32_t id = 0, pid = 0;
    char nm[INST_BUS_NAME_LEN];
    nm[0] = 0;
    if (!key || !key[0] || !InstBusResolve(key, &id, &pid, nm, (int)sizeof(nm)))
        return 0;
    LuaPushNum(L, (double)id);
    LuaPushNum(L, (double)pid);
    LuaPushStr(L, nm);
    return 3;
}

static int __cdecl GmListInstances_Lua(void* L)
{
    InstBusDir dir[INST_BUS_MAX_INST];
    int n = InstBusCopyDir(dir, (int)INST_BUS_MAX_INST);
    int idx = (int)LuaArgNum(L, 1);
    if (idx < 1) {
        LuaPushNum(L, (double)n);
        return 1;
    }
    if (idx > n)
        return 0;
    LuaPushNum(L, (double)dir[idx - 1].instance_id);
    LuaPushNum(L, (double)dir[idx - 1].pid);
    LuaPushStr(L, dir[idx - 1].name);
    return 3;
}

static void FillInstArgFromLua(void* L, int idx, InstBusArg* a)
{
    const char* s;
    memset(a, 0, sizeof(*a));
    s = LuaArgStr(L, idx);
    /* Prefer number when tolstring yields a numeric-looking value that tonumber accepts.
       WoW FrameScript: strings and numbers coexist; try number first via LuaArgNum always,
       and treat empty/null string as number path. */
    if (s && s[0]) {
        /* If caller passed an explicit string that is not a pure number, keep string. */
        char* endp = NULL;
        double v = strtod(s, &endp);
        if (endp && endp != s && *endp == 0) {
            a->kind = kInstArgNumber;
            a->num = v;
        } else {
            a->kind = kInstArgString;
            strncpy(a->str, s, INST_BUS_STR_LEN - 1);
        }
    } else {
        a->kind = kInstArgNumber;
        a->num = LuaArgNum(L, idx);
    }
}

static int __cdecl GmRemoteCall_Lua(void* L)
{
    /* GmRemoteCall(targetId|name, "GmLoS", argc, ...) -> peer returns */
    const char* a1s = LuaArgStr(L, 1);
    uint32_t target = 0, pid = 0;
    const char* fn;
    InstBusArg args[INST_BUS_MAX_ARGS];
    InstBusArg rets[INST_BUS_MAX_RETS];
    uint32_t argc = 0, retc = 0, i;
    char err[INST_BUS_STR_LEN];
    char nm[INST_BUS_NAME_LEN];
    double a1n;
    int pushed;
    int argBase = 4;

    err[0] = 0;
    nm[0] = 0;
    a1n = LuaArgNum(L, 1);
    if (a1s && a1s[0] && !(a1n >= 1.0 && a1n <= 16.0 && floor(a1n) == a1n)) {
        if (!InstBusResolve(a1s, &target, &pid, nm, (int)sizeof(nm)))
            return 0;
    } else {
        target = (uint32_t)a1n;
        if (target == 0)
            return 0;
    }

    fn = LuaArgStr(L, 2);
    if (!fn || !fn[0])
        return 0;

    argc = (uint32_t)LuaArgNum(L, 3);
    if (argc > INST_BUS_MAX_ARGS)
        argc = INST_BUS_MAX_ARGS;

    /* Back-compat: if argc looks like a real first arg (fn takes numbers) and
       arg4 is unused, treat arg3 as first vararg (argc omitted). Detect:
       LuaArgStr(4) null and common pattern from old callers — addon always sends argc. */
    for (i = 0; i < argc; ++i)
        FillInstArgFromLua(L, argBase + (int)i, &args[i]);

    if (!InstBusRemoteCall(target, fn, args, argc, rets, &retc, err, (int)sizeof(err), 2000u)) {
        if (err[0])
            LuaPushStr(L, err);
        else
            LuaPushStr(L, "rpc failed");
        return 1;
    }

    pushed = 0;
    for (i = 0; i < retc && i < INST_BUS_MAX_RETS; ++i) {
        if (rets[i].kind == kInstArgString)
            LuaPushStr(L, rets[i].str);
        else
            LuaPushNum(L, rets[i].num);
        pushed++;
    }
    return pushed;
}

static int __cdecl GmRpcCapture_Lua(void* L)
{
    InstBusArg rets[INST_BUS_MAX_RETS];
    uint32_t retc = (uint32_t)LuaArgNum(L, 1);
    uint32_t i;
    if (retc > INST_BUS_MAX_RETS)
        retc = INST_BUS_MAX_RETS;
    for (i = 0; i < retc; ++i)
        FillInstArgFromLua(L, (int)(i + 2), &rets[i]);
    InstBusCaptureReturns(rets, retc);
    return 0;
}

static int __cdecl GmRpcFail_Lua(void* L)
{
    const char* msg = LuaArgStr(L, 1);
    InstBusCaptureError(msg ? msg : "rpc fail");
    return 0;
}

static int __cdecl GmSharedNearby_Lua(void* L)
{
    float x = (float)LuaArgNum(L, 1);
    float y = (float)LuaArgNum(L, 2);
    float r = (float)LuaArgNum(L, 3);
    float r2;
    uint32_t n = 0, i, out = 0;
    const SharedViewObject* arr = PktIpcSharedObjects(&n);
    if (r <= 0.f) r = 40.f;
    r2 = r * r;
    if (!arr) { LuaPushNum(L, 0); return 1; }
    for (i = 0; i < n; i++) {
        float dx = arr[i].x - x;
        float dy = arr[i].y - y;
        if (dx * dx + dy * dy <= r2)
            out++;
    }
    LuaPushNum(L, (double)out);
    return 1;
}

static int __cdecl GmSharedNearbyObject_Lua(void* L)
{
    float x = (float)LuaArgNum(L, 1);
    float y = (float)LuaArgNum(L, 2);
    float r = (float)LuaArgNum(L, 3);
    int idx = (int)LuaArgNum(L, 4);
    float r2;
    uint32_t n = 0, i, seen = 0;
    const SharedViewObject* arr = PktIpcSharedObjects(&n);
    if (r <= 0.f) r = 40.f;
    r2 = r * r;
    if (!arr || idx < 1) return 0;
    for (i = 0; i < n; i++) {
        float dx = arr[i].x - x;
        float dy = arr[i].y - y;
        if (dx * dx + dy * dy > r2)
            continue;
        seen++;
        if ((int)seen == idx)
            return PushSharedViewObject(L, &arr[i]);
    }
    return 0;
}

static int PushUnit(void* L, const ObjMgrUnit* u)
{
    char guid[20], tguid[20];
    _snprintf(guid, sizeof(guid), "%016llX", (unsigned long long)u->guid);
    _snprintf(tguid, sizeof(tguid), "%016llX", (unsigned long long)u->target_guid);
    LuaPushStr(L, guid);
    LuaPushNum(L, (double)u->x);
    LuaPushNum(L, (double)u->y);
    LuaPushNum(L, (double)u->z);
    LuaPushNum(L, (double)u->dist);
    LuaPushNum(L, (double)u->health);
    LuaPushNum(L, (double)u->max_health);
    LuaPushNum(L, (double)u->level);
    LuaPushNum(L, (double)u->faction);
    LuaPushNum(L, (double)u->type_mask);
    LuaPushStr(L, tguid);
    LuaPushNum(L, (double)u->entry);
    LuaPushNum(L, (double)u->dyn_flags);
    /* 14th: UNIT_FIELD_FLAGS (non-attackable / not-selectable / immune-to-PC). */
    LuaPushNum(L, (double)u->unit_flags);
    return 14;
}

enum { kMaxPathPts = 256 };
static float g_path[kMaxPathPts * 3];
static int g_path_n = 0;

static int __cdecl GmFindPath_Lua(void* L)
{
    float sx = (float)LuaArgNum(L, 1), sy = (float)LuaArgNum(L, 2), sz = (float)LuaArgNum(L, 3);
    float ex = (float)LuaArgNum(L, 4), ey = (float)LuaArgNum(L, 5), ez = (float)LuaArgNum(L, 6);
    uint32_t map = ResolveNavMap((uint32_t)LuaArgNum(L, 7), sx, sy, sz);
    g_path_n = NavFindPath(map, sx, sy, sz, ex, ey, ez, g_path, kMaxPathPts);
    LuaPushNum(L, (double)g_path_n);
    return 1;
}

static int __cdecl GmPathPoint_Lua(void* L)
{
    int i = (int)LuaArgNum(L, 1);
    if (i < 1 || i > g_path_n)
        return 0;
    LuaPushNum(L, (double)g_path[(i - 1) * 3 + 0]);
    LuaPushNum(L, (double)g_path[(i - 1) * 3 + 1]);
    LuaPushNum(L, (double)g_path[(i - 1) * 3 + 2]);
    return 3;
}

uint32_t ProxyFindPath(float sx, float sy, float sz,
                       float ex, float ey, float ez, uint32_t map,
                       float* out_xyz, uint32_t max_points)
{
    float local[kMaxPathPts * 3];
    uint32_t n, cap;
    if (!out_xyz || !max_points)
        return 0;
    map = ResolveNavMap(map, sx, sy, sz);
    n = (uint32_t)NavFindPath(map, sx, sy, sz, ex, ey, ez, local, kMaxPathPts);
    cap = n > max_points ? max_points : n;
    memcpy(out_xyz, local, cap * sizeof(float) * 3u);
    return cap;
}

static int __cdecl GmObjectCount_Lua(void* L)
{
    LuaPushNum(L, (double)ObjMgrCacheCount());
    return 1;
}

static int __cdecl GmObjectPump_Lua(void* L)
{
    int force = (int)LuaArgNum(L, 1);
    if (force)
        ObjMgrInvalidate();
    ObjMgrPump();
    LuaPushNum(L, (double)ObjMgrCacheCount());
    return 1;
}

static int __cdecl GmObjectCacheAge_Lua(void* L)
{
    uint32_t age = ObjMgrCacheAgeMs();
    if (age == 0xFFFFFFFFu)
        LuaPushNum(L, -1.0);
    else
        LuaPushNum(L, (double)age);
    return 1;
}

static int __cdecl GmObjectGen_Lua(void* L)
{
    LuaPushNum(L, (double)ObjMgrCacheGen());
    return 1;
}

/* gen, count, ageMs — one round-trip for Lua OM table sync. */
static int __cdecl GmObjectSync_Lua(void* L)
{
    int force = (int)LuaArgNum(L, 1);
    uint32_t age;
    if (force)
        ObjMgrInvalidate();
    ObjMgrPump();
    age = ObjMgrCacheAgeMs();
    LuaPushNum(L, (double)ObjMgrCacheGen());
    LuaPushNum(L, (double)ObjMgrCacheCount());
    if (age == 0xFFFFFFFFu)
        LuaPushNum(L, -1.0);
    else
        LuaPushNum(L, (double)age);
    return 3;
}

static int __cdecl GmObjectInfo_Lua(void* L)
{
    ObjMgrUnit u;
    int i = (int)LuaArgNum(L, 1);
    if (i < 1 || !ObjMgrCacheGet((uint32_t)(i - 1), &u))
        return 0;
    return PushUnit(L, &u);
}

static uint64_t LuaArgGuid64(void* L, int idx)
{
    const char* s = LuaArgStr(L, idx);
    uint64_t guid = 0;
    if (!s)
        return 0;
    if (s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
        s += 2;
    while (*s) {
        char c = *s++;
        int d;
        if (c >= '0' && c <= '9') d = c - '0';
        else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
        else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
        else break;
        guid = (guid << 4) | (uint32_t)d;
    }
    return guid;
}

static int __cdecl GmObjectByGuid_Lua(void* L)
{
    ObjMgrUnit u;
    uint64_t guid = LuaArgGuid64(L, 1);
    if (!guid)
        return 0;
    /* Live OM walk is the source of truth for XYZ. Cache is a fallback
     * only when the object pointer is mid-unlink this frame. */
    if (ObjMgrLiveByGuid(guid, &u))
        return PushUnit(L, &u);
    if (ObjMgrCacheFind(guid, &u))
        return PushUnit(L, &u);
    return 0;
}

enum { kOmPlayerCap = 256 };
static ObjMgrNamed g_om_players[kOmPlayerCap];
static uint32_t g_om_player_n;

/* Live OM walk of every player within range (default 200 yd), with names. */
static int __cdecl GmOmPlayerPump_Lua(void* L)
{
    float max_dist = (float)LuaArgNum(L, 1);
    if (max_dist <= 0.0f)
        max_dist = 200.0f;
    g_om_player_n = ObjMgrCollectPlayers(g_om_players, kOmPlayerCap, max_dist);
    LuaPushNum(L, (double)g_om_player_n);
    return 1;
}

static int __cdecl GmOmPlayerCount_Lua(void* L)
{
    LuaPushNum(L, (double)g_om_player_n);
    return 1;
}

static int __cdecl GmOmPlayerInfo_Lua(void* L)
{
    int i = (int)LuaArgNum(L, 1);
    char guid[20];
    const ObjMgrNamed* r;
    if (i < 1 || (uint32_t)i > g_om_player_n)
        return 0;
    r = &g_om_players[i - 1];
    _snprintf(guid, sizeof(guid), "%016llX", (unsigned long long)r->unit.guid);
    LuaPushStr(L, guid);
    LuaPushStr(L, r->name);
    LuaPushNum(L, (double)r->unit.dist);
    LuaPushNum(L, (double)r->unit.x);
    LuaPushNum(L, (double)r->unit.y);
    LuaPushNum(L, (double)r->unit.z);
    LuaPushNum(L, (double)r->unit.faction);
    LuaPushNum(L, (double)r->unit.level);
    return 8;
}

static int __cdecl GmNearest_Lua(void* L)
{
    uint32_t mask = (uint32_t)LuaArgNum(L, 1);
    uint32_t n = ObjMgrCacheCount();
    uint32_t i;
    ObjMgrUnit u;
    ObjMgrPump();
    n = ObjMgrCacheCount();
    for (i = 0; i < n; i++) {
        if (!ObjMgrCacheGet(i, &u))
            break;
        if (mask == 0 || (u.type_mask & mask)) {
            LuaPushNum(L, (double)(i + 1));
            return 1;
        }
    }
    LuaPushNum(L, 0.0);
    return 1;
}

int ProxyLineOfSightGuid(uint64_t target_guid, uint32_t map);
int ProxySetMouseoverGuid(uint64_t guid);

static int UnitIsTaggedByOther(const ObjMgrUnit* u)
{
    int tapped = (u->dyn_flags & kUnitDynTapped) != 0;
    int by_me = (u->dyn_flags & kUnitDynTappedByPlayer) != 0;
    int by_all = (u->dyn_flags & kUnitDynTappedByAllThreat) != 0;
    return tapped && !by_me && !by_all;
}

__attribute__((unused))
static int UnitIsDeadCorpse(const ObjMgrUnit* u)
{
    int is_corpse = (u->type_mask & kTypeMaskCorpse) != 0;
    int is_unit = (u->type_mask & kTypeMaskUnit) != 0;
    int dyn_dead = (u->dyn_flags & kUnitDynDead) != 0;
    return is_corpse || dyn_dead || (is_unit && u->health == 0u);
}

static int UnitIsLootCandidate(const ObjMgrUnit* u)
{
    int is_player = (u->type_mask & kTypeMaskPlayer) != 0;
    int is_corpse = (u->type_mask & kTypeMaskCorpse) != 0;
    int is_unit = (u->type_mask & kTypeMaskUnit) != 0;
    int is_go = (u->type_mask & kTypeMaskGameObject) != 0;
    int flagged = (u->dyn_flags & kUnitDynLootable) != 0;
    uint32_t gt, goflags, godyn;

    if (is_go) {
        void* obj = ObjMgrFindByGuid(u->guid);
        goflags = u->dyn_flags;
        godyn = u->faction; /* CollectVisit stashes GAMEOBJECT_DYNAMIC here. */
        gt = u->level;
        if (obj) {
            goflags = ObjMgrGoFlags(obj);
            godyn = ObjMgrGoDynamic(obj);
            gt = ObjMgrGoType(obj);
            if (!gt)
                gt = u->level;
        }
        if (goflags & (kGoFlagInUse | kGoFlagLocked | kGoFlagNoInteract))
            return 0;
        if (godyn & kGoDynNoInteract)
            return 0;
        if (ObjMgrGoTypeIsInteractLoot(gt))
            return 1;
        if (godyn & (kGoDynActivate | kGoDynSparkle))
            return 1;
        /* Visible nearby GO whose type byte did not decode — still lootable. */
        if (u->dist <= 80.0f)
            return 1;
        return 0;
    }
    if (!(is_unit || is_corpse))
        return 0;
    if (is_player)
        return 0;
    if (!flagged)
        return 0;
    if (UnitIsTaggedByOther(u))
        return 0;
    return 1;
}

static int UnitIsSkinnableCandidate(const ObjMgrUnit* u)
{
    int is_player = (u->type_mask & kTypeMaskPlayer) != 0;
    int is_unit = (u->type_mask & kTypeMaskUnit) != 0;
    int is_corpse = (u->type_mask & kTypeMaskCorpse) != 0;

    if (is_player)
        return 0;
    if (!(is_unit || is_corpse))
        return 0;
    if (!(u->unit_flags & kUnitFlagSkinnable))
        return 0;
    return 1;
}

static int UnitIsLootableLive(uint64_t guid)
{
    void* obj;
    ObjMgrUnit u;
    if (!guid)
        return 0;
    obj = ObjMgrFindByGuid(guid);
    if (!obj)
        return 0;
    if (!ObjMgrCacheFind(guid, &u)) {
        memset(&u, 0, sizeof(u));
        u.guid = guid;
        u.type_mask = ObjMgrTypeMask(obj);
    }
    if (u.type_mask & kTypeMaskGameObject) {
        u.dyn_flags = ObjMgrGoFlags(obj);
        u.faction = ObjMgrGoDynamic(obj);
        u.level = ObjMgrGoType(obj);
        u.unit_flags = u.dyn_flags;
    } else {
        u.dyn_flags = ObjMgrDynFlags(obj);
        u.unit_flags = ObjMgrUnitFlags(obj);
    }
    return UnitIsLootCandidate(&u);
}

static int UnitIsSkinnableLive(uint64_t guid)
{
    void* obj;
    ObjMgrUnit u;
    if (!guid)
        return 0;
    obj = ObjMgrFindByGuid(guid);
    if (!obj)
        return 0;
    if (!ObjMgrCacheFind(guid, &u)) {
        memset(&u, 0, sizeof(u));
        u.guid = guid;
        u.type_mask = ObjMgrTypeMask(obj);
    }
    u.unit_flags = ObjMgrUnitFlags(obj);
    u.dyn_flags = ObjMgrDynFlags(obj);
    return UnitIsSkinnableCandidate(&u);
}

static int UnitIsValidCombatTarget(const ObjMgrUnit* u, float radius, int allow_tagged)
{
    int is_player = (u->type_mask & kTypeMaskPlayer) != 0;
    int is_unit = (u->type_mask & kTypeMaskUnit) != 0;
    int is_corpse = (u->type_mask & kTypeMaskCorpse) != 0;
    uint32_t bad_flags = kUnitFlagNonAttackable | kUnitFlagNotAttackable1
        | kUnitFlagImmuneToPc | kUnitFlagNotSelectable;

    if (!is_unit || is_player || is_corpse)
        return 0;
    if (u->health == 0u || (u->dyn_flags & kUnitDynDead))
        return 0;
    /* Sparkling loot corpse — not a combat target. */
    if (u->dyn_flags & kUnitDynLootable)
        return 0;
    if (u->unit_flags & bad_flags)
        return 0;
    /* entry==0 is common on some Ascension GUID layouts — don't reject. */
    if (radius > 0.0f && u->dist > radius)
        return 0;
    if (!allow_tagged && UnitIsTaggedByOther(u))
        return 0;
    /* Reaction / UnitCanAttack are Lua-side after target selection (OM has no reaction).
     * Near-range LoS is enforced by HuntingBot; engage TP may skip LoS. */
    return 1;
}

static int __cdecl GmNearestLootable_Lua(void* L)
{
    float radius = (float)LuaArgNum(L, 1);
    int include_go = LuaArgNum(L, 2) != 0.0;
    uint32_t n, i;
    ObjMgrUnit u;
    ObjMgrUnit best;
    int have_best = 0;
    char guid_buf[20];
    memset(&best, 0, sizeof(best));
    ObjMgrPump();
    n = ObjMgrCacheCount();
    for (i = 0; i < n; i++) {
        if (!ObjMgrCacheGet(i, &u))
            break;
        if (radius > 0.0f && u.dist > radius)
            continue;
        if (u.type_mask & (kTypeMaskUnit | kTypeMaskCorpse)) {
            void* obj = ObjMgrFindByGuid(u.guid);
            if (obj)
                u.dyn_flags = ObjMgrDynFlags(obj);

            if (!UnitIsLootCandidate(&u))
                continue;
            if (!have_best || u.dist < best.dist) {
                best = u;
                have_best = 1;
            }
            continue;
        }
        if (include_go && (u.type_mask & kTypeMaskGameObject)) {
            if (!UnitIsLootCandidate(&u))
                continue;
            if (!have_best || u.dist < best.dist) {
                best = u;
                have_best = 1;
            }
            continue;
        }
    }
    if (!have_best)
        return 0;
    _snprintf(guid_buf, sizeof(guid_buf), "%016llX", (unsigned long long)best.guid);
    LuaPushStr(L, guid_buf);
    return 1;
}

static int __cdecl GmLoS_Lua(void* L)
{
    const char* s = LuaArgStr(L, 1);
    uint64_t guid = 0;
    uint32_t map;
    int r;
    float ax = 0, ay = 0, az = 0;
    void* self;
    if (s) {
        if (s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
            s += 2;
        while (*s) {
            char c = *s++;
            int d;
            if (c >= '0' && c <= '9') d = c - '0';
            else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
            else break;
            guid = (guid << 4) | (uint32_t)d;
        }
    }
    map = (uint32_t)LuaArgNum(L, 2);
    self = ObjMgrPlayerObject();
    if (self && ObjMgrPosition(self, &ax, &ay, &az, NULL))
        map = ResolveNavMap(map, ax, ay, az);
    else if (map != 0)
        map = ResolveNavMap(map, 0.f, 0.f, 0.f);
    else
        map = g_last_map;
    r = ProxyLineOfSightGuid(guid, map);
    LuaPushNum(L, (double)r);
    return 1;
}

static int __cdecl GmNavZ_Lua(void* L)
{
    float x = (float)LuaArgNum(L, 1);
    float y = (float)LuaArgNum(L, 2);
    uint32_t map = (uint32_t)LuaArgNum(L, 3);
    float zhint = (float)LuaArgNum(L, 4);
    float z = 0.f;
    float z_try = 0.f;
    uint32_t resolved;
    int have = 0;

    /* Caller map first — map 0 is Eastern Kingdoms, not "unset".
     * Same gx/gy tiles exist on every continent (00003031 vs 00013031).
     * If the caller named a map and that tile hits, trust it — Inclusive
     * used to steal Kalimdor's higher sheet and drop you in the trees. */
    if (map != 0xFFFFFFFFu && NavHeightAt(map, x, y, zhint, &z_try)) {
        InterlockedExchange((volatile LONG*)&g_last_map, (LONG)map);
        LuaPushNum(L, (double)z_try);
        return 1;
    }

    resolved = LearnNavMapForTeleport(x, y, zhint);
    if (resolved != 0xFFFFFFFFu && NavHeightAt(resolved, x, y, zhint, &z_try)) {
        z = z_try;
        have = 1;
        map = resolved;
    } else {
        resolved = NavGuessMapInclusive(x, y, zhint);
        if (resolved != 0xFFFFFFFFu && NavHeightAt(resolved, x, y, zhint, &z_try)) {
            z = z_try;
            have = 1;
            map = resolved;
        }
    }

    if (have) {
        InterlockedExchange((volatile LONG*)&g_last_map, (LONG)map);
        LuaPushNum(L, (double)z);
    } else {
        LuaPushNum(L, -100000.0);
    }
    return 1;
}

static int __cdecl GmMapId_Lua(void* L)
{
    RefreshLastMapFromPlayer();
    LuaPushNum(L, (double)g_last_map);
    return 1;
}

static int __cdecl GmPlayerXYZ_Lua(void* L)
{
    void* self = ObjMgrPlayerObject();
    float x = 0, y = 0, z = 0, o = 0;
    if (self)
        ObjMgrPosition(self, &x, &y, &z, &o);
    LuaPushNum(L, (double)x);
    LuaPushNum(L, (double)y);
    LuaPushNum(L, (double)z);
    LuaPushNum(L, (double)o);
    return 4;
}

static int __cdecl GmPlayerPose_Lua(void* L)
{
    void* self = ObjMgrPlayerObject();
    float x = 0, y = 0, z = 0, o = 0;
    uint32_t map = 0;
    if (!self || !ObjMgrPosition(self, &x, &y, &z, NULL)) {
        LuaPushNum(L, 0.0);
        LuaPushNum(L, 0.0);
        LuaPushNum(L, 0.0);
        LuaPushNum(L, 0.0);
        LuaPushNum(L, 0.0);
        return 5;
    }
    ObjMgrReadClientFacing(self, &o);
    RefreshLastMapFromPlayer();
    map = g_last_map;
    LuaPushNum(L, (double)x);
    LuaPushNum(L, (double)y);
    LuaPushNum(L, (double)z);
    LuaPushNum(L, (double)o);
    LuaPushNum(L, (double)map);
    return 5;
}

static int __cdecl GmFace_Lua(void* L)
{
    float tx, ty;
    ForceClearTaint();
    tx = (float)LuaArgNum(L, 1);
    ty = (float)LuaArgNum(L, 2);
    LuaPushNum(L, ProxyFacePoint(tx, ty) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmObjFloat_Lua(void* L)
{
    void* self = ObjMgrPlayerObject();
    int off = (int)LuaArgNum(L, 1);
    LuaPushNum(L, self ? (double)ObjMgrReadFloatAt(self, off) : 0.0);
    return 1;
}

static uint64_t ParseHexGuid(const char* s)
{
    uint64_t g = 0;
    if (!s)
        return 0;
    if (s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
        s += 2;
    while (*s) {
        char c = *s++;
        int d;
        if (c >= '0' && c <= '9') d = c - '0';
        else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
        else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
        else break;
        g = (g << 4) | (uint32_t)d;
    }
    return g;
}

static int GuidHighIsGameObject(uint64_t guid)
{
    uint32_t hi = (uint32_t)(guid >> 48);
    /* WotLK ObjectGuid high: GAMEOBJECT 0xF110, TRANSPORT 0xF120,
     * MO_TRANSPORT 0x1FC0. Do not WalkObjects for these — name probe AVs. */
    return hi == 0xF110u || hi == 0xF111u || hi == 0xF120u
        || hi == 0xF121u || hi == 0x1FC0u || hi == 0x1FC1u;
}

/* Resolve unit/player display name from ObjMgr cache or token (target/player/…). */
static int __cdecl GmUnitName_Lua(void* L)
{
    const char* key = LuaArgStr(L, 1);
    uint64_t guid = 0;
    void* obj = NULL;
    const char* name = NULL;
    char hex[24];
    uint32_t mask;
    if (!key || !key[0])
        return 0;
    guid = ParseHexGuid(key);
    if (guid && GuidHighIsGameObject(guid))
        return 0;
    if (guid)
        obj = ObjMgrFindByGuid(guid);
    if (!obj) {
        if (strcmp(key, "player") == 0)
            obj = ObjMgrPlayerObject();
        else if (strcmp(key, "target") == 0) {
            guid = ProxyReadGuidSlot(kCurrentTargetGuidRva);
            obj = guid ? ObjMgrFindByGuid(guid) : NULL;
        } else if (strcmp(key, "mouseover") == 0) {
            guid = ProxyReadGuidSlot(kMouseoverGuidRva);
            obj = guid ? ObjMgrFindByGuid(guid) : NULL;
        }
    }
    if (!obj)
        return 0;
    if (!guid)
        guid = ObjMgrObjectGuid(obj);
    mask = ObjMgrTypeMask(obj);
    if ((mask & (kTypeMaskUnit | kTypeMaskPlayer)) == 0)
        return 0;
    if (mask & kTypeMaskGameObject)
        return 0;
    /* ObjMgrObjectName is a no-op (offset spray AVd). Do not walk name slots. */
    name = ObjMgrObjectName(obj);
    if (!name || !name[0])
        return 0;
    LuaPushStr(L, name);
    _snprintf(hex, sizeof(hex), "%016llX", (unsigned long long)guid);
    LuaPushStr(L, hex);
    return 2;
}

/* GmReportChat(channel, sender, message [, guidHex [, extra]]) — authoritative client chat text. */
static int __cdecl GmReportChat_Lua(void* L)
{
    const char* channel = LuaArgStr(L, 1);
    const char* sender = LuaArgStr(L, 2);
    const char* message = LuaArgStr(L, 3);
    const char* guidHex = LuaArgStr(L, 4);
    const char* extra = LuaArgStr(L, 5);
    uint64_t guid = ParseHexGuid(guidHex);
    int ok = ChatReportPush(kChatRepChat, guid, sender, channel, message, extra, -1, -1, -1, -1);
    LuaPushNum(L, ok ? 1.0 : 0.0);
    return 1;
}

/* GmReportPlayer(guidHex, name [, level, class, race, gender, extra]) */
static int __cdecl GmReportPlayer_Lua(void* L)
{
    const char* guidHex = LuaArgStr(L, 1);
    const char* name = LuaArgStr(L, 2);
    int level = (int)LuaArgNum(L, 3);
    int class_id = (int)LuaArgNum(L, 4);
    int race = (int)LuaArgNum(L, 5);
    int gender = (int)LuaArgNum(L, 6);
    const char* extra = LuaArgStr(L, 7);
    uint64_t guid = ParseHexGuid(guidHex);
    int ok;
    if (!guid && (!name || !name[0])) {
        LuaPushNum(L, 0.0);
        return 1;
    }
    ok = ChatReportPush(kChatRepPlayer, guid, name, "player", "", extra, level, class_id, race, gender);
    LuaPushNum(L, ok ? 1.0 : 0.0);
    return 1;
}

/* GmSetClipboard(text) — copy arbitrary Lua string to the Windows clipboard (CF_UNICODETEXT). */
static int __cdecl GmSetClipboard_Lua(void* L)
{
    LuaToLStringFn to_lstr;
    unsigned len = 0;
    const char* s;
    int wlen;
    HGLOBAL h = NULL;
    WCHAR* wp;
    int ok = 0;

    if (!g_ascension) {
        LuaPushNum(L, 0.0);
        return 1;
    }

    to_lstr = (LuaToLStringFn)(g_ascension + kLuaToLStringRva);
    s = to_lstr(L, 1, &len);
    if (!s) {
        LuaPushNum(L, 0.0);
        return 1;
    }
    /* Hard cap 32 MiB — profile dumps are large but not unbounded. */
    if (len > (32u * 1024u * 1024u))
        len = 32u * 1024u * 1024u;

    wlen = MultiByteToWideChar(CP_UTF8, 0, s, (int)len, NULL, 0);
    if (wlen <= 0) {
        /* Fallback: treat as system ANSI / raw bytes via CF_TEXT. */
        if (!OpenClipboard(NULL)) {
            LuaPushNum(L, 0.0);
            return 1;
        }
        EmptyClipboard();
        h = GlobalAlloc(GMEM_MOVEABLE, (SIZE_T)len + 1);
        if (h) {
            char* p = (char*)GlobalLock(h);
            if (p) {
                memcpy(p, s, len);
                p[len] = '\0';
                GlobalUnlock(h);
                if (SetClipboardData(CF_TEXT, h))
                    ok = 1;
                else
                    GlobalFree(h);
            } else {
                GlobalFree(h);
            }
        }
        CloseClipboard();
        LuaPushNum(L, ok ? 1.0 : 0.0);
        return 1;
    }

    if (!OpenClipboard(NULL)) {
        LuaPushNum(L, 0.0);
        return 1;
    }
    EmptyClipboard();
    h = GlobalAlloc(GMEM_MOVEABLE, (SIZE_T)(wlen + 1) * sizeof(WCHAR));
    if (!h) {
        CloseClipboard();
        LuaPushNum(L, 0.0);
        return 1;
    }
    wp = (WCHAR*)GlobalLock(h);
    if (!wp) {
        GlobalFree(h);
        CloseClipboard();
        LuaPushNum(L, 0.0);
        return 1;
    }
    MultiByteToWideChar(CP_UTF8, 0, s, (int)len, wp, wlen);
    wp[wlen] = L'\0';
    GlobalUnlock(h);
    if (SetClipboardData(CF_UNICODETEXT, h))
        ok = 1;
    else
        GlobalFree(h);
    CloseClipboard();
    LuaPushNum(L, ok ? 1.0 : 0.0);
    return 1;
}

static int SendGuidOpcode(uint32_t opcode, uint64_t guid)
{
    uint8_t buf[12];
    if (!guid)
        return 0;
    memcpy(buf + 0, &opcode, 4);
    memcpy(buf + 4, &guid, 8);
    return InjectClientPacket(buf, 12);
}

int ProxyTargetGuid(uint64_t guid)
{
    return SendGuidOpcode(kOpSetSelection, guid);
}

int ProxyLootGuid(uint64_t guid, uint32_t mode)
{
    uint32_t op;
    if (!guid)
        return 0;
    /* Packet send is not loot proof — Lua GmLootProof owns window/take.
     * Still refuse to fire if we cannot stand on the live OM object. */
    if (mode != 1u && !ProxyApproachGuid(guid))
        return 0;
    if (mode == 0u)
        op = kOpLoot;
    else if (mode == 1u)
        op = kOpLootRelease;
    else
        op = ProxyInteractOpcodeFor(guid);
    return SendGuidOpcode(op, guid);
}

static int SendBareOpcode(uint32_t opcode)
{
    uint8_t buf[4];
    memcpy(buf, &opcode, 4);
    return InjectClientPacket(buf, 4);
}

static int SendLootSlot(uint32_t lua_slot_1based)
{
    uint8_t buf[5];
    uint32_t op = kOpAutostoreLootItem;
    if (lua_slot_1based == 0u)
        return 0;
    memcpy(buf, &op, 4);
    buf[4] = (uint8_t)(lua_slot_1based - 1u);
    return InjectClientPacket(buf, 5);
}

static void ProxyAuditOpcodes(void)
{
    static const struct { uint32_t op; const char* name; } kExpect[] = {
        { kOpGameObjUse,        "CMSG_GAMEOBJ_USE" },
        { kOpAutostoreLootItem, "CMSG_AUTOSTORE_LOOT_ITEM" },
        { kOpSetSelection,      "CMSG_SET_SELECTION" },
        { kOpLoot,              "CMSG_LOOT" },
        { kOpLootMoney,         "CMSG_LOOT_MONEY" },
        { kOpLootRelease,       "CMSG_LOOT_RELEASE" },
        { kOpGossipHello,       "CMSG_GOSSIP_HELLO" },
    };
    static int done = 0;
    unsigned i;
    if (done)
        return;
    done = 1;
    for (i = 0; i < sizeof(kExpect) / sizeof(kExpect[0]); ++i) {
        char nm[80], msg[200];
        uint32_t found = 0;
        if (ProxyOpcodeName(kExpect[i].op, nm, sizeof(nm))) {
            if (_stricmp(nm, kExpect[i].name) == 0)
                continue;
            _snprintf(msg, sizeof(msg),
                "OPCODE DRIFT: 0x%03X is '%s' on this build, expected '%s'",
                kExpect[i].op, nm, kExpect[i].name);
            LogLine(msg);
        }
        if (ProxyFindOpcode(kExpect[i].name, &found)) {
            _snprintf(msg, sizeof(msg), "  -> '%s' is really 0x%03X here",
                kExpect[i].name, found);
            LogLine(msg);
        }
    }
}

int ProxyFacePoint(float tx, float ty)
{
    void* self = ObjMgrPlayerObject();
    float x = 0, y = 0, z = 0, ang, dx, dy;
    if (!self || !ObjMgrPosition(self, &x, &y, &z, NULL))
        return 0;
    dx = tx - x;
    dy = ty - y;
    if ((dx * dx + dy * dy) < 0.04f)
        return 0;
    ang = atan2f(dy, dx);
    if (ang < 0.0f)
        ang += 6.2831853f;
    return ProxyFaceAngle(ang);
}

static int ProxyInjectMovePacketO(uint32_t opcode, float x, float y, float z, float o)
{
    uint8_t buf[64];
    uint32_t guid, flags = 0, counter, t;
    uint16_t f2 = 0;

    guid = EnsureMoverGuid();
    if (!guid) {
        KickLearnMove();
        guid = EnsureMoverGuid();
    }
    if (!guid)
        return 0;
    EnsureMoveTemplate();

    counter = (uint32_t)InterlockedIncrement(&g_move_counter);
    if (counter == 0 || counter >= 0x10000000u) {
        counter = 1u;
        InterlockedExchange(&g_move_counter, 1);
    }
    t = GetTickCount();
    memset(buf, 0, sizeof(buf));
    memcpy(buf + 0, &opcode, 4);
    memcpy(buf + kAscOffGuid, &guid, 4);
    /* Never inject FALLING. Hover+waterwalk so a FallLand at dest Z is 0 drop. */
    flags = kMoveFlagHover | kMoveFlagDisableGravity | kMoveFlagWaterwalking;
    memcpy(buf + kAscOffFlags, &flags, 4);
    memcpy(buf + kAscOffFlags2, &f2, 2);
    memcpy(buf + kAscOffTime, &t, 4);
    memcpy(buf + kAscOffX, &x, 4);
    memcpy(buf + kAscOffY, &y, 4);
    memcpy(buf + kAscOffZ, &z, 4);
    memcpy(buf + kAscOffO, &o, 4);
    memcpy(buf + kAscOffCounter, &counter, 4);
    return InjectClientPacket(buf, kAscMoveSize) ? 1 : 0;
}

static int ProxyInjectFaceHeartbeat(float x, float y, float z, float o)
{
    return ProxyInjectMovePacketO(0xEEu, x, y, z, o);
}

static int ProxyInjectSetFacing(float x, float y, float z, float o)
{
    if (ProxyInjectMovePacketO(kOpcodeMoveSetFacing, x, y, z, o))
        return 1;

    return ProxyInjectFaceHeartbeat(x, y, z, o);
}

/* Heartbeat + facing at dest Z. Never MSG_MOVE_FALL_LAND — servers splat from
 * last-zone height even when dest Z is the beach (Hunt STV rod death). */
static int ProxyTeleportLoadKick(float x, float y, float z, float o)
{
    int n = 0;
    if (ProxyInjectMovePacketO(0xEEu, x, y, z, o))
        n++;
    if (ProxyInjectMovePacketO(kOpcodeMoveSetFacing, x, y, z, o))
        n++;
    if (ProxyInjectMovePacketO(0xEEu, x, y, z, o))
        n++;
    return n > 0 ? 1 : 0;
}

int ProxyFaceAngle(float ang)
{
    void* self = ObjMgrPlayerObject();
    float x = 0, y = 0, z = 0, prev, d;
    int need_sync;
    if (!self || !ObjMgrPosition(self, &x, &y, &z, NULL))
        return 0;

    if (!ObjMgrFacingOffsetResolved()
        && InterlockedCompareExchange(&g_facing_valid, 0, 0)) {
        ObjMgrCalibrateFacing(g_player_facing);
    }
    while (ang < 0.f) ang += 6.2831853f;
    while (ang >= 6.2831853f) ang -= 6.2831853f;
    if (!ObjMgrSetFacing(self, ang))
        return 0;
    ObjMgrSetPosition(self, x, y, z, ang);
    prev = g_player_facing;
    d = ang - prev;
    if (d < 0.f) d = -d;
    if (d > 3.14159265f) d = 6.2831853f - d;
    need_sync = !InterlockedCompareExchange(&g_facing_valid, 0, 0) || (d > 0.02f);
    g_player_facing = ang;
    InterlockedExchange(&g_facing_valid, 1);
    if (need_sync)
        ProxyInjectSetFacing(x, y, z, ang);
    return 1;
}

float ProxyPlayerFacingCached(void)
{
    if (InterlockedCompareExchange(&g_facing_valid, 0, 0))
        return g_player_facing;
    return -1000.0f;
}

int ProxyFaceUnit(uint64_t guid)
{
    void* self = ObjMgrPlayerObject();
    void* tgt;
    float sx = 0, sy = 0, sz = 0, so = 0;
    float tx = 0, ty = 0, tz = 0, to = 0;
    float dx, dy, ang;
    if (!self || !guid)
        return 0;
    tgt = ObjMgrFindByGuid(guid);
    if (!tgt)
        return 0;
    if (!ObjMgrPosition(self, &sx, &sy, &sz, &so))
        return 0;
    if (!ObjMgrPosition(tgt, &tx, &ty, &tz, &to))
        return 0;

    ObjMgrReadClientFacing(self, &so);
    ObjMgrReadClientFacing(tgt, &to);

    dx = tx - sx;
    dy = ty - sy;
    if ((dx * dx + dy * dy) < 0.04f) {

        ang = to + 3.14159265f;
    } else {

        ang = atan2f(dy, dx);
    }
    if (ang < 0.0f)
        ang += 6.2831853f;
    while (ang >= 6.2831853f)
        ang -= 6.2831853f;
    (void)so;
    return ProxyFaceAngle(ang);
}

static int __cdecl GmTarget_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    LuaPushNum(L, SendGuidOpcode(kOpSetSelection, guid) ? 1.0 : 0.0);
    return 1;
}

static void* ProxyLuaState(void)
{
    void** pp;
    if (!g_ascension)
        return NULL;
    pp = (void**)(g_ascension + kLuaStatePtrRva);
    if (!PtrReadable(pp, sizeof(void*)))
        return NULL;
    return *pp;
}

static uint64_t ProxyReadGuidSlot(uint32_t rva)
{
    const uint64_t* p;
    if (!g_ascension)
        return 0;
    p = (const uint64_t*)(g_ascension + rva);
    if (!PtrReadable(p, 8))
        return 0;
    return *p;
}

static int ProxyWriteGuidSlot(uint32_t rva, uint64_t guid)
{
    uint64_t* p;
    if (!g_ascension)
        return 0;
    p = (uint64_t*)(g_ascension + rva);
    if (!PtrReadable(p, 8))
        return 0;
    *p = guid;
    return 1;
}

static int ProxyWriteCurrentTarget(uint64_t guid)
{
    if (!guid)
        return 0;
    return ProxyWriteGuidSlot(kCurrentTargetGuidRva, guid);
}

typedef void(__cdecl* GameUiSetTargetFn)(uint32_t guid_lo, uint32_t guid_hi);

int ProxySetTargetNative(uint64_t guid)
{
    GameUiSetTargetFn set;
    if (!g_ascension)
        return 0;
    set = (GameUiSetTargetFn)(g_ascension + g_off.game_ui_set_target);
    if (!PtrReadable((const void*)set, 4))
        return 0;
    ForceClearTaint();
    set((uint32_t)(guid & 0xFFFFFFFFu), (uint32_t)(guid >> 32));
    return ProxyReadGuidSlot(kCurrentTargetGuidRva) == guid;
}

static void ProxyGrantHwEvent(void)
{
    uint32_t* flags;
    if (!g_ascension)
        return;
    flags = (uint32_t*)(g_ascension + kHwEventFlagsRva);
    if (!PtrReadable(flags, 4))
        return;
    *flags |= kHwBitTargetNearest;
}

static int ProxyHwEventEnforced(void)
{
    const uint32_t* ctx;
    if (!g_ascension)
        return 0;
    ctx = (const uint32_t*)(g_ascension + kHwEventCtxRva);
    if (!PtrReadable(ctx, 4))
        return 0;
    return *ctx != 0;
}

typedef void(__cdecl* GameUiTargetNearestFn)(int reverse, int mode);

uint64_t ProxyTargetNearestNative(int mode, int reverse)
{
    GameUiTargetNearestFn scan;
    if (!g_ascension)
        return 0;
    if (mode < 0 || mode > 4)
        return 0;
    scan = (GameUiTargetNearestFn)(g_ascension + kGameUiTargetNearestRva);
    if (!PtrReadable((const void*)scan, 4))
        return 0;
    ForceClearTaint();
    ProxyGrantHwEvent();
    scan(reverse ? 1 : 0, mode);
    return ProxyReadGuidSlot(kCurrentTargetGuidRva);
}

typedef int(__cdecl* GameUiInteractGuidFn)(uint32_t guid_lo, uint32_t guid_hi);

int ProxyInteractGuidNative(uint64_t guid)
{
    GameUiInteractGuidFn interact;
    if (!g_ascension || !guid)
        return 0;
    if (!ObjMgrFindByGuid(guid))
        return 0;
    interact = (GameUiInteractGuidFn)(g_ascension + kGameUiInteractGuidRva);
    if (!PtrReadable((const void*)interact, 4))
        return 0;
    ForceClearTaint();

    ProxyWriteGuidSlot(kInteractTargetGuidRva, guid);
    ProxySetMouseoverGuid(guid);
    return interact((uint32_t)(guid & 0xFFFFFFFFu),
                    (uint32_t)(guid >> 32)) ? 1 : 0;
}

uint64_t ProxyTargetNearestValid(float radius, int mode, int allow_tagged)
{
    uint64_t seen[32];
    uint32_t n_seen = 0;
    int i;

    if (mode < 0 || mode > 4)
        mode = 1;
    if (radius <= 0.0f)
        radius = 40.0f;

    ObjMgrPump();
    for (i = 0; i < 32; i++) {
        uint64_t guid;
        ObjMgrUnit u;
        uint32_t s;
        int dup = 0;

        guid = ProxyTargetNearestNative(mode, 0);
        if (!guid)
            return 0;
        for (s = 0; s < n_seen; s++) {
            if (seen[s] == guid) {
                dup = 1;
                break;
            }
        }
        if (dup)
            return 0;
        if (n_seen < 32)
            seen[n_seen++] = guid;

        if (!ObjMgrCacheFind(guid, &u)) {

            ObjMgrPump();
            if (!ObjMgrCacheFind(guid, &u))
                continue;
        }
        if (!UnitIsValidCombatTarget(&u, radius, allow_tagged))
            continue;

        if (ProxyReadGuidSlot(kCurrentTargetGuidRva) != guid) {
            if (!ProxySetTargetNative(guid))
                continue;
        }
        return guid;
    }
    return 0;
}

enum { kLootBatchMax = 32u };

int ProxyLootSessionByGuid(uint64_t guid)
{
    uint32_t slot;
    if (!guid)
        return 0;

    if (!UnitIsLootableLive(guid))
        return 0;
    ProxyAuditOpcodes();
    ForceClearTaint();
    if (!SendGuidOpcode(kOpLoot, guid))
        return 0;
    SendBareOpcode(kOpLootMoney);
    for (slot = 1u; slot <= 16u; slot++)
        SendLootSlot(slot);
    SendGuidOpcode(kOpLootRelease, guid);
    return 1;
}

int ProxyLootOpenGuid(uint64_t guid)
{
    if (!guid)
        return 0;

    if (!UnitIsLootableLive(guid) && !UnitIsSkinnableLive(guid))
        return 0;
    ProxyAuditOpcodes();
    ForceClearTaint();
    return SendGuidOpcode(kOpLoot, guid) ? 1 : 0;
}

int ProxyLootTakeOpen(uint64_t guid)
{
    uint32_t slot;
    ForceClearTaint();
    ProxyAuditOpcodes();
    SendBareOpcode(kOpLootMoney);
    for (slot = 1u; slot <= 16u; slot++)
        SendLootSlot(slot);
    if (guid)
        SendGuidOpcode(kOpLootRelease, guid);
    return 1;
}

int ProxySkinStartGuid(uint64_t guid)
{
    if (!guid || !UnitIsSkinnableLive(guid))
        return 0;

    ForceClearTaint();
    if (!ProxySetTargetNative(guid)) {
        ProxyWriteCurrentTarget(guid);
        SendGuidOpcode(kOpSetSelection, guid);
    }
    ProxyWriteGuidSlot(kInteractTargetGuidRva, guid);
    ProxySetMouseoverGuid(guid);

    if (ProxyInteractGuidNative(guid))
        return 1;
    RunFrameScriptExecute("pcall(CastSpellByName,\"Skinning\")");
    return 1;
}

int ProxyIsSkinnable(uint64_t guid)
{
    if (!guid)
        return 0;
    ObjMgrPump();
    return UnitIsSkinnableLive(guid);
}

uint32_t ProxyCollectLootable(float radius, uint64_t* out, uint32_t max)
{
    uint32_t n, i, found = 0;
    ObjMgrUnit u;
    if (!out || !max)
        return 0;
    if (radius <= 0.0f)
        radius = 8.0f;
    ObjMgrPump();
    n = ObjMgrCacheCount();
    for (i = 0; i < n && found < max; i++) {
        void* obj;
        if (!ObjMgrCacheGet(i, &u))
            break;
        if (u.dist > radius)
            continue;

        obj = ObjMgrFindByGuid(u.guid);
        if (obj) {
            if (u.type_mask & kTypeMaskGameObject) {
                u.dyn_flags = ObjMgrGoFlags(obj);
                u.faction = ObjMgrGoDynamic(obj);
                u.level = ObjMgrGoType(obj);
            } else {
                u.dyn_flags = ObjMgrDynFlags(obj);
            }
        }
        if (!UnitIsLootCandidate(&u))
            continue;
        out[found++] = u.guid;
    }
    return found;
}

uint32_t ProxyLootNearestNative(float radius)
{
    uint64_t guids[kLootBatchMax];
    uint32_t found = ProxyCollectLootable(radius, guids, kLootBatchMax);
    uint32_t i, ok = 0;
    for (i = 0; i < found; i++) {
        if (ProxyLootSessionByGuid(guids[i]))
            ok++;
    }
    return ok;
}

uint32_t ProxyCollectSkinnable(float radius, uint64_t* out, uint32_t max)
{
    uint32_t n, i, found = 0;
    ObjMgrUnit u;
    if (!out || !max)
        return 0;
    if (radius <= 0.0f)
        radius = 8.0f;
    ObjMgrPump();
    n = ObjMgrCacheCount();
    for (i = 0; i < n && found < max; i++) {
        void* obj;
        if (!ObjMgrCacheGet(i, &u))
            break;
        if (u.dist > radius)
            continue;
        obj = ObjMgrFindByGuid(u.guid);
        if (obj)
            u.unit_flags = ObjMgrUnitFlags(obj);
        if (!UnitIsSkinnableCandidate(&u))
            continue;
        out[found++] = u.guid;
    }
    return found;
}

uint32_t ProxySkinNearestNative(float radius)
{
    uint64_t guids[kLootBatchMax];
    uint32_t n = ProxyCollectSkinnable(radius, guids, kLootBatchMax);
    uint32_t i, ok = 0;
    for (i = 0; i < n; i++) {
        if (ProxySkinStartGuid(guids[i]))
            ok++;

        break;
    }
    return ok;
}

static int ProxyCallScript1Str(uint32_t fn_rva, const char* token)
{
    void* lua;
    LuaCFunction fn;
    char script[160];
    if (!g_ascension || !token || !token[0])
        return 0;
    ForceClearTaint();
    lua = ProxyLuaState();
    fn = (LuaCFunction)(g_ascension + fn_rva);
    if (lua && PtrReadable((const void*)fn, 4)) {
        LuaPushStr(lua, token);
        fn(lua);
        return 1;
    }

    _snprintf(script, sizeof(script), "pcall(%s,\"%s\")",
        (fn_rva == kScriptInteractUnitRva) ? "InteractUnit" : "TargetUnit",
        token);
    RunFrameScriptExecute(script);
    return 1;
}

int ProxyTargetUnit(const char* token)
{
    return ProxyCallScript1Str(kScriptTargetUnitRva, token);
}

int ProxyInteractUnit(const char* token)
{
    return ProxyCallScript1Str(kScriptInteractUnitRva, token);
}

int ProxyTargetGuidUnit(uint64_t guid)
{
    if (!guid)
        return 0;
    if (!ObjMgrFindByGuid(guid))
        return 0;
    if (ProxySetTargetNative(guid))
        return 1;

    ProxyWriteCurrentTarget(guid);
    SendGuidOpcode(kOpSetSelection, guid);
    return ProxyReadGuidSlot(kCurrentTargetGuidRva) == guid;
}

int ProxySetMouseoverGuid(uint64_t guid)
{
    return ProxyWriteGuidSlot(kMouseoverGuidRva, guid);
}

int ProxyLookAt(float tx, float ty, float tz)
{
    void* self = ObjMgrPlayerObject();
    float x = 0, y = 0, z = 0, dx, dy, ang;
    (void)tz;
    if (!self || !ObjMgrPosition(self, &x, &y, &z, NULL))
        return 0;
    dx = tx - x;
    dy = ty - y;
    if ((dx * dx + dy * dy) < 0.04f)
        return 0;
    ang = atan2f(dy, dx);
    if (ang < 0.0f)
        ang += 6.2831853f;
    return ProxyFaceAngle(ang);
}

static uint32_t ProxyInteractOpcodeFor(uint64_t guid)
{
    void* obj = ObjMgrFindByGuid(guid);
    uint32_t mask = obj ? ObjMgrTypeMask(obj) : 0;
    ObjMgrUnit u;

    if (mask & kTypeMaskGameObject)
        return kOpGameObjUse;
    if (mask & kTypeMaskCorpse)
        return kOpLoot;
    if (mask & (kTypeMaskUnit | kTypeMaskPlayer)) {
        if (ObjMgrCacheFind(guid, &u)) {
            if ((u.dyn_flags & (kUnitDynLootable | kUnitDynDead)) || u.health == 0u)
                return kOpLoot;
            return kOpGossipHello;
        }

        return kOpLoot;
    }
    return kOpLoot;
}

int ProxyRightClickGuid(uint64_t guid)
{
    int targeted, sent;
    if (!guid)
        return 0;
    targeted = ProxySetTargetNative(guid);
    if (!targeted) {
        ProxyWriteCurrentTarget(guid);
        SendGuidOpcode(kOpSetSelection, guid);
        targeted = ProxyReadGuidSlot(kCurrentTargetGuidRva) == guid;
    }

    ProxyWriteGuidSlot(kInteractTargetGuidRva, guid);
    ProxySetMouseoverGuid(guid);
    sent = SendGuidOpcode(ProxyInteractOpcodeFor(guid), guid);
    return (targeted && sent) ? 1 : 0;
}

static int __cdecl GmTargetUnit_Lua(void* L)
{
    const char* tok = LuaArgStr(L, 1);
    ForceClearTaint();
    LuaPushNum(L, ProxyTargetUnit(tok && tok[0] ? tok : "target") ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmTargetGuid_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    ForceClearTaint();
    LuaPushNum(L, ProxyTargetGuidUnit(guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmClearTarget_Lua(void* L)
{
    ForceClearTaint();
    ProxyGrantHwEvent();
    (void)ProxySetTargetNative(0);
    if (ProxyReadGuidSlot(kCurrentTargetGuidRva) != 0ull)
        ProxyWriteGuidSlot(kCurrentTargetGuidRva, 0ull);
    LuaPushNum(L, ProxyReadGuidSlot(kCurrentTargetGuidRva) == 0ull ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmInteractUnit_Lua(void* L)
{
    const char* tok = LuaArgStr(L, 1);
    ForceClearTaint();
    LuaPushNum(L, ProxyInteractUnit(tok && tok[0] ? tok : "target") ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmLookAt_Lua(void* L)
{
    float tx = (float)LuaArgNum(L, 1);
    float ty = (float)LuaArgNum(L, 2);
    float tz = (float)LuaArgNum(L, 3);
    ForceClearTaint();
    LuaPushNum(L, ProxyLookAt(tx, ty, tz) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmRightClick_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    ForceClearTaint();
    LuaPushNum(L, ProxyRightClickGuid(guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmLoot_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    ProxyAuditOpcodes();
    LuaPushNum(L, SendGuidOpcode(kOpLoot, guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmLootSlot_Lua(void* L)
{
    uint32_t slot = (uint32_t)LuaArgNum(L, 1);
    LuaPushNum(L, SendLootSlot(slot) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmLootMoney_Lua(void* L)
{
    LuaPushNum(L, SendBareOpcode(kOpLootMoney) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmLootSource_Lua(void* L)
{
    uint64_t g = ProxyReadGuidSlot(kLootSourceGuidRva);
    char buf[24];
    if (!g)
        return 0;
    _snprintf(buf, sizeof(buf), "%08x%08x",
        (unsigned)(g >> 32), (unsigned)(g & 0xFFFFFFFFu));
    LuaPushStr(L, buf);
    return 1;
}

static int __cdecl GmSetMouseover_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    LuaPushNum(L, ProxySetMouseoverGuid(guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmTargetSlots_Lua(void* L)
{
    static const uint32_t kSlots[] = {
        kCurrentTargetGuidRva, kMouseoverGuidRva, kInteractTargetGuidRva,
        kLastEnemyGuidRva, kLootSourceGuidRva, kLastTargetGuidRva,
    };
    unsigned i;
    for (i = 0; i < sizeof(kSlots) / sizeof(kSlots[0]); ++i) {
        uint64_t g = ProxyReadGuidSlot(kSlots[i]);
        char buf[24];
        _snprintf(buf, sizeof(buf), "%08x%08x",
            (unsigned)(g >> 32), (unsigned)(g & 0xFFFFFFFFu));
        LuaPushStr(L, buf);
    }
    return (int)(sizeof(kSlots) / sizeof(kSlots[0]));
}

static int __cdecl GmTargetNearest_Lua(void* L)
{
    int mode = (int)LuaArgNum(L, 1);
    int reverse = (int)LuaArgNum(L, 2);
    uint64_t guid;
    char buf[24];
    if (mode <= 0 || mode > 4)
        mode = 1;
    guid = ProxyTargetNearestNative(mode, reverse);
    if (!guid)
        return 0;
    _snprintf(buf, sizeof(buf), "%08x%08x",
        (unsigned)(guid >> 32), (unsigned)(guid & 0xFFFFFFFFu));
    LuaPushStr(L, buf);
    return 1;
}

static int __cdecl GmInteractGuid_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    LuaPushNum(L, ProxyInteractGuidNative(guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmHwEvent_Lua(void* L)
{
    int grant = (int)LuaArgNum(L, 1);
    const uint32_t* flags;
    if (grant)
        ProxyGrantHwEvent();
    LuaPushNum(L, ProxyHwEventEnforced() ? 1.0 : 0.0);
    flags = (const uint32_t*)(g_ascension + kHwEventFlagsRva);
    LuaPushNum(L, (g_ascension && PtrReadable(flags, 4)) ? (double)*flags : 0.0);
    return 2;
}

static int __cdecl GmNearestInfo_Lua(void* L)
{
    const uint32_t* p;
    unsigned i;
    static const uint32_t kFields[] = {
        kNearestCountRva, kNearestIndexRva, kNearestModeRva,
    };
    for (i = 0; i < sizeof(kFields) / sizeof(kFields[0]); ++i) {
        p = (const uint32_t*)(g_ascension + kFields[i]);
        LuaPushNum(L, (g_ascension && PtrReadable(p, 4)) ? (double)*p : 0.0);
    }
    return (int)(sizeof(kFields) / sizeof(kFields[0]));
}

static int __cdecl GmLootRelease_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    LuaPushNum(L, SendGuidOpcode(kOpLootRelease, guid) ? 1.0 : 0.0);
    return 1;
}

static int g_interact_op_logged = 0;
static int __cdecl GmInteract_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    if (!g_interact_op_logged) {
        g_interact_op_logged = 1;
        LogLine("GmInteract: auto GO=0x8B / NPC=0x17B via ProxyLootGuid mode=2");
    }
    LuaPushNum(L, ProxyLootGuid(guid, 2u) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmUseObject_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    LuaPushNum(L, SendGuidOpcode(kOpGameObjUse, guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmLootEx_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    uint32_t mode = (uint32_t)LuaArgNum(L, 2);
    LuaPushNum(L, ProxyLootGuid(guid, mode) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmSetFacing_Lua(void* L)
{
    float o = (float)LuaArgNum(L, 1);
    ForceClearTaint();
    LuaPushNum(L, ProxyFaceAngle(o) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmFaceAngle_Lua(void* L)
{
    float o = (float)LuaArgNum(L, 1);
    ForceClearTaint();
    LuaPushNum(L, ProxyFaceAngle(o) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmFaceUnit_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    ForceClearTaint();
    LuaPushNum(L, ProxyFaceUnit(guid) ? 1.0 : 0.0);
    return 1;
}

int ProxyFaceTarget(void)
{
    uint64_t guid = ProxyReadGuidSlot(kCurrentTargetGuidRva);
    if (!guid)
        return 0;
    return ProxyFaceUnit(guid);
}

static int __cdecl GmFaceTarget_Lua(void* L)
{
    (void)L;
    ForceClearTaint();
    LuaPushNum(L, ProxyFaceTarget() ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmCalibrateFacing_Lua(void* L)
{
    (void)L;
    if (g_facing_valid)
        ObjMgrCalibrateFacing(g_player_facing);
    LuaPushNum(L, (double)ObjMgrFacingOffset());
    return 1;
}

static int __cdecl GmFacingInfo_Lua(void* L)
{
    void* self = ObjMgrPlayerObject();
    float client_f = -1000.f;
    (void)L;
    if (self)
        ObjMgrReadClientFacing(self, &client_f);
    LuaPushNum(L, (double)ObjMgrPositionOffset());
    LuaPushNum(L, (double)ObjMgrFacingOffset());
    LuaPushNum(L, ObjMgrFacingOffsetResolved() ? 1.0 : 0.0);

    LuaPushNum(L, (double)client_f);
    LuaPushNum(L, g_facing_valid ? (double)g_player_facing : -1000.0);
    return 5;
}

static int __cdecl GmLootAll_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    ForceClearTaint();
    LuaPushNum(L, ProxyInteractGuidNative(guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmTargetNearestValid_Lua(void* L)
{
    float radius = (float)LuaArgNum(L, 1);
    int mode = (int)LuaArgNum(L, 2);
    /* Arg 3: allow_tagged (1 = ignore tap status). Default 0 = reject tagged-by-other. */
    int allow_tagged = (int)LuaArgNum(L, 3);
    uint64_t guid;
    char buf[24];
    if (radius <= 0.0f)
        radius = 40.0f;
    if (mode <= 0 || mode > 4)
        mode = 1;
    ForceClearTaint();
    guid = ProxyTargetNearestValid(radius, mode, allow_tagged);
    if (!guid)
        return 0;
    _snprintf(buf, sizeof(buf), "%016llX", (unsigned long long)guid);
    LuaPushStr(L, buf);
    return 1;
}

static uint64_t g_loot_list[kLootBatchMax];
static uint32_t g_loot_list_n = 0;
static uint64_t g_skin_list[kLootBatchMax];
static uint32_t g_skin_list_n = 0;

static int __cdecl GmLootableCount_Lua(void* L)
{
    float radius = (float)LuaArgNum(L, 1);
    if (radius <= 0.0f)
        radius = 8.0f;
    ForceClearTaint();
    g_loot_list_n = ProxyCollectLootable(radius, g_loot_list, kLootBatchMax);
    LuaPushNum(L, (double)g_loot_list_n);
    return 1;
}

static int __cdecl GmLootableGuid_Lua(void* L)
{
    int i = (int)LuaArgNum(L, 1);
    char buf[24];
    if (i < 1 || (uint32_t)i > g_loot_list_n)
        return 0;
    _snprintf(buf, sizeof(buf), "%016llX", (unsigned long long)g_loot_list[i - 1]);
    LuaPushStr(L, buf);
    return 1;
}

static int __cdecl GmLootOne_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    ForceClearTaint();
    LuaPushNum(L, ProxyLootSessionByGuid(guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmLootOpen_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    ForceClearTaint();
    LuaPushNum(L, ProxyLootOpenGuid(guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmLootNearest_Lua(void* L)
{
    float radius = (float)LuaArgNum(L, 1);
    uint32_t n;
    if (radius <= 0.0f)
        radius = 8.0f;
    ForceClearTaint();
    n = ProxyLootNearestNative(radius);
    if (!n)
        return 0;
    LuaPushNum(L, (double)n);
    return 1;
}

static int __cdecl GmIsLootable_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    ForceClearTaint();
    LuaPushNum(L, UnitIsLootableLive(guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmIsSkinnable_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    LuaPushNum(L, ProxyIsSkinnable(guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmSkinnableCount_Lua(void* L)
{
    float radius = (float)LuaArgNum(L, 1);
    if (radius <= 0.0f)
        radius = 8.0f;
    ForceClearTaint();
    g_skin_list_n = ProxyCollectSkinnable(radius, g_skin_list, kLootBatchMax);
    LuaPushNum(L, (double)g_skin_list_n);
    return 1;
}

static int __cdecl GmSkinnableGuid_Lua(void* L)
{
    int i = (int)LuaArgNum(L, 1);
    char buf[24];
    if (i < 1 || (uint32_t)i > g_skin_list_n)
        return 0;
    _snprintf(buf, sizeof(buf), "%016llX", (unsigned long long)g_skin_list[i - 1]);
    LuaPushStr(L, buf);
    return 1;
}

static int __cdecl GmSkinStart_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    ForceClearTaint();
    LuaPushNum(L, ProxySkinStartGuid(guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmLootTake_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    ForceClearTaint();
    LuaPushNum(L, ProxyLootTakeOpen(guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmLastLootPkt_Lua(void* L)
{
    uint32_t op = (uint32_t)g_loot_pkt_op;
    uint32_t dir = (uint32_t)g_loot_pkt_dir;
    uint32_t gen = (uint32_t)g_loot_pkt_gen;
    uint32_t len = (uint32_t)g_loot_pkt_len;
    DWORD tick = (DWORD)g_loot_pkt_tick;
    uint64_t guid = ((uint64_t)(uint32_t)g_loot_pkt_guid_hi << 32)
                  | (uint32_t)g_loot_pkt_guid_lo;
    char hex[20];
    (void)L;
    if (!gen)
        return 0;
    LuaPushNum(L, (double)op);
    LuaPushNum(L, (double)dir);
    SharedGuidHex(hex, sizeof(hex), guid);
    LuaPushStr(L, hex);
    LuaPushNum(L, (double)tick);
    LuaPushNum(L, (double)len);
    LuaPushNum(L, (double)gen);
    return 6;
}

static int __cdecl GmSkinNearest_Lua(void* L)
{
    float radius = (float)LuaArgNum(L, 1);
    uint32_t n;
    if (radius <= 0.0f)
        radius = 8.0f;
    ForceClearTaint();
    n = ProxySkinNearestNative(radius);
    if (!n)
        return 0;
    LuaPushNum(L, (double)n);
    return 1;
}

static int __cdecl GmSkin_Lua(void* L)
{
    return GmSkinStart_Lua(L);
}

static int __cdecl GmPlayerFacing_Lua(void* L)
{
    void* self = ObjMgrPlayerObject();
    float f = 0.f;
    if (self && ObjMgrReadClientFacing(self, &f)) {
        LuaPushNum(L, (double)f);
        return 1;
    }
    if (g_facing_valid) {
        f = g_player_facing;
        if (f < 0.f) f += 6.2831853f;
        LuaPushNum(L, (double)f);
        return 1;
    }
    LuaPushNum(L, -1000.0);
    return 1;
}

enum {
    kTpSkipGround       = 0x1u,
    kTpSkipJump         = 0x2u,
    kTpSkipOmInvalidate = 0x4u,
    kGroundTolYd  = 3u,

};

int ProxyApproachGuid(uint64_t guid)
{
    ObjMgrUnit u;
    void* self;
    float px = 0, py = 0, pz = 0, dest_z, nav_z, dx, dy, dz, d, ang;
    uint32_t map;
    int tries;

    if (!guid)
        return 0;
    if (!ObjMgrLiveByGuid(guid, &u))
        return 0;

    self = ObjMgrPlayerObject();
    if (!self || !ObjMgrPosition(self, &px, &py, &pz, NULL))
        return 0;

    dest_z = u.z;
    RefreshLastMapFromPlayer();
    map = g_last_map;
    /* Dummy / insane OM Z → nav floor at this XY. Real OM Z is the loot pose. */
    if (u.z > 400.0f || u.z < -200.0f) {
        if (NavHeightAt(map, u.x, u.y, 500.0f, &nav_z))
            dest_z = nav_z;
    } else {
        NavHeightAt(map, u.x, u.y, u.z, &nav_z);
    }

    dx = u.x - px;
    dy = u.y - py;
    dz = u.z - pz;
    d = sqrtf(dx * dx + dy * dy + dz * dz);
    ang = atan2f(dy, dx);
    if (ang < 0.0f)
        ang += 6.2831853f;

    for (tries = 0; tries < 3 && d > 5.2f; tries++) {
        if (!ProxyTeleportSafeEx(u.x, u.y, dest_z, ang,
                                 kTpSkipGround | kTpSkipJump, 2500u))
            return 0;
        if (!ObjMgrLiveByGuid(guid, &u))
            return 0;
        self = ObjMgrPlayerObject();
        if (!self || !ObjMgrPosition(self, &px, &py, &pz, NULL))
            return 0;
        dx = u.x - px;
        dy = u.y - py;
        dz = u.z - pz;
        d = sqrtf(dx * dx + dy * dy + dz * dz);
        dest_z = u.z;
        if (u.z > 400.0f || u.z < -200.0f) {
            if (NavHeightAt(map, u.x, u.y, 500.0f, &nav_z))
                dest_z = nav_z;
        }
    }
    if (d > 5.5f)
        return 0;
    ProxyFacePoint(u.x, u.y);
    return 1;
}

static int __cdecl GmApproachGuid_Lua(void* L)
{
    uint64_t guid = LuaArgGuid64(L, 1);
    ObjMgrUnit u;
    void* self;
    float px = 0, py = 0, pz = 0;
    int ok;

    ForceClearTaint();
    ok = ProxyApproachGuid(guid);
    self = ObjMgrPlayerObject();
    if (self)
        ObjMgrPosition(self, &px, &py, &pz, NULL);
    LuaPushNum(L, ok ? 1.0 : 0.0);
    if (!ObjMgrLiveByGuid(guid, &u)) {
        LuaPushNum(L, -1.0);
        LuaPushNum(L, (double)px);
        LuaPushNum(L, (double)py);
        LuaPushNum(L, (double)pz);
        return 5;
    }
    LuaPushNum(L, (double)u.dist);
    LuaPushNum(L, (double)px);
    LuaPushNum(L, (double)py);
    LuaPushNum(L, (double)pz);
    LuaPushNum(L, (double)u.x);
    LuaPushNum(L, (double)u.y);
    LuaPushNum(L, (double)u.z);
    return 8;
}

static uint32_t EnsureMoverGuid(void);
static int EnsureMoveTemplate(void);
static int EnsureMoveReady(void);
static void KickLearnMove(void);

static int TpFinite(float v)
{
    return _finite((double)v) != 0;
}

int ProxyValidateTeleportDest(float x, float y, float z, float o,
                              float* out_ground, char* err, size_t errn)
{
    uint32_t map;
    float ground = 0.f;
    float dz;
    const char* why = NULL;

    if (!TpFinite(x) || !TpFinite(y) || !TpFinite(z) || !TpFinite(o)) {
        why = "non-finite x/y/z/o";
        goto fail;
    }
    if (fabsf(x) > 60000.f || fabsf(y) > 60000.f) {
        why = "xy out of world range";
        goto fail;
    }
    if (z < -2000.f || z > 3000.f) {
        why = "z out of world range";
        goto fail;
    }

    if (fabsf(o) > 100.f) {
        why = "facing out of range";
        goto fail;
    }
    if (o != 0.f && fabsf(o) < 1.0e-20f) {
        why = "facing denormal/poison";
        goto fail;
    }

    /* No maps/mmtiles configured → allow TP (player-Z / Raw). Nav is optional. */
    {
        const char* maps = NavMapsRoot();
        const char* tiles = NavHeightRoot();
        if ((!maps || !maps[0]) && (!tiles || !tiles[0])) {
            if (out_ground)
                *out_ground = z;
            if (err && errn)
                err[0] = '\0';
            {
                static volatile LONG s_soft_nonav = 0;
                if ((InterlockedIncrement(&s_soft_nonav) % 64) == 1)
                    LogLine("tp-validate: soft-allow (nav paths not configured)");
            }
            return 1;
        }
    }

    map = LearnNavMapForTeleport(x, y, z);
    if (!NavHeightAt(map, x, y, z, &ground)) {

        float cont_z = 0.f;
        uint32_t cont = NavGuessMap(x, y, z);
        int under_world = (cont != 0xFFFFFFFFu &&
                           NavHeightAt(cont, x, y, z, &cont_z) &&
                           (z - cont_z) < -25.f);
        if ((!NavIsContinentMap(g_last_map) && g_last_map != 0u) || under_world) {
            if (out_ground)
                *out_ground = z;
            if (err && errn)
                err[0] = '\0';
            {
                static volatile LONG s_soft_in = 0;
                if ((InterlockedIncrement(&s_soft_in) % 64) == 1)
                    LogLine("tp-validate: soft-allow instance/indoor (no walkable poly)");
            }
            return 1;
        }
        /* Continent with maps configured but tile missing — still allow (GM map click). */
        if (out_ground)
            *out_ground = z;
        if (err && errn)
            err[0] = '\0';
        {
            static volatile LONG s_soft_miss = 0;
            if ((InterlockedIncrement(&s_soft_miss) % 32) == 1)
                LogLine("tp-validate: soft-allow missing nav tile (use player/request Z)");
        }
        return 1;
    }
    dz = z - ground;
    if (dz > (float)kGroundTolYd) {

        if (NavIsContinentMap(map) || dz > 25.f) {
            why = "above ground (sky/swim)";
            goto fail;
        }
    }
    if (dz < -(float)kGroundTolYd) {
        if (!NavIsContinentMap(map) && dz >= -25.f) {

        } else if (NavIsContinentMap(map) && dz < -25.f) {

            if (out_ground)
                *out_ground = z;
            if (err && errn)
                err[0] = '\0';
            {
                static volatile LONG s_soft_below = 0;
                if ((InterlockedIncrement(&s_soft_below) % 64) == 1)
                    LogLine("tp-validate: soft-allow below continent mesh (instance)");
            }
            return 1;
        } else {
            why = "below ground";
            goto fail;
        }
    }

    if (out_ground)
        *out_ground = ground;
    if (err && errn)
        err[0] = '\0';
    return 1;

fail:
    if (out_ground)
        *out_ground = ground;
    if (err && errn && why)
        _snprintf(err, errn, "%s", why);
    return 0;
}

int ProxyTeleportSafe(float x, float y, float z, float o, uint32_t flags)
{
    return ProxyTeleportSafeEx(x, y, z, o, flags, 0);
}

uint32_t ProxyDefaultTpLockMs(void)
{
    LONG v = InterlockedCompareExchange(&g_tp_lock_default_ms, 0, 0);
    if (v < 50) return 50u;
    if (v > 60000) return 60000u;
    return (uint32_t)v;
}

int ProxyTeleportSafeEx(float x, float y, float z, float o, uint32_t flags, uint32_t lock_ms)
{
    uint8_t buf[64];
    uint32_t len = (uint32_t)g_move_template_len;
    uint32_t Q = g_move_quad_off;
    uint32_t op = 0xEEu, t, cnt;
    float ground = 0.f;
    char msg[160];
    uint32_t pin_ms;


    if (len == 0 || Q < 4 || Q + 20u > len || len > sizeof(buf)) {

        if (Q && Q + 20u > len) {
            _snprintf(msg, sizeof(msg),
                "teleport: discarding unusable quad offset +%u for len=%u "
                "(need Q+20<=len) — re-seeding from player", Q, len);
            LogLine(msg);
            g_move_quad_off = 0;
        }
        EnsureMoveReady();
        len = (uint32_t)g_move_template_len;
        Q = g_move_quad_off;
    }
    if (len == 0 || Q < 4 || Q + 20u > len || len > sizeof(buf)) {
        static volatile LONG s_tp_tmpl = 0;
        _snprintf(msg, sizeof(msg),
            "teleport ABORT: no usable move template (len=%u quad=+%u)", len, Q);
        if ((InterlockedIncrement(&s_tp_tmpl) % 32) == 1)
            LogLine(msg);
        return 0;
    }


    if (!(flags & kTpSkipGround)) {
        char why[96];
        why[0] = '\0';
        if (!ProxyValidateTeleportDest(x, y, z, o, &ground, why, sizeof(why))) {
            static volatile LONG s_tp_rej = 0;
            _snprintf(msg, sizeof(msg),
                "teleport REJECTED at (%.1f,%.1f,%.1f o=%.3f): %s (ground=%.1f)",
                x, y, z, o, why[0] ? why : "invalid", ground);
            if ((InterlockedIncrement(&s_tp_rej) % 16) == 1)
                LogLine(msg);
            return 0;
        }

    }



    memcpy(buf, g_move_template, len);
    memcpy(buf + 0, &op, 4);
    {
        uint32_t mg = EnsureMoverGuid();
        if (mg && len >= 8)
            memcpy(buf + kAscOffGuid, &mg, 4);
    }
    memcpy(buf + Q + 0, &x, 4);
    memcpy(buf + Q + 4, &y, 4);
    memcpy(buf + Q + 8, &z, 4);
    memcpy(buf + Q + 12, &o, 4);
    t = GetTickCount();
    memcpy(buf + Q - 4, &t, 4);
    cnt = (uint32_t)(++g_move_counter);
    memcpy(buf + Q + 16, &cnt, 4);
    SanitizeTpMoveBuf(buf, len);
    {
        uint32_t sendLen = len;
        if (sendLen > kAscMoveSize && Q + 20u <= kAscMoveSize)
            sendLen = kAscMoveSize;
        if (!InjectClientPacket(buf, sendLen))
            return 0;
    }


    if (!(flags & kTpSkipJump)) {
        uint8_t jbuf[64];
        uint32_t jop = (uint32_t)kOpcodeMoveJump;
        uint32_t sendLen;
        memcpy(jbuf, g_move_template, len);
        memcpy(jbuf + 0, &jop, 4);

        t = GetTickCount();
        memcpy(jbuf + Q - 4, &t, 4);
        cnt = (uint32_t)(++g_move_counter);
        memcpy(jbuf + Q + 16, &cnt, 4);
        SanitizeTpMoveBuf(jbuf, len);
        sendLen = len;
        if (sendLen > kAscMoveSize && Q + 20u <= kAscMoveSize)
            sendLen = kAscMoveSize;
        InjectClientPacket(jbuf, sendLen);
    }

    _snprintf(msg, sizeof(msg), "teleport+jump: (%.1f,%.1f,%.1f) ground=%.1f flags=0x%X",
        x, y, z, (flags & kTpSkipGround) ? z : ground, (unsigned)flags);

    {
        static volatile LONG s_tp_ok = 0;
        LONG n = InterlockedIncrement(&s_tp_ok);
        if ((n % 64) == 1)
            LogLine(msg);
    }


    {
        void* self = ObjMgrPlayerObject();
        pin_ms = (lock_ms > 0u) ? lock_ms : ProxyDefaultTpLockMs();
        ProxyTpLock(x, y, z, o, pin_ms, 28.f);
        if (self)
            ObjMgrSetPosition(self, x, y, z, o);
        g_last_player_x = x;
        g_last_player_y = y;
        g_last_player_z = z;
        g_player_facing = o;
        InterlockedExchange(&g_facing_valid, 1);
        InterlockedExchange(&g_last_player_valid, 1);

        if (!(flags & kTpSkipOmInvalidate))
            ObjMgrInvalidate();
    }
    ArmAntiFall();
    return 1;
}

void ProxyTpLock(float x, float y, float z, float o, uint32_t duration_ms, float radius_yd)
{
    g_tp_lock_x = x;
    g_tp_lock_y = y;
    g_tp_lock_z = z;
    g_tp_lock_o = o;
    g_tp_lock_radius = (radius_yd > 1.f) ? radius_yd : 28.f;

    if (duration_ms == 0)
        duration_ms = ProxyDefaultTpLockMs();
    if (duration_ms > 2000u)
        duration_ms = 2000u;
    InterlockedExchange((volatile LONG*)&g_tp_lock_until,
                        (LONG)(GetTickCount() + duration_ms));
    InterlockedExchange(&g_tp_lock, 1);
    InterlockedExchange(&g_tp_lock_rewrites, 0);
}

void ProxyTpUnlock(void)
{
    InterlockedExchange(&g_tp_lock, 0);
    InterlockedExchange((volatile LONG*)&g_tp_lock_until, 0);
}

int ProxyTpLockActive(void)
{
    DWORD until;
    if (!InterlockedCompareExchange(&g_tp_lock, 0, 0))
        return 0;
    until = (DWORD)InterlockedCompareExchange((volatile LONG*)&g_tp_lock_until, 0, 0);
    if (until != 0 && GetTickCount() > until) {
        InterlockedExchange(&g_tp_lock, 0);
        return 0;
    }
    return 1;
}

static int IsMoveFamilyOpcode(uint32_t op)
{

    if (op >= 0x00B5u && op <= 0x00EEu)
        return 1;
    if (op == 0x00F6u || op == 0x00F7u || op == 0x00F8u)
        return 1;
    return 0;
}

static int IsLikelyMovePacket(uint32_t opcode, uint32_t size)
{
    if (size < 30)
        return 0;
    if (opcode == kDefaultMoveOpcode)
        return 1;
    if (opcode >= 0xB5u && opcode <= 0xFFu)
        return 1;
    if (opcode == 0x4F7u || opcode == 0xC9u || opcode == 0xDAu)
        return 1;
    return 0;
}

static uint32_t ApplyFlyFlags(uint32_t flags, int active_flying)
{
    flags |= kMoveFlyPassive;
    flags &= ~kMoveFallingMask;
    if (active_flying)
        flags |= kMoveFlagFlying;
    else
        flags &= ~kMoveFlagFlying;
    return flags;
}

static uint32_t ApplyClientHacks(uint32_t flags, uint32_t hacks)
{
    if (hacks & kHackWaterwalk)
        flags |= kMoveFlagWaterwalking;
    if (hacks & kHackHover)
        flags |= kMoveFlagHover | kMoveFlagDisableGravity;
    if (hacks & kHackNoFall)
        flags &= ~kMoveFallingMask;
    if (hacks & kHackAntiRoot)
        flags &= ~kMoveFlagRoot;
    if (hacks & kHackNoclip) {
        flags |= kMoveFlagDisableGravity | kMoveFlagHover | kMoveFlagWaterwalking | kMoveFlagCanFly;
        flags &= ~(kMoveFlagRoot | kMoveFallingMask);
    }
    return flags;
}

/* Injected teleports bypass PatchOutboundMove. A sniffed falling template
 * replayed at the dest Z is a splat (last height in Elwynn → STV beach). */
static void SanitizeTpMoveBuf(uint8_t* buf, uint32_t len)
{
    uint32_t flags;
    uint16_t f2 = 0;
    if (!buf || len < kAscOffFlags + 4u)
        return;
    memcpy(&flags, buf + kAscOffFlags, 4);
    flags &= ~(kMoveFallingMask | kMoveFlagAscending | kMoveFlagDescending);
    flags |= kMoveFlagHover | kMoveFlagDisableGravity | kMoveFlagWaterwalking;
    memcpy(buf + kAscOffFlags, &flags, 4);
    if (len >= kAscOffFlags2 + 2u)
        memcpy(buf + kAscOffFlags2, &f2, 2);
}

static void TruncateMovePayload(CDataStore* packet, uint32_t off, uint32_t new_len)
{
    if (!packet || new_len < kAscMoveSize)
        return;
    if (packet->size > off + new_len)
        packet->size = off + new_len;
}

static uint32_t PacketPayload(const CDataStore* packet, const uint8_t** out_ptr);
static uint32_t ReadOpcode(const uint8_t* data, uint32_t size);
enum { kMaxOpcode = 0x9D4u };

static int IsForbiddenGmTeleportOpcode(uint32_t op)
{
    /* Player accounts: these CMSG/MSG cheats return LANG_COMMAND_PERMISSIONS
     * and do not move server vis. Swallow — never send. */
    return op == kOpcodeWorldTeleport
        || op == kOpcodeMoveCharmPortCheat
        || op == kOpcodeMoveTeleportCheat
        || op == kOpcodeTeleportToUnit;
}

/* Server HandleFall is keyed off MSG_MOVE_FALL_LAND (0xC9), not flags.
 * Always rewrite to heartbeat before NetClient::Send. */
static int RewriteFallLandToHeartbeat(CDataStore* packet)
{
    const uint8_t* ro = NULL;
    uint8_t* raw;
    uint32_t len, opcode, flags, off;
    uint32_t hb = kDefaultMoveOpcode;
    uint16_t zf2 = 0;
    if (!packet || !packet->buffer)
        return 0;
    len = PacketPayload(packet, &ro);
    if (!len || !ro || len < kAscMoveSize)
        return 0;
    off = (uint32_t)(ro - packet->buffer);
    if (off >= packet->alloc || len > packet->alloc - off)
        return 0;
    raw = packet->buffer + off;
    opcode = ReadOpcode(raw, len);
    if (opcode != kOpcodeMoveFallLand)
        return 0;
    memcpy(raw, &hb, 4);
    memcpy(&flags, raw + kAscOffFlags, 4);
    flags &= ~(kMoveFallingMask | kMoveFlagAscending | kMoveFlagDescending);
    flags |= kMoveFlagHover | kMoveFlagDisableGravity | kMoveFlagWaterwalking;
    memcpy(raw + kAscOffFlags, &flags, 4);
    if (len >= kAscOffFlags2 + 2u)
        memcpy(raw + kAscOffFlags2, &zf2, 2);
    TruncateMovePayload(packet, off, kAscMoveSize);
    {
        static volatile LONG s_c9 = 0;
        if ((InterlockedIncrement(&s_c9) % 8) == 1)
            LogLine("antifall: FALL_LAND -> HEARTBEAT (always)");
    }
    return 1;
}

static void PatchOutboundMove(CDataStore* packet)
{
    const uint8_t* ro = NULL;
    uint8_t* raw;
    uint32_t len, opcode, flags, old_flags, off;
    MovementConfig snap;
    int fly, xyz, any_hack, is_jump, was_falling, boost_z = 0;
    float scale, z, jump_boost;
    static volatile LONG s_patch_logs;

    if (!packet || !packet->buffer)
        return;
    memcpy(&snap, (const void*)&g_config, sizeof(snap));
    if (snap.magic != MOVE_CONFIG_MAGIC)
        return;
    fly = snap.flyhack ? 1 : 0;

    xyz = (snap.no_zclip || snap.enabled) ? 1 : 0;
    if (ProxyTpLockActive())
        xyz = 0;
    any_hack = (snap.hacks != 0) ? 1 : 0;
    scale = snap.speed_scale;
    if (scale < kSpeedScaleMin)
        scale = kSpeedScaleMin;
    if (scale > kSpeedScaleMax)
        scale = kSpeedScaleMax;
    {
        int block_fall = AntiFallActive() || ProxyTpLockActive()
            || fly || (snap.hacks & (kHackNoFall | kHackNoclip | kHackHover));
        if (!fly && !xyz && !any_hack && !block_fall && fabsf(scale - 1.f) < 0.01f) {
            InterlockedExchange(&g_fly_airborne, 0);
            return;
        }
    }

    len = PacketPayload(packet, &ro);
    if (!len || !ro || len < kAscMoveSize)
        return;
    off = (uint32_t)(ro - packet->buffer);
    if (off >= packet->alloc || len > packet->alloc - off)
        return;
    raw = packet->buffer + off;
    opcode = ReadOpcode(raw, len);
    if (!IsLikelyMovePacket(opcode, len))
        return;

    memcpy(&old_flags, raw + kAscOffFlags, 4);
    flags = old_flags;
    is_jump = (opcode == kOpcodeMoveJump);
    was_falling = (old_flags & kMoveFallingMask) != 0;
    {
        int block_fall = AntiFallActive() || ProxyTpLockActive()
            || fly || (snap.hacks & (kHackNoFall | kHackNoclip | kHackHover));
        if (block_fall && opcode == kOpcodeMoveFallLand) {
            uint32_t hb = kDefaultMoveOpcode;
            memcpy(raw, &hb, 4);
            opcode = hb;
            flags &= ~(kMoveFallingMask | kMoveFlagAscending | kMoveFlagDescending);
            flags |= kMoveFlagHover | kMoveFlagDisableGravity | kMoveFlagWaterwalking;
            memcpy(raw + kAscOffFlags, &flags, 4);
            TruncateMovePayload(packet, off, kAscMoveSize);
            len = PacketPayload(packet, &ro);
            if (len >= kAscMoveSize)
                raw = packet->buffer + off;
            {
                static volatile LONG s_c9 = 0;
                if ((InterlockedIncrement(&s_c9) % 16) == 1)
                    LogLine("antifall: FALL_LAND -> HEARTBEAT (server HandleFall is opcode-based)");
            }
        } else if (block_fall) {
            flags &= ~(kMoveFallingMask | kMoveFlagAscending | kMoveFlagDescending);
            flags |= kMoveFlagHover | kMoveFlagDisableGravity | kMoveFlagWaterwalking;
        }
    }

    if (xyz) {
        flags &= ~kMoveMaskMoving;
        flags &= ~(kMoveFlyhackFlags | kMoveFlagHover | kMoveFlagWaterwalking);
        if (!fly)
            InterlockedExchange(&g_fly_airborne, 0);
    } else if (fly) {
        if (is_jump || was_falling || opcode == kOpcodeMoveFallLand)
            InterlockedExchange(&g_fly_airborne, 1);
        flags = ApplyFlyFlags(flags, 1);
        if (g_fly_airborne) {
            flags |= kMoveFlagFlying | kMoveFlyPassive;
            if (is_jump || was_falling)
                flags |= kMoveFlagAscending;
            flags &= ~kMoveFallingMask;
        }
        if (opcode == kOpcodeMoveFallLand && g_fly_airborne) {
            flags |= kMoveFlagFlying | kMoveFlyPassive;
            flags &= ~kMoveFallingMask;
        }
        boost_z = (is_jump || was_falling) ? 1 : 0;
    }

    flags = ApplyClientHacks(flags, snap.hacks);
    memcpy(raw + kAscOffFlags, &flags, 4);
    {
        uint16_t zf2 = 0;
        memcpy(raw + kAscOffFlags2, &zf2, 2);
    }

    if (xyz) {
        memcpy(raw + kAscOffX, &snap.world_x, 4);
        memcpy(raw + kAscOffY, &snap.world_y, 4);
        memcpy(raw + kAscOffZ, &snap.world_z, 4);
        memcpy(raw + kAscOffO, &snap.facing, 4);
    } else if (boost_z) {
        jump_boost = (snap.hacks & kHackSuperJump) ? 28.f : 12.f;
        memcpy(&z, raw + kAscOffZ, 4);
        z += jump_boost;
        memcpy(raw + kAscOffZ, &z, 4);
    } else if (fabsf(scale - 1.f) >= 0.01f && InterlockedCompareExchange(&g_last_player_valid, 0, 0)) {

        float x, y, o, lx, ly, lz;
        memcpy(&x, raw + kAscOffX, 4);
        memcpy(&y, raw + kAscOffY, 4);
        memcpy(&z, raw + kAscOffZ, 4);
        memcpy(&o, raw + kAscOffO, 4);
        lx = g_last_player_x;
        ly = g_last_player_y;
        lz = g_last_player_z;
        x = lx + (x - lx) * scale;
        y = ly + (y - ly) * scale;
        z = lz + (z - lz) * scale;
        memcpy(raw + kAscOffX, &x, 4);
        memcpy(raw + kAscOffY, &y, 4);
        memcpy(raw + kAscOffZ, &z, 4);
        (void)o;
    }

    if ((fly || (snap.hacks & kHackNoFall)) && !(flags & kMoveFallingMask) && len > kAscMoveSize)
        TruncateMovePayload(packet, off, kAscMoveSize);

    if (InterlockedIncrement(&s_patch_logs) <= 40) {
        char msg[160];
        _snprintf(msg, sizeof(msg),
            "hack-patch op=0x%X flags 0x%X->0x%X fly=%u hacks=0x%X scale=%.2f",
            opcode, old_flags, flags, snap.flyhack, snap.hacks, scale);
        LogLine(msg);
    }
}

void ProxyGetConfig(MovementConfig* out)
{
    if (!out)
        return;
    memcpy(out, (const void*)&g_config, sizeof(*out));
}

int ProxySetConfig(const MovementConfig* in)
{
    MovementConfig cfg;
    int was_enabled;
    if (!in || in->magic != MOVE_CONFIG_MAGIC)
        return 0;
    memset(&cfg, 0, sizeof(cfg));
    memcpy(&cfg, in, sizeof(cfg));
    was_enabled = g_config.enabled ? 1 : 0;
    if (cfg.enabled) {
        if (cfg.inject_mode < 5u)
            cfg.opcode = kDefaultMoveOpcode;
        else if (cfg.opcode == 0)
            cfg.opcode = kDefaultMoveOpcode;
        cfg.flags = 0;
        cfg.flags2 = 0;
        cfg.no_zclip = 1;
    }
    if (cfg.speed_scale < kSpeedScaleMin)
        cfg.speed_scale = kSpeedScaleMin;
    if (cfg.speed_scale > kSpeedScaleMax)
        cfg.speed_scale = kSpeedScaleMax;
    memcpy((void*)&g_config, &cfg, sizeof(cfg));
    if (cfg.enabled) {

        ProxyTpLock(cfg.world_x, cfg.world_y, cfg.world_z, cfg.facing, 500u, 28.f);
        ProxyTeleportSafe(cfg.world_x, cfg.world_y, cfg.world_z, cfg.facing,
            cfg.packets_only ? (kTpSkipGround | 2u) : kTpSkipGround);
        LogLine("SetConfig: enabled — TpLock + EE armed");
    } else if (was_enabled) {
        ProxyTpUnlock();
        LogLine("SetConfig: disarmed — TpUnlock");
    }
    if (!cfg.flyhack)
        InterlockedExchange(&g_fly_airborne, 0);
    return 1;
}

float ProxySetSpeed(float scale, uint32_t speed_cheat)
{
    if (scale < kSpeedScaleMin)
        scale = kSpeedScaleMin;
    if (scale > kSpeedScaleMax)
        scale = kSpeedScaleMax;
    g_config.speed_scale = scale;
    g_config.speed_cheat = speed_cheat ? 1u : 0u;
    if (speed_cheat) {
        uint8_t buf[8], payload[8];
        uint32_t op = kOpcodeMoveSetRunSpeedCheat;
        uint32_t op2 = kOpcodeMoveSetAllSpeedCheat;
        float run = kBaseRunSpeed * scale;
        memcpy(buf + 0, &op, 4);
        memcpy(buf + 4, &run, 4);
        memcpy(payload + 0, &op2, 4);
        memcpy(payload + 4, &run, 4);
        InjectClientPacket(buf, 8);
        InjectClientPacket(payload, 8);
        LogLine("SetSpeed: cheat packets queued (server GM may be required)");
    }
    {
        char b[96];
        _snprintf(b, sizeof(b), "SetSpeed scale=%.2f cheat=%u", scale, speed_cheat);
        LogLine(b);
    }
    return scale;
}

uint32_t ProxySetHacks(uint32_t hacks, uint32_t flyhack)
{
    g_config.hacks = hacks;
    g_config.flyhack = flyhack ? 1u : 0u;
    if (!flyhack)
        InterlockedExchange(&g_fly_airborne, 0);
    {
        char b[96];
        _snprintf(b, sizeof(b), "SetHacks bits=0x%X fly=%u", hacks, flyhack);
        LogLine(b);
    }
    return g_config.hacks;
}

int ProxyFindOpcode(const char* name, uint32_t* out_op)
{
    typedef const char*(__cdecl* OpcodeToNameFn)(uint32_t op);
    OpcodeToNameFn fn;
    uint32_t i;
    if (!name || !name[0] || !out_op || !g_real_base)
        return 0;
    fn = (OpcodeToNameFn)(g_real_base + g_off.ext_opcode_to_name);
    for (i = 0; i <= kMaxOpcode; ++i) {
        const char* n = fn(i);
        if (n && _stricmp(n, name) == 0) {
            *out_op = i;
            return 1;
        }
    }
    return 0;
}

static int PacketHasMoveQuad(const uint8_t* p, uint32_t len, uint32_t* out_off)
{
    uint32_t off = g_move_quad_off;
    float q[4];
    if (!p || len < 20)
        return 0;
    if (off && off + 16u <= len) {
        memcpy(q, p + off, 16);
        if (q[0] == q[0] && fabsf(q[0]) > 1.f && fabsf(q[0]) < 20000.f
            && fabsf(q[1]) > 1.f && fabsf(q[1]) < 20000.f
            && q[2] > -2500.f && q[2] < 4500.f) {
            if (out_off) *out_off = off;
            return 1;
        }
    }

    if (len >= 34u) {
        off = 18u;
        memcpy(q, p + off, 16);
        if (q[0] == q[0] && fabsf(q[0]) > 1.f && fabsf(q[0]) < 20000.f
            && fabsf(q[1]) > 1.f && fabsf(q[1]) < 20000.f
            && q[2] > -2500.f && q[2] < 4500.f) {
            if (out_off) *out_off = off;
            return 1;
        }
    }
    return 0;
}

static int ApplyTpLockToPacket(uint8_t* p, uint32_t len, int inbound)
{
    uint32_t op, off = 0;
    float q[4];
    float dx, dy, dist;
    if (!ProxyTpLockActive() || !p || len < 4)
        return 0;
    memcpy(&op, p, 4);
    if (inbound) {

        if (op != kOpcodeMoveTeleport && op != kOpcodeMoveTeleportCheat
            && op != 0x00EEu && !IsMoveFamilyOpcode(op))
            return 0;
    } else {
        if (!IsMoveFamilyOpcode(op))
            return 0;
    }
    if (!PacketHasMoveQuad(p, len, &off))
        return 0;
    memcpy(q, p + off, 16);
    dx = q[0] - g_tp_lock_x;
    dy = q[1] - g_tp_lock_y;
    dist = sqrtf(dx * dx + dy * dy);
    if (dist <= g_tp_lock_radius)
        return 0;

    q[0] = g_tp_lock_x;
    q[1] = g_tp_lock_y;
    q[2] = g_tp_lock_z;
    q[3] = g_tp_lock_o;
    memcpy(p + off, q, 16);
    InterlockedIncrement(&g_tp_lock_rewrites);
    return 1;
}

int ProxyTpPulse(void)
{
    return ProxyTpPulseEx(0);
}

int ProxyTpPulseEx(uint32_t lock_ms)
{
    float o, nx, ny, nz;
    void* self;
    uint32_t pin_ms = (lock_ms > 0u) ? lock_ms : ProxyDefaultTpLockMs();
    if (!ProxyTpLockActive())
        return 0;
    o = g_tp_lock_o;

    /* 1.25 yd step in facing — wakes grid/ADT without a jump hop. */
    nx = g_tp_lock_x + cosf(o) * 1.25f;
    ny = g_tp_lock_y + sinf(o) * 1.25f;
    nz = g_tp_lock_z;
    if (!ProxyTeleportSafeEx(nx, ny, nz, o, kTpSkipGround | kTpSkipJump, pin_ms))
        return 0;
    ProxyTeleportLoadKick(nx, ny, nz, o);

    ProxyTpLock(nx, ny, nz, o, pin_ms, g_tp_lock_radius);
    self = ObjMgrPlayerObject();
    if (self)
        ObjMgrSetPosition(self, nx, ny, nz, o);
    return 1;
}

int ProxyFreeMove(void)
{
    int lock_was = ProxyTpLockActive() ? 1 : 0;
    int cfg_was = 0;
    MovementConfig cfg;
    ProxyGetConfig(&cfg);
    cfg_was = (cfg.no_zclip || cfg.enabled) ? 1 : 0;
    ProxyTpUnlock();
    if (cfg.enabled || cfg.no_zclip) {
        cfg.enabled = 0;
        cfg.no_zclip = 0;
        ProxySetConfig(&cfg);
    }
    ProxyClickToMoveStop();
    return (lock_was << 1) | cfg_was;
}

int ProxyTeleportLoadEx(float x, float y, float z, float o, uint32_t flags, uint32_t lock_ms)
{
    void* self;
    float ground = 0.f;
    char why[96];
    uint32_t pin_ms = (lock_ms > 0u) ? lock_ms : ProxyDefaultTpLockMs();
    /* Keep exact pin XYZ; never MOVE_JUMP. Load kick = heartbeat+face (no FallLand). */
    uint32_t f = flags | kTpSkipGround | kTpSkipJump;

    /* Drop any leftover sticky lock from a prior hop so every LoadEx is clean. */
    ProxyTpUnlock();
    EnsureMoveReady();

    why[0] = '\0';
    if (!ProxyValidateTeleportDest(x, y, z, o, &ground, why, sizeof(why))) {
        char msg[192];
        _snprintf(msg, sizeof(msg),
            "GmTeleport REJECTED (%.1f,%.1f,%.1f o=%.3f): %s ground=%.1f",
            x, y, z, o, why[0] ? why : "invalid", ground);
        LogLine(msg);
        return 0;
    }

    if (!ProxyTeleportSafeEx(x, y, z, o, f, pin_ms))
        return 0;
    ProxyTeleportLoadKick(x, y, z, o);
    ProxyTpPulseEx(pin_ms);
    ProxyTeleportSafeEx(x, y, z, o, f, pin_ms);
    ProxyTeleportLoadKick(x, y, z, o);
    ProxyTpLock(x, y, z, o, pin_ms, 28.f);
    self = ObjMgrPlayerObject();
    if (self)
        ObjMgrSetPosition(self, x, y, z, o);
    {
        char msg[160];
        _snprintf(msg, sizeof(msg),
            "GmTeleport LoadEx OK (%.1f,%.1f,%.1f) kick=heartbeat pulse pin=%ums",
            x, y, z, pin_ms);
        LogLine(msg);
    }
    return 1;
}

int ProxyTeleportAnywhere(float x, float y, float z, float o, uint32_t map_id,
                          uint32_t flags, uint32_t lock_ms)
{
    uint32_t pin_ms = (lock_ms > 0u) ? lock_ms : ProxyDefaultTpLockMs();
    uint32_t here;
    uint32_t f = flags | kTpSkipGround | kTpSkipJump;
    char msg[160];
    void* self;

    if (!TpFinite(x) || !TpFinite(y) || !TpFinite(z))
        return 0;
    if (!TpFinite(o) || fabsf(o) > 100.f || (o != 0.f && fabsf(o) < 1.0e-20f))
        o = 0.f;

    RefreshLastMapFromPlayer();
    here = g_last_map;

    /* Player accounts: CMSG_WORLD_TELEPORT 0x08, CHARM_PORT_CHEAT 0xE0,
     * SET_RAW_POSITION 0xE1 are GM/cheat. Server replies
     * "You do not have permission to perform that function" and does not
     * move. Client XYZ then leaves the object manager empty at dest.
     * Same-map hop = heartbeat + local pose only. */
    if (!ProxyTeleportSafeEx(x, y, z, o, f, pin_ms))
        return 0;
    ProxyTeleportLoadKick(x, y, z, o);
    ProxyTpPulseEx(pin_ms);
    ProxyTeleportSafeEx(x, y, z, o, f, pin_ms);
    ProxyTeleportLoadKick(x, y, z, o);
    ProxyTpLock(x, y, z, o, pin_ms, 28.f);
    self = ObjMgrPlayerObject();
    if (self)
        ObjMgrSetPosition(self, x, y, z, o);

    if (map_id != kMapIdUnknown && map_id == here)
        InterlockedExchange((volatile LONG*)&g_last_map, (LONG)map_id);

    {
        static volatile LONG s_any = 0;
        _snprintf(msg, sizeof(msg),
            "teleport-anywhere: heartbeat (%.1f,%.1f,%.1f) map=%u here=%u",
            x, y, z, map_id, here);
        if ((InterlockedIncrement(&s_any) % 16) == 1)
            LogLine(msg);
    }
    return 1;
}

static uint32_t EnsureMoverGuid(void)
{
    uint32_t guid;
    uint64_t full;
    void* self;
    char msg[112];

    guid = (uint32_t)InterlockedCompareExchange(&g_mover_guid, 0, 0);
    if (guid)
        return guid;

    if (g_move_template_len >= 8) {
        memcpy(&guid, g_move_template + kAscOffGuid, 4);
        if (guid) {
            InterlockedExchange(&g_mover_guid, (LONG)guid);
            return guid;
        }
    }

    full = ObjMgrPlayerGuid();
    if (!full) {
        self = ObjMgrPlayerObject();
        if (self)
            full = ObjMgrObjectGuid(self);
    }
    guid = (uint32_t)(full & 0xFFFFFFFFu);
    if (guid) {
        InterlockedExchange(&g_mover_guid, (LONG)guid);
        _snprintf(msg, sizeof(msg),
            "mover-guid: seeded from player GUID low=0x%X (full=%016llX)",
            guid, (unsigned long long)full);
        LogLine(msg);
        return guid;
    }
    return 0;
}

static int EnsureMoveTemplate(void)
{
    uint32_t guid;
    float x = 0, y = 0, z = 0, o = 0;
    void* self;
    uint32_t opcode = kDefaultMoveOpcode;
    uint32_t flags = 0, t, counter;
    uint16_t f2 = 0;

    if (g_move_template_len >= (LONG)kAscMoveSize && g_move_quad_off != 0)
        return 1;

    guid = EnsureMoverGuid();
    if (!guid)
        return 0;

    self = ObjMgrPlayerObject();
    if (self && ObjMgrPosition(self, &x, &y, &z, &o)) {

    } else if (InterlockedCompareExchange(&g_last_player_valid, 0, 0)) {
        x = g_last_player_x;
        y = g_last_player_y;
        z = g_last_player_z;
        o = g_player_facing;
    } else {
        return 0;
    }

    t = GetTickCount();
    counter = (uint32_t)InterlockedIncrement(&g_move_counter);
    if (counter == 0 || counter >= 0x10000000u) {
        counter = 1u;
        InterlockedExchange(&g_move_counter, 1);
    }

    memset(g_move_template, 0, sizeof(g_move_template));
    memcpy(g_move_template + 0, &opcode, 4);
    memcpy(g_move_template + kAscOffGuid, &guid, 4);
    memcpy(g_move_template + kAscOffFlags, &flags, 4);
    memcpy(g_move_template + kAscOffFlags2, &f2, 2);
    memcpy(g_move_template + kAscOffTime, &t, 4);
    memcpy(g_move_template + kAscOffX, &x, 4);
    memcpy(g_move_template + kAscOffY, &y, 4);
    memcpy(g_move_template + kAscOffZ, &z, 4);
    memcpy(g_move_template + kAscOffO, &o, 4);
    memcpy(g_move_template + kAscOffCounter, &counter, 4);
    InterlockedExchange(&g_move_template_len, (LONG)kAscMoveSize);
    if (!g_move_quad_off)
        g_move_quad_off = kAscOffX;
    LogLine("move-template: synthesized from player pos (no sniff yet)");
    return 1;
}

static void KickLearnMove(void)
{
    static const char kKick[] =
        "if MoveForwardStart then pcall(MoveForwardStart) end; "
        "if JumpOrAscendStart then pcall(JumpOrAscendStart) end; "
        "if C_Timer and C_Timer.After then "
        "C_Timer.After(0.15, function() "
        "if MoveForwardStop then pcall(MoveForwardStop) end; "
        "if JumpOrAscendStop then pcall(JumpOrAscendStop) end end) "
        "elseif MoveForwardStop then pcall(MoveForwardStop) end";
    ProxyRequestRunLua(kKick, (uint32_t)(sizeof(kKick) - 1u));
}

static int EnsureMoveReady(void)
{
    uint32_t guid = EnsureMoverGuid();
    int tmpl = EnsureMoveTemplate();
    if (!guid) {
        KickLearnMove();
        guid = EnsureMoverGuid();
        tmpl = EnsureMoveTemplate();
    } else if (!tmpl) {
        KickLearnMove();
        tmpl = EnsureMoveTemplate();
    }
    (void)tmpl;
    return (guid != 0) ? 1 : 0;
}

static int ProxyInjectWalkStep(float x, float y, float z, float o)
{

    uint8_t buf[64];
    uint32_t opcode = 0xEEu;
    uint32_t guid, flags, counter, t;
    uint16_t f2 = 0;
    void* self;
    char msg[144];


    if (InterlockedCompareExchange(&g_tp_lock, 0, 0))
        ProxyTpUnlock();

    guid = EnsureMoverGuid();
    if (!guid) {
        KickLearnMove();
        guid = EnsureMoverGuid();
    }
    if (!guid) {
        LogLine("walk-step: no player GUID yet (not in world?)");
        return 0;
    }
    EnsureMoveTemplate();

    counter = (uint32_t)InterlockedIncrement(&g_move_counter);
    if (counter == 0 || counter >= 0x10000000u) {
        counter = 1u;
        InterlockedExchange(&g_move_counter, 1);
    }
    t = GetTickCount();
    flags = kMoveFlagForward;

    memset(buf, 0, sizeof(buf));
    memcpy(buf + 0, &opcode, 4);
    memcpy(buf + kAscOffGuid, &guid, 4);
    memcpy(buf + kAscOffFlags, &flags, 4);
    memcpy(buf + kAscOffFlags2, &f2, 2);
    memcpy(buf + kAscOffTime, &t, 4);
    memcpy(buf + kAscOffX, &x, 4);
    memcpy(buf + kAscOffY, &y, 4);
    memcpy(buf + kAscOffZ, &z, 4);
    memcpy(buf + kAscOffO, &o, 4);
    memcpy(buf + kAscOffCounter, &counter, 4);

    if (!InjectClientPacket(buf, kAscMoveSize)) {
        LogLine("walk-step: InjectClientPacket FAILED (no net client?)");
        return 0;
    }

    self = ObjMgrPlayerObject();
    if (self)
        ObjMgrSetPosition(self, x, y, z, o);
    g_last_player_x = x;
    g_last_player_y = y;
    g_last_player_z = z;
    g_player_facing = o;
    InterlockedExchange(&g_facing_valid, 1);
    InterlockedExchange(&g_last_player_valid, 1);

    {
        static DWORD s_last_log = 0;
        DWORD n = GetTickCount();
        if (n - s_last_log > 1000u) {
            s_last_log = n;
            _snprintf(msg, sizeof(msg),
                "walk-step: guid=0x%X (%.1f,%.1f,%.1f) o=%.2f", guid, x, y, z, o);
            LogLine(msg);
        }
    }
    return 1;
}
int ProxySetMove(uint32_t op, float duration_s)
{
    if (op != kMoveOpStop && !EnsureMoveReady())
        return 0;
    if (op == kMoveOpStop) {
        InterlockedExchange(&g_move_op, kMoveOpStop);
        InterlockedExchange((volatile LONG*)&g_move_until, 0);
        return 1;
    }

    if (InterlockedCompareExchange(&g_tp_lock, 0, 0))
        ProxyTpUnlock();
    if (op == kMoveOpJump) {

        void* self = ObjMgrPlayerObject();
        float x = 0, y = 0, z = 0, o = 0;
        if (self && ObjMgrPosition(self, &x, &y, &z, &o))
            ProxyTeleportSafe(x, y, z, o, kTpSkipGround);
        return 1;
    }

    if (op == kMoveOpForward || op == kMoveOpBackward) {
        void* self = ObjMgrPlayerObject();
        float x = 0, y = 0, z = 0;
        if (self && ObjMgrPosition(self, &x, &y, &z, NULL)) {
            float f = g_player_facing;
            float reach = 50.f;
            float tx, ty;
            if (op == kMoveOpBackward) f += 3.1415927f;
            tx = x + cosf(f) * reach;
            ty = y + sinf(f) * reach;
            InterlockedExchange((volatile LONG*)&g_move_target_x, *(LONG*)&tx);
            InterlockedExchange((volatile LONG*)&g_move_target_y, *(LONG*)&ty);
            InterlockedExchange((volatile LONG*)&g_move_target_z, *(LONG*)&z);
        }
        InterlockedExchange(&g_move_op, (LONG)op);
        InterlockedExchange((volatile LONG*)&g_move_until, GetTickCount() + (DWORD)(duration_s * 1000.f));
        return 1;
    }
    return 0;
}

int ProxyClickToMove(float x, float y, float z)
{
    void* self;
    float px = 0.f, py = 0.f, pz = 0.f;
    float dist, dur;
    char msg[160];
    int have_pos = 0;
    int stepped = 0;
    uint32_t guid;

    if (!EnsureMoveReady()) {
        LogLine("ClickToMove: not ready (no player GUID / not in world)");
        return 0;
    }
    guid = EnsureMoverGuid();


    if (InterlockedCompareExchange(&g_tp_lock, 0, 0)) {
        LogLine("ClickToMove: clearing sticky TpLock (AscensionNetTool DisarmConfig)");
        ProxyTpUnlock();
    }


    {
        static const char kCtmCvars[] =
            "if SetCVar then "
            "pcall(SetCVar,'autointeract','1'); "
            "pcall(SetCVar,'enableMoveToMouse','1'); "
            "pcall(SetCVar,'SoftTargetInteract','3'); "
            "end";
        ProxyRequestRunLua(kCtmCvars, (uint32_t)(sizeof(kCtmCvars) - 1u));
    }

    self = ObjMgrPlayerObject();
    if (self && ObjMgrPosition(self, &px, &py, &pz, NULL)) {
        have_pos = 1;
        g_last_player_x = px; g_last_player_y = py; g_last_player_z = pz;
        InterlockedExchange(&g_last_player_valid, 1);
    } else if (InterlockedCompareExchange(&g_last_player_valid, 0, 0)) {
        px = g_last_player_x; py = g_last_player_y; pz = g_last_player_z;
        have_pos = 1;
    }

    if (have_pos) {
        float dx = x - px, dy = y - py;
        dist = sqrtf(dx * dx + dy * dy);
    } else {
        dist = 40.f;
    }

    dur = (dist / EffectiveRunSpeed()) + 3.f;
    if (dur < 2.f) dur = 2.f;
    if (dur > 180.f) dur = 180.f;

    InterlockedExchange((volatile LONG*)&g_move_target_x, *(LONG*)&x);
    InterlockedExchange((volatile LONG*)&g_move_target_y, *(LONG*)&y);
    InterlockedExchange((volatile LONG*)&g_move_target_z, *(LONG*)&z);
    InterlockedExchange(&g_move_op, kMoveOpForward);
    InterlockedExchange((volatile LONG*)&g_move_until,
        GetTickCount() + (DWORD)(dur * 1000.f));
    InterlockedExchange(&g_move_tick, 0);

    _snprintf(msg, sizeof(msg),
        "ClickToMove -> (%.1f,%.1f,%.1f) dist=%.1f dur=%.1fs guid=0x%X",
        x, y, z, dist, dur, guid);
    LogLine(msg);

    if (have_pos) {
        float dx = x - px, dy = y - py, d2 = sqrtf(dx * dx + dy * dy);
        float step = EffectiveRunSpeed() * ((float)kMoveStepMs / 1000.f);
        float nx, ny, nz, ang;
        uint32_t map;
        if (d2 > 0.5f) {
            if (step > d2) step = d2;
            nx = px + dx / d2 * step;
            ny = py + dy / d2 * step;
            ang = atan2f(y - py, x - px);
            if (ang < 0.f) ang += 6.2831853f;
            map = ResolveNavMap(0u, nx, ny, pz);
            if (!NavHeightAt(map, nx, ny, pz, &nz))
                nz = pz;
            if (fabsf(nz - pz) > 40.f)
                nz = pz;
            stepped = ProxyInjectWalkStep(nx, ny, nz, ang);
            if (stepped)
                InterlockedExchange(&g_move_tick, GetTickCount());
            else
                LogLine("ClickToMove: first walk-step FAILED");
        } else {
            InterlockedExchange(&g_move_op, kMoveOpStop);
            LogLine("ClickToMove: already at destination");
        }
    } else {
        LogLine("ClickToMove: no player pos — pump will start when OM/cache available");
    }
    return 1;
}

int ProxyClickToMoveStop(void)
{
    return ProxySetMove(kMoveOpStop, 0.f);
}

int ProxyMoveStatus(int* ready, int* moving, float* tx, float* ty, float* tz, uint32_t* remain_ms)
{
    DWORD now, until;
    LONG op;
    if (ready) {

        *ready = (EnsureMoverGuid() != 0) ? 1 : 0;
    }
    op = InterlockedCompareExchange(&g_move_op, 0, 0);
    if (moving)
        *moving = (op != kMoveOpStop) ? 1 : 0;
    if (tx) *tx = g_move_target_x;
    if (ty) *ty = g_move_target_y;
    if (tz) *tz = g_move_target_z;
    if (remain_ms) {
        now = GetTickCount();
        until = (DWORD)InterlockedCompareExchange((volatile LONG*)&g_move_until, 0, 0);
        if (op == kMoveOpStop || until <= now)
            *remain_ms = 0;
        else
            *remain_ms = until - now;
    }
    return 1;
}

void ProxyPumpMove(void)
{
    LONG op;
    DWORD now, last;
    float px, py, pz, po;
    void* self;
    float dx, dy, dist, step, nx, ny, nz, ang;
    uint32_t map;

    op = InterlockedCompareExchange(&g_move_op, 0, 0);
    if (op == kMoveOpStop)
        return;

    (void)EnsureMoverGuid();
    (void)EnsureMoveTemplate();
    now = GetTickCount();
    if (now >= (DWORD)InterlockedCompareExchange((volatile LONG*)&g_move_until, 0, 0)) {
        InterlockedExchange(&g_move_op, kMoveOpStop);
        return;
    }
    last = (DWORD)InterlockedCompareExchange(&g_move_tick, 0, 0);
    if (now - last < kMoveStepMs)
        return;
    InterlockedExchange(&g_move_tick, now);

    self = ObjMgrPlayerObject();
    if (self && ObjMgrPosition(self, &px, &py, &pz, &po)) {
        g_last_player_x = px; g_last_player_y = py; g_last_player_z = pz;
        InterlockedExchange(&g_last_player_valid, 1);
    } else if (InterlockedCompareExchange(&g_last_player_valid, 0, 0)) {
        px = g_last_player_x;
        py = g_last_player_y;
        pz = g_last_player_z;
        po = g_player_facing;
    } else {
        return;
    }

    dx = g_move_target_x - px;
    dy = g_move_target_y - py;
    dist = sqrtf(dx * dx + dy * dy);
    if (dist < 0.5f) {
        InterlockedExchange(&g_move_op, kMoveOpStop);
        return;
    }
    step = EffectiveRunSpeed() * ((float)kMoveStepMs / 1000.f);
    if (step > dist) step = dist;
    if (op == kMoveOpBackward) { dx = -dx; dy = -dy; }
    nx = px + dx / dist * step;
    ny = py + dy / dist * step;
    ang = atan2f(g_move_target_y - py, g_move_target_x - px);
    if (ang < 0.f) ang += 6.2831853f;


    map = ResolveNavMap(0u, nx, ny, pz);
    if (!NavHeightAt(map, nx, ny, pz, &nz))
        nz = pz;

    if (fabsf(nz - pz) > 40.f)
        nz = pz;

    if (ProxyInjectWalkStep(nx, ny, nz, ang)) {

    }
}

static int __cdecl GmTeleportValidate_Lua(void* L)
{
    float x = (float)LuaArgNum(L, 1);
    float y = (float)LuaArgNum(L, 2);
    float z = (float)LuaArgNum(L, 3);
    float o = (float)LuaArgNum(L, 4);
    float ground = 0.f;
    char why[96];
    why[0] = '\0';
    if (ProxyValidateTeleportDest(x, y, z, o, &ground, why, sizeof(why))) {
        LuaPushNum(L, 1.0);
        LuaPushNum(L, (double)ground);
        return 2;
    }
    LuaPushNum(L, 0.0);
    LuaPushStr(L, why[0] ? why : "invalid");
    return 2;
}

/* Multi-instance teleport mirror — leader publishes pose + optional combat GUID. */
static int __cdecl GmMirrorPublish_Lua(void* L)
{
    float x = (float)LuaArgNum(L, 1);
    float y = (float)LuaArgNum(L, 2);
    float z = (float)LuaArgNum(L, 3);
    float o = (float)LuaArgNum(L, 4);
    uint32_t map = (uint32_t)LuaArgNum(L, 5);
    uint32_t flags = (uint32_t)LuaArgNum(L, 6);
    uint64_t combat = 0;
    uint32_t pubInst = 0;
    uint32_t seq;
    /* Optional arg7: combat GUID as hex string (preferred) or number. */
    {
        const char* gs = LuaArgStr(L, 7);
        if (gs && gs[0])
            combat = ParseHexGuid(gs);
        else {
            double n = LuaArgNum(L, 7);
            if (n > 0.0) combat = (uint64_t)n;
        }
    }
    pubInst = (uint32_t)LuaArgNum(L, 8);
    if (map == 0)
        map = g_last_map;
    seq = TeleMirrorPublishEx(map, x, y, z, o, flags, combat, pubInst);
    LuaPushNum(L, (double)seq);
    return 1;
}

static int __cdecl GmMirrorPoll_Lua(void* L)
{
    TeleMirrorSlot s;
    char gbuf[24];
    if (!TeleMirrorPeek(&s) || s.seq == 0) {
        LuaPushNum(L, 0.0);
        return 1;
    }
    LuaPushNum(L, (double)s.seq);
    LuaPushNum(L, (double)s.map);
    LuaPushNum(L, (double)s.x);
    LuaPushNum(L, (double)s.y);
    LuaPushNum(L, (double)s.z);
    LuaPushNum(L, (double)s.o);
    LuaPushNum(L, (double)s.leader_pid);
    LuaPushNum(L, (double)s.flags);
    /* 9: combat guid hex string (empty if none) */
    if (s.combat_guid) {
        _snprintf(gbuf, sizeof(gbuf), "0x%016llX", (unsigned long long)s.combat_guid);
        LuaPushStr(L, gbuf);
    } else {
        LuaPushStr(L, "");
    }
    /* 10: publisher instance id */
    LuaPushNum(L, (double)s.publisher_instance);
    return 10;
}

static int __cdecl GmMirrorSeq_Lua(void* L)
{
    LuaPushNum(L, (double)TeleMirrorSeq());
    return 1;
}

static int __cdecl GmTeleport_Lua(void* L)
{
    float x = (float)LuaArgNum(L, 1);
    float y = (float)LuaArgNum(L, 2);
    float z = (float)LuaArgNum(L, 3);
    float o = (float)LuaArgNum(L, 4);
    uint32_t flags = (uint32_t)LuaArgNum(L, 5);
    uint32_t lock_ms = (uint32_t)LuaArgNum(L, 6);
    ForceClearTaint();
    LuaPushNum(L, ProxyTeleportLoadEx(x, y, z, o, flags, lock_ms) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmTeleportRaw_Lua(void* L)
{
    float x = (float)LuaArgNum(L, 1);
    float y = (float)LuaArgNum(L, 2);
    float z = (float)LuaArgNum(L, 3);
    float o = (float)LuaArgNum(L, 4);
    uint32_t flags = (uint32_t)LuaArgNum(L, 5);
    uint32_t lock_ms = (uint32_t)LuaArgNum(L, 6);
    ForceClearTaint();
    LuaPushNum(L, ProxyTeleportSafeEx(x, y, z, o, flags, lock_ms) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmTeleportAnywhere_Lua(void* L)
{
    float x = (float)LuaArgNum(L, 1);
    float y = (float)LuaArgNum(L, 2);
    float z = (float)LuaArgNum(L, 3);
    float o = (float)LuaArgNum(L, 4);
    double map_arg = LuaArgNum(L, 5);
    uint32_t flags = (uint32_t)LuaArgNum(L, 6);
    uint32_t lock_ms = (uint32_t)LuaArgNum(L, 7);
    uint32_t map_id = kMapIdUnknown;

    if (map_arg >= 0.0 && map_arg < 2147483647.0)
        map_id = (uint32_t)map_arg;
    ForceClearTaint();
    LuaPushNum(L, ProxyTeleportAnywhere(x, y, z, o, map_id, flags, lock_ms) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmTpLock_Lua(void* L)
{
    float x = (float)LuaArgNum(L, 1);
    float y = (float)LuaArgNum(L, 2);
    float z = (float)LuaArgNum(L, 3);
    float o = (float)LuaArgNum(L, 4);
    uint32_t ms = (uint32_t)LuaArgNum(L, 5);
    float rad = (float)LuaArgNum(L, 6);

    if (ms == 0) ms = ProxyDefaultTpLockMs();
    if (rad < 1.f) rad = 28.f;
    ProxyTpLock(x, y, z, o, ms, rad);
    LuaPushNum(L, 1.0);
    return 1;
}

static int __cdecl GmTpUnlock_Lua(void* L)
{
    ProxyTpUnlock();
    LuaPushNum(L, 1.0);
    return 1;
}

static int __cdecl GmFreeMove_Lua(void* L)
{
    int packed = ProxyFreeMove();
    ForceClearTaint();
    LuaPushNum(L, 1.0);
    LuaPushNum(L, (packed & 2) ? 1.0 : 0.0);
    LuaPushNum(L, (packed & 1) ? 1.0 : 0.0);
    return 3;
}

static int __cdecl GmTpPulse_Lua(void* L)
{
    LuaPushNum(L, ProxyTpPulse() ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmTpLockActive_Lua(void* L)
{
    LuaPushNum(L, ProxyTpLockActive() ? 1.0 : 0.0);
    return 1;
}

static int __cdecl ClickToMove_Lua(void* L)
{
    float x = (float)LuaArgNum(L, 1);
    float y = (float)LuaArgNum(L, 2);
    float z = (float)LuaArgNum(L, 3);

    ForceClearTaint();
    LuaPushNum(L, ProxyClickToMove(x, y, z) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl ClickToMoveStop_Lua(void* L)
{
    LuaPushNum(L, ProxyClickToMoveStop() ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmSetMove_Lua(void* L)
{
    uint32_t op = (uint32_t)LuaArgNum(L, 1);
    float dur = (float)LuaArgNum(L, 2);
    if (op != kMoveOpStop && dur <= 0.f)
        dur = 1.0f;
    LuaPushNum(L, ProxySetMove(op, dur) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmMoveStatus_Lua(void* L)
{
    int ready = 0, moving = 0;
    float tx = 0, ty = 0, tz = 0;
    uint32_t rem = 0;
    ProxyMoveStatus(&ready, &moving, &tx, &ty, &tz, &rem);
    LuaPushNum(L, (double)ready);
    LuaPushNum(L, (double)moving);
    LuaPushNum(L, (double)tx);
    LuaPushNum(L, (double)ty);
    LuaPushNum(L, (double)tz);
    LuaPushNum(L, (double)rem);
    return 6;
}

static int __cdecl GmClearTaint_Lua(void* L)
{
    ForceClearTaint();
    LuaPushNum(L, 1.0);
    return 1;
}

static int __cdecl GmFlyhack_Lua(void* L)
{
    uint32_t on = (uint32_t)LuaArgNum(L, 1) ? 1u : 0u;
    ProxySetHacks(g_config.hacks, on);
    LuaPushNum(L, (double)g_config.flyhack);
    return 1;
}

static int __cdecl GmNoclip_Lua(void* L)
{
    uint32_t on = (uint32_t)LuaArgNum(L, 1) ? 1u : 0u;
    uint32_t h = g_config.hacks;
    if (on)
        h |= kHackNoclip;
    else
        h &= ~kHackNoclip;
    ProxySetHacks(h, g_config.flyhack);
    LuaPushNum(L, (h & kHackNoclip) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmNoFall_Lua(void* L)
{
    uint32_t on = (uint32_t)LuaArgNum(L, 1) ? 1u : 0u;
    uint32_t h = g_config.hacks;
    if (on)
        h |= kHackNoFall | kHackHover | kHackWaterwalk;
    else
        h &= ~kHackNoFall;
    ProxySetHacks(h, g_config.flyhack);
    LuaPushNum(L, (h & kHackNoFall) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmHackBit_Lua(void* L)
{
    uint32_t bit = (uint32_t)LuaArgNum(L, 1);
    uint32_t on = (uint32_t)LuaArgNum(L, 2) ? 1u : 0u;
    uint32_t h = g_config.hacks;
    if (!bit)
        bit = kHackNoFall;
    if (on)
        h |= bit;
    else
        h &= ~bit;
    ProxySetHacks(h, g_config.flyhack);
    LuaPushNum(L, (h & bit) ? 1.0 : 0.0);
    return 1;
}

/* GmSetNamePlateRange(yards) — Extensions.dll FrameScript SetNamePlateRange @ RVA 0x2C3160
 * (+ CVar nameplateDistance). Auto-rescannable via string→imm32 registration. */
static int __cdecl GmSetNamePlateRange_Lua(void* L)
{
    double range = LuaArgNum(L, 1);
    char script[192];
    if (range < 1.0)
        range = 1.0;
    if (range > 100.0)
        range = 100.0;
    _snprintf(script, sizeof(script),
        "if type(SetNamePlateRange)=='function' then pcall(SetNamePlateRange,%.2f) end "
        "if type(SetCVar)=='function' then pcall(SetCVar,'nameplateDistance',%.2f) end",
        range, range);
    RunFrameScriptExecute(script);
    LuaPushNum(L, range);
    return 1;
}

static int __cdecl GmGetNamePlateRange_Lua(void* L)
{
    /* CVar read is Lua-side; native returns 1 to signal “use GetCVar('nameplateDistance')”. */
    LuaPushNum(L, 1.0);
    return 1;
}

static int __cdecl GmSetHacks_Lua(void* L)
{
    uint32_t bits = (uint32_t)LuaArgNum(L, 1);

    LuaPushNum(L, (double)ProxySetHacks(bits, g_config.flyhack));
    return 1;
}

/* Lua: local h, fly, spd = GmGetHacks()  — bits, flyhack flag, speed_scale */
static int __cdecl GmGetHacks_Lua(void* L)
{
    LuaPushNum(L, (double)g_config.hacks);
    LuaPushNum(L, (double)g_config.flyhack);
    LuaPushNum(L, (double)g_config.speed_scale);
    return 3;
}

static int __cdecl GmSpeed_Lua(void* L)
{
    float scale = (float)LuaArgNum(L, 1);
    uint32_t cheat = (uint32_t)LuaArgNum(L, 2) ? 1u : 0u;
    if (scale <= 0.f)
        scale = 1.f;
    LuaPushNum(L, (double)ProxySetSpeed(scale, cheat));
    return 1;
}

static int __cdecl GmWaterwalk_Lua(void* L)
{
    uint32_t on = (uint32_t)LuaArgNum(L, 1) ? 1u : 0u;
    uint32_t h = g_config.hacks;
    if (on)
        h |= kHackWaterwalk;
    else
        h &= ~kHackWaterwalk;
    ProxySetHacks(h, g_config.flyhack);
    LuaPushNum(L, (h & kHackWaterwalk) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmFindOpcode_Lua(void* L)
{

    const char* name = LuaArgStr(L, 1);
    uint32_t op = 0;
    if (!name || !ProxyFindOpcode(name, &op))
        return 0;
    LuaPushNum(L, (double)op);
    return 1;
}

static int __cdecl GmIsExploredBit_Lua(void* L)
{
    uint32_t bit = (uint32_t)LuaArgNum(L, 1);
    LuaPushNum(L, (double)FogIsExploredBit(bit));
    return 1;
}

static int __cdecl GmAreaBit_Lua(void* L)
{
    uint32_t area = (uint32_t)LuaArgNum(L, 1);
    uint32_t bit = 0;
    if (!FogAreaBit(area, &bit)) {
        LuaPushNum(L, -1.0);
        return 1;
    }
    LuaPushNum(L, (double)bit);
    return 1;
}

static int __cdecl GmIsAreaExplored_Lua(void* L)
{
    uint32_t area = (uint32_t)LuaArgNum(L, 1);
    LuaPushNum(L, (double)FogIsAreaExplored(area));
    return 1;
}

static int __cdecl GmExploredWord_Lua(void* L)
{
    uint32_t idx = (uint32_t)LuaArgNum(L, 1);
    LuaPushNum(L, (double)FogExploredWord(idx));
    return 1;
}

static int __cdecl GmDrawClear_Lua(void* L)
{
    (void)L;
    Overlay_Clear();
    LuaPushNum(L, 1.0);
    return 1;
}

static int __cdecl GmDrawBeginFrame_Lua(void* L)
{
    (void)L;
    Overlay_BeginFrame();
    LuaPushNum(L, 1.0);
    return 1;
}

static int __cdecl GmDrawEndFrame_Lua(void* L)
{
    (void)L;
    Overlay_EndFrame();
    LuaPushNum(L, 1.0);
    return 1;
}

static int __cdecl GmDrawSetHz_Lua(void* L)
{
    int hz = (int)LuaArgNum(L, 1);
    if (hz <= 0) hz = 30;
    Overlay_SetUpdateHz(hz);
    LuaPushNum(L, (double)Overlay_GetUpdateHz());
    return 1;
}

static int __cdecl GmDrawLine_Lua(void* L)
{
    int ok = Overlay_AddLine(
        (float)LuaArgNum(L, 1), (float)LuaArgNum(L, 2),
        (float)LuaArgNum(L, 3), (float)LuaArgNum(L, 4),
        Overlay_ColorByte(LuaArgNum(L, 5)),
        Overlay_ColorByte(LuaArgNum(L, 6)),
        Overlay_ColorByte(LuaArgNum(L, 7)),
        Overlay_ColorByte(LuaArgNum(L, 8) > 0 ? LuaArgNum(L, 8) : 1.0));
    LuaPushNum(L, ok ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmDrawRect_Lua(void* L)
{
    int ok = Overlay_AddRect(
        (float)LuaArgNum(L, 1), (float)LuaArgNum(L, 2),
        (float)LuaArgNum(L, 3), (float)LuaArgNum(L, 4),
        Overlay_ColorByte(LuaArgNum(L, 5)),
        Overlay_ColorByte(LuaArgNum(L, 6)),
        Overlay_ColorByte(LuaArgNum(L, 7)),
        Overlay_ColorByte(LuaArgNum(L, 8) > 0 ? LuaArgNum(L, 8) : 1.0));
    LuaPushNum(L, ok ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmDrawCircle_Lua(void* L)
{
    int ok = Overlay_AddCircle(
        (float)LuaArgNum(L, 1), (float)LuaArgNum(L, 2), (float)LuaArgNum(L, 3),
        Overlay_ColorByte(LuaArgNum(L, 4)),
        Overlay_ColorByte(LuaArgNum(L, 5)),
        Overlay_ColorByte(LuaArgNum(L, 6)),
        Overlay_ColorByte(LuaArgNum(L, 7) > 0 ? LuaArgNum(L, 7) : 1.0));
    LuaPushNum(L, ok ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmDrawText_Lua(void* L)
{
    const char* text = LuaArgStr(L, 3);
    double r = LuaArgNum(L, 4), g = LuaArgNum(L, 5), b = LuaArgNum(L, 6), a = LuaArgNum(L, 7);
    if (r == 0 && g == 0 && b == 0 && a == 0) { r = 1; g = 1; b = 0.3; a = 1; }
    int ok = Overlay_AddText(
        (float)LuaArgNum(L, 1), (float)LuaArgNum(L, 2), text ? text : "",
        Overlay_ColorByte(r), Overlay_ColorByte(g), Overlay_ColorByte(b),
        Overlay_ColorByte(a > 0 ? a : 1.0));
    LuaPushNum(L, ok ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmDrawWorldLine_Lua(void* L)
{
    double r = LuaArgNum(L, 7), g = LuaArgNum(L, 8), b = LuaArgNum(L, 9), a = LuaArgNum(L, 10);
    if (r == 0 && g == 0 && b == 0 && a == 0) { r = 0.3; g = 0.86; b = 0.47; a = 0.86; }
    int ok = Overlay_AddWorldLine(
        (float)LuaArgNum(L, 1), (float)LuaArgNum(L, 2), (float)LuaArgNum(L, 3),
        (float)LuaArgNum(L, 4), (float)LuaArgNum(L, 5), (float)LuaArgNum(L, 6),
        Overlay_ColorByte(r), Overlay_ColorByte(g), Overlay_ColorByte(b),
        Overlay_ColorByte(a > 0 ? a : 1.0));
    LuaPushNum(L, ok ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmDrawWorldBox_Lua(void* L)
{
    float half = (float)LuaArgNum(L, 4);
    float height = (float)LuaArgNum(L, 5);
    double r = LuaArgNum(L, 6), g = LuaArgNum(L, 7), b = LuaArgNum(L, 8), a = LuaArgNum(L, 9);
    int ok;
    if (half <= 0.f) half = 0.6f;
    if (height <= 0.f) height = 2.0f;
    if (r == 0 && g == 0 && b == 0 && a == 0) { r = 1; g = 0.3; b = 0.3; a = 0.78; }
    ok = Overlay_AddWorldBox(
        (float)LuaArgNum(L, 1), (float)LuaArgNum(L, 2), (float)LuaArgNum(L, 3),
        half, height,
        Overlay_ColorByte(r), Overlay_ColorByte(g), Overlay_ColorByte(b),
        Overlay_ColorByte(a > 0 ? a : 1.0));
    LuaPushNum(L, ok ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmWorldToScreen_Lua(void* L)
{
    float sx = 0.f, sy = 0.f;
    int ok = Overlay_WorldToScreen(
        (float)LuaArgNum(L, 1), (float)LuaArgNum(L, 2), (float)LuaArgNum(L, 3),
        &sx, &sy);
    if (!ok) return 0;
    LuaPushNum(L, (double)sx);
    LuaPushNum(L, (double)sy);
    return 2;
}

static int __cdecl GmOverlayReady_Lua(void* L)
{
    LuaPushNum(L, Overlay_Ready() ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmOverlayStats_Lua(void* L)
{
    LuaPushNum(L, (double)Overlay_FrameCount());
    LuaPushNum(L, (double)Overlay_DrawCount());
    LuaPushNum(L, (double)Overlay_GetUpdateHz());
    LuaPushNum(L, (double)Overlay_LootEspCount());
    return 4;
}

static int __cdecl GmLootEsp_Lua(void* L)
{
    const char* s = LuaArgStr(L, 1);
    if (s && s[0])
        Overlay_SetLootEsp(LuaArgNum(L, 1) >= 0.5 ? 1 : 0);
    LuaPushNum(L, Overlay_GetLootEsp() ? 1.0 : 0.0);
    LuaPushNum(L, (double)Overlay_LootEspCount());
    LuaPushNum(L, (double)Overlay_GetLootEspRadius());
    return 3;
}

static int __cdecl GmLootEspRadius_Lua(void* L)
{
    const char* s = LuaArgStr(L, 1);
    if (s && s[0]) {
        double v = LuaArgNum(L, 1);
        if (v > 1.0)
            Overlay_SetLootEspRadius((float)v);
    }
    LuaPushNum(L, (double)Overlay_GetLootEspRadius());
    return 1;
}

static int __cdecl GmDrawCmd_Lua(void* L)
{
    const char* cmd = LuaArgStr(L, 1);
    int ok = Overlay_ParseDrawCommand(cmd);
    LuaPushNum(L, ok ? 1.0 : 0.0);
    return 1;
}

#include "TeleportNg.inc.c"
#include "GmActions.inc.c"
#include "GmApiExt.inc.c"
#include "QueryCache.inc.c"

static void RegisterLuaApis(void)
{
    RegisterFunctionFn reg;
    DWORD now = GetTickCount();
    if (!g_ascension)
        return;
    if (g_lua_api_tick && (now - g_lua_api_tick) < kLuaApiReseedMs)
        return;
    /* RegisterFunction loads L from [ImageBase+kLuaStatePtrRva]; L==NULL → AV at 0x44E408. */
    if (!ProxyLuaState())
        return;
    g_lua_api_tick = now;
    reg = (RegisterFunctionFn)(g_ascension + kRegisterFunctionRva);
    LogLine("lua APIs: RegisterFunction begin");
    reg("GmLoS", (void*)GmLoS_Lua);
    reg("GmNavZ", (void*)GmNavZ_Lua);
    reg("GmMapId", (void*)GmMapId_Lua);
    reg("GmTarget", (void*)GmTarget_Lua);
    reg("GmTargetUnit", (void*)GmTargetUnit_Lua);
    reg("GmTargetGuid", (void*)GmTargetGuid_Lua);
    reg("GmClearTarget", (void*)GmClearTarget_Lua);
    reg("GmInteractUnit", (void*)GmInteractUnit_Lua);
    reg("GmLookAt", (void*)GmLookAt_Lua);
    reg("GmRightClick", (void*)GmRightClick_Lua);
    reg("GmPlayerXYZ", (void*)GmPlayerXYZ_Lua);
    reg("GmPlayerPose", (void*)GmPlayerPose_Lua);
    reg("GmFace", (void*)GmFace_Lua);
    reg("GmApproachGuid", (void*)GmApproachGuid_Lua);
    reg("GmObjFloat", (void*)GmObjFloat_Lua);
    reg("GmObjectCount", (void*)GmObjectCount_Lua);
    reg("GmObjectPump", (void*)GmObjectPump_Lua);
    reg("GmObjectCacheAge", (void*)GmObjectCacheAge_Lua);
    reg("GmObjectGen", (void*)GmObjectGen_Lua);
    reg("GmObjectSync", (void*)GmObjectSync_Lua);
    reg("GmObjectInfo", (void*)GmObjectInfo_Lua);
    reg("GmObjectByGuid", (void*)GmObjectByGuid_Lua);
    reg("GmOmPlayerPump", (void*)GmOmPlayerPump_Lua);
    reg("GmOmPlayerCount", (void*)GmOmPlayerCount_Lua);
    reg("GmOmPlayerInfo", (void*)GmOmPlayerInfo_Lua);
    reg("GmNearest", (void*)GmNearest_Lua);
    reg("GmNearestLootable", (void*)GmNearestLootable_Lua);
    reg("GmTargetNearestValid", (void*)GmTargetNearestValid_Lua);
    reg("GmLootableCount", (void*)GmLootableCount_Lua);
    reg("GmLootableGuid", (void*)GmLootableGuid_Lua);
    reg("GmLootOne", (void*)GmLootOne_Lua);
    reg("GmLootOpen", (void*)GmLootOpen_Lua);
    reg("GmLootNearest", (void*)GmLootNearest_Lua);
    reg("GmIsLootable", (void*)GmIsLootable_Lua);
    reg("GmIsSkinnable", (void*)GmIsSkinnable_Lua);
    reg("GmSkinnableCount", (void*)GmSkinnableCount_Lua);
    reg("GmSkinnableGuid", (void*)GmSkinnableGuid_Lua);
    reg("GmSkinStart", (void*)GmSkinStart_Lua);
    reg("GmLootTake", (void*)GmLootTake_Lua);
    reg("GmSkinNearest", (void*)GmSkinNearest_Lua);
    reg("GmSkin", (void*)GmSkin_Lua);
    reg("GmFindPath", (void*)GmFindPath_Lua);
    reg("GmPathPoint", (void*)GmPathPoint_Lua);
    reg("GmLoot", (void*)GmLoot_Lua);
    reg("GmLootEx", (void*)GmLootEx_Lua);
    reg("GmLootAll", (void*)GmLootAll_Lua);
    reg("GmLootRelease", (void*)GmLootRelease_Lua);
    reg("GmLootSlot", (void*)GmLootSlot_Lua);
    reg("GmLootMoney", (void*)GmLootMoney_Lua);
    reg("GmLootSource", (void*)GmLootSource_Lua);
    reg("GmLastLootPkt", (void*)GmLastLootPkt_Lua);
    reg("GmSetMouseover", (void*)GmSetMouseover_Lua);
    reg("GmTargetSlots", (void*)GmTargetSlots_Lua);


    reg("GmTargetNearest", (void*)GmTargetNearest_Lua);
    reg("GmInteractGuid", (void*)GmInteractGuid_Lua);
    reg("GmHwEvent", (void*)GmHwEvent_Lua);
    reg("GmNearestInfo", (void*)GmNearestInfo_Lua);
    reg("GmInteract", (void*)GmInteract_Lua);
    reg("GmUseObject", (void*)GmUseObject_Lua);
    reg("GmPlayerFacing", (void*)GmPlayerFacing_Lua);
    reg("GmSetFacing", (void*)GmSetFacing_Lua);
    reg("GmFaceAngle", (void*)GmFaceAngle_Lua);
    reg("GmFaceUnit", (void*)GmFaceUnit_Lua);
    reg("GmFaceTarget", (void*)GmFaceTarget_Lua);
    reg("GmCalibrateFacing", (void*)GmCalibrateFacing_Lua);
    reg("GmFacingInfo", (void*)GmFacingInfo_Lua);
    reg("GmClearTaint", (void*)GmClearTaint_Lua);
    reg("GmTeleport", (void*)GmTeleport_Lua);
    reg("GmTeleportRaw", (void*)GmTeleportRaw_Lua);
    reg("GmTeleportLoad", (void*)GmTeleport_Lua);
    reg("GmTeleportAnywhere", (void*)GmTeleportAnywhere_Lua);
    reg("GmTeleportValidate", (void*)GmTeleportValidate_Lua);
    reg("GmMirrorPublish", (void*)GmMirrorPublish_Lua);
    reg("GmMirrorPoll", (void*)GmMirrorPoll_Lua);
    reg("GmMirrorSeq", (void*)GmMirrorSeq_Lua);
    reg("GmTpLock", (void*)GmTpLock_Lua);
    reg("GmTpUnlock", (void*)GmTpUnlock_Lua);
    reg("GmFreeMove", (void*)GmFreeMove_Lua);
    reg("GmTpPulse", (void*)GmTpPulse_Lua);
    reg("GmTpLockActive", (void*)GmTpLockActive_Lua);


    reg("ClickToMove", (void*)ClickToMove_Lua);
    reg("ClickToMoveStop", (void*)ClickToMoveStop_Lua);
    reg("GmClickToMove", (void*)ClickToMove_Lua);
    reg("GmClickToMoveStop", (void*)ClickToMoveStop_Lua);
    reg("GmSetMove", (void*)GmSetMove_Lua);
    reg("GmMoveStatus", (void*)GmMoveStatus_Lua);

    reg("GmFlyhack", (void*)GmFlyhack_Lua);
    reg("GmNoclip", (void*)GmNoclip_Lua);
    reg("GmNoFall", (void*)GmNoFall_Lua);
    reg("GmHackBit", (void*)GmHackBit_Lua);
    reg("GmWaterwalk", (void*)GmWaterwalk_Lua);
    reg("GmSetHacks", (void*)GmSetHacks_Lua);
    reg("GmGetHacks", (void*)GmGetHacks_Lua);
    reg("GmSetNamePlateRange", (void*)GmSetNamePlateRange_Lua);
    reg("GmGetNamePlateRange", (void*)GmGetNamePlateRange_Lua);
    reg("GmSpeed", (void*)GmSpeed_Lua);
    reg("GmFindOpcode", (void*)GmFindOpcode_Lua);

    reg("GmIsExploredBit", (void*)GmIsExploredBit_Lua);
    reg("GmAreaBit", (void*)GmAreaBit_Lua);
    reg("GmIsAreaExplored", (void*)GmIsAreaExplored_Lua);
    reg("GmExploredWord", (void*)GmExploredWord_Lua);
    reg("GmAntiAfk", (void*)GmAntiAfk_Lua);
    reg("GmCharCreateUnlock", (void*)GmCharCreateUnlock_Lua);
    reg("GmCharCreateForce", (void*)GmCharCreateForce_Lua);
    reg("GmCharCreateChaos", (void*)GmCharCreateChaos_Lua);

    reg("GmSendBookmark", (void*)GmSendBookmark_Lua);
    reg("SendPacketAct", (void*)SendPacketAct_Lua);
    reg("GmPacketLoop", (void*)GmPacketLoop_Lua);
    reg("GmPacketBurst", (void*)GmPacketBurst_Lua);
    reg("GmBookmarkInfo", (void*)GmBookmarkInfo_Lua);

    /* Multi-instance shared world (host pushes via kCmdSubscribeShared). */
    reg("GmSharedCount", (void*)GmSharedCount_Lua);
    reg("GmSharedObject", (void*)GmSharedObject_Lua);
    reg("GmSharedObjects", (void*)GmSharedObjects_Lua);
    reg("GmSharedPlayers", (void*)GmSharedPlayers_Lua);
    reg("GmSharedPlayer", (void*)GmSharedPlayer_Lua);
    reg("GmInstanceInfo", (void*)GmInstanceInfo_Lua);
    reg("GmGetInstance", (void*)GmGetInstance_Lua);
    reg("GmGetInstanceCount", (void*)GmGetInstanceCount_Lua);
    reg("GmGetInstanceObject", (void*)GmGetInstanceObject_Lua);
    reg("GmSharedNearby", (void*)GmSharedNearby_Lua);
    reg("GmSharedNearbyObject", (void*)GmSharedNearbyObject_Lua);
    reg("GmPublishName", (void*)GmPublishName_Lua);
    reg("GmResolveInstance", (void*)GmResolveInstance_Lua);
    reg("GmListInstances", (void*)GmListInstances_Lua);
    reg("GmRemoteCall", (void*)GmRemoteCall_Lua);
    reg("GmRpcCapture", (void*)GmRpcCapture_Lua);
    reg("GmRpcFail", (void*)GmRpcFail_Lua);

    reg("GmUnitName", (void*)GmUnitName_Lua);
    reg("GmObjectName", (void*)GmUnitName_Lua);
    reg("GmReportChat", (void*)GmReportChat_Lua);
    reg("GmReportPlayer", (void*)GmReportPlayer_Lua);
    reg("GmSetClipboard", (void*)GmSetClipboard_Lua);

    reg("GmDrawClear", (void*)GmDrawClear_Lua);
    reg("GmDrawBeginFrame", (void*)GmDrawBeginFrame_Lua);
    reg("GmDrawEndFrame", (void*)GmDrawEndFrame_Lua);
    reg("GmDrawSetHz", (void*)GmDrawSetHz_Lua);
    reg("GmDrawLine", (void*)GmDrawLine_Lua);
    reg("GmDrawRect", (void*)GmDrawRect_Lua);
    reg("GmDrawCircle", (void*)GmDrawCircle_Lua);
    reg("GmDrawText", (void*)GmDrawText_Lua);
    reg("GmDrawWorldLine", (void*)GmDrawWorldLine_Lua);
    reg("GmDrawWorldBox", (void*)GmDrawWorldBox_Lua);
    reg("GmWorldToScreen", (void*)GmWorldToScreen_Lua);
    reg("GmOverlayReady", (void*)GmOverlayReady_Lua);
    reg("GmOverlayStats", (void*)GmOverlayStats_Lua);
    reg("GmLootEsp", (void*)GmLootEsp_Lua);
    reg("GmLootEspRadius", (void*)GmLootEspRadius_Lua);
    reg("GmDrawCmd", (void*)GmDrawCmd_Lua);

    RegisterTeleportNgApis(reg);
    RegisterGmActionApis(reg);
    RegisterApiExtApis(reg);
    RegisterQueryCacheApis(reg);
    LogLine("lua APIs: registered (GmTeleport/GmObjectCount/…)");
}

int ProxyLineOfSightGuid(uint64_t target_guid, uint32_t map)
{
    void* self = ObjMgrPlayerObject();
    void* tgt = ObjMgrFindByGuid(target_guid);
    float ax, ay, az, bx, by, bz;
    if (!self || !tgt)
        return -1;
    if (!ObjMgrPosition(self, &ax, &ay, &az, NULL))
        return -1;
    if (!ObjMgrPosition(tgt, &bx, &by, &bz, NULL))
        return -1;

    return NavLineOfSight(map, ax, ay, az + 2.0f, bx, by, bz + 2.0f, 2.0f);
}

int ProxyOpcodeName(uint32_t opcode, char* out, uint32_t out_cap)
{
    typedef const char*(__cdecl* OpcodeToNameFn)(uint32_t op);
    OpcodeToNameFn fn;
    const char* name;
    if (!out || out_cap < 2 || !g_real_base)
        return 0;
    if (opcode > 0x9D4u)
        return 0;
    fn = (OpcodeToNameFn)(g_real_base + g_off.ext_opcode_to_name);
    name = fn(opcode);
    if (!name || !name[0])
        return 0;
    lstrcpynA(out, name, (int)out_cap);
    return 1;
}

static uint32_t PacketPayload(const CDataStore* packet, const uint8_t** out_ptr)
{
    uint32_t start, end, len;
    if (!packet || !packet->buffer || !out_ptr)
        return 0;
    end = packet->size;
    start = packet->read_pos;
    if (start == 0xFFFFFFFFu || start > end)
        start = 0;
    if (end < start)
        return 0;
    len = end - start;
    if (len == 0) {

        if (packet->size > 0 && packet->size != 0xFFFFFFFFu
            && packet->size <= packet->alloc && packet->size < 0x10000u) {
            *out_ptr = packet->buffer;
            return packet->size;
        }
        return 0;
    }
    if (start >= packet->alloc)
        return 0;
    if (len > packet->alloc - start)
        len = packet->alloc - start;
    if (len > 0x10000u)
        return 0;
    *out_ptr = packet->buffer + start;
    return len;
}

static uint32_t ReadOpcode(const uint8_t* data, uint32_t size)
{
    uint32_t op = 0;
    uint16_t op16 = 0;
    if (!data || size < 2)
        return 0;
    memcpy(&op16, data, 2);
    if (size < 4)
        return op16;
    memcpy(&op, data, 4);
    if (op > kMaxOpcode)
        return op16;
    return op;
}

static int QuadCalibratesAt(const uint8_t* p, uint32_t off)
{
    float q[4];
    memcpy(q, p + off, sizeof(q));
    if (q[0] != q[0] || q[1] != q[1] || q[2] != q[2] || q[3] != q[3])
        return 0;
    if (fabsf(q[0]) < 1.f || fabsf(q[0]) > 20000.f)
        return 0;
    if (fabsf(q[1]) < 1.f || fabsf(q[1]) > 20000.f)
        return 0;
    if (q[2] < -2500.f || q[2] > 4500.f)
        return 0;
    if (q[3] < -7.f || q[3] > 7.f)
        return 0;
    return ObjMgrCalibrate(q[0], q[1], q[2]) != 0;
}

static uint32_t FindMoveQuadOffset(const uint8_t* p, uint32_t len)
{
    uint32_t off;

    if (kAscOffX + 20u <= len && QuadCalibratesAt(p, kAscOffX))
        return kAscOffX;

    for (off = 4u; off + 20u <= len; off += 1u) {
        if (off == kAscOffX)
            continue;
        if (QuadCalibratesAt(p, off))
            return off;
    }
    return 0;
}

/* Stock 3.3.5a loot family. Ascension keeps these opcodes (verified CMSG
 * 0x15D-0x15F in this tree). Packed GUID after the 4-byte opcode is the
 * common SMSG_LOOT_RESPONSE layout; unpacked 8-byte GUID is the fallback. */
static int LootFamilyOpcode(uint32_t op)
{
    if (op >= 0x15Du && op <= 0x166u)
        return 1;
    if (op == 0x8Bu || op == 0x17Bu || op == 0x0B1u || op == 0x108u || op == 0x13Du)
        return 1;
    if (op == 0x29Eu || op == 0x29Fu)
        return 1;
    return 0;
}

static uint64_t ReadPackedGuid(const uint8_t* p, uint32_t len, uint32_t off)
{
    uint64_t g = 0;
    unsigned i;
    uint8_t mask;
    if (!p || off >= len)
        return 0;
    mask = p[off++];
    for (i = 0; i < 8 && off < len; i++) {
        if (mask & (uint8_t)(1u << i))
            g |= ((uint64_t)p[off++]) << (i * 8);
    }
    return g;
}

static void NoteLootPacket(uint8_t dir, uint32_t opcode, const uint8_t* p, uint32_t len)
{
    uint64_t guid = 0;
    if (!LootFamilyOpcode(opcode) || !p)
        return;
    if (len >= 12u)
        memcpy(&guid, p + 4, 8);
    if (guid == 0 && len >= 6u)
        guid = ReadPackedGuid(p, len, 4u);
    InterlockedExchange(&g_loot_pkt_op, (LONG)opcode);
    InterlockedExchange(&g_loot_pkt_dir, (LONG)dir);
    InterlockedExchange(&g_loot_pkt_len, (LONG)len);
    InterlockedExchange(&g_loot_pkt_guid_lo, (LONG)(uint32_t)(guid & 0xFFFFFFFFu));
    InterlockedExchange(&g_loot_pkt_guid_hi, (LONG)(uint32_t)(guid >> 32));
    InterlockedExchange(&g_loot_pkt_tick, (LONG)GetTickCount());
    InterlockedIncrement(&g_loot_pkt_gen);
}

static void SniffStoreDir(CDataStore* packet, const char* tag, uint8_t dir)
{
    const uint8_t* p = NULL;
    uint32_t len;
    uint32_t opcode;
    char b[140];
    if (!packet)
        return;
    len = PacketPayload(packet, &p);
    if (!len || !p)
        return;
    opcode = ReadOpcode(p, len);
    EntitlementOnPacket(dir, opcode, p, len);
    NoteLootPacket(dir, opcode, p, len);
    QueryCacheNotePacket(dir, opcode, p, len);

    if (dir == kPktDirOut && len >= 38u
        && ((opcode >= 0xB5u && opcode <= 0xFFu) || opcode == 0xEEu)) {
        uint32_t guid = 0, counter = 0;
        memcpy(&guid, p + 4, 4);
        if (guid) {
            InterlockedExchange(&g_mover_guid, (LONG)guid);
            if (len >= 38u) {
                memcpy(&counter, p + 34, 4);
                if (counter != 0 && counter < 0x10000000u)
                    InterlockedExchange(&g_move_counter, (LONG)counter);
            }
            if (!g_move_template_len && len <= sizeof(g_move_template)) {

                memcpy(g_move_template, p, len);
                g_move_template_len = (LONG)len;
                SanitizeTpMoveBuf(g_move_template, (uint32_t)g_move_template_len);
                if (!g_move_quad_off)
                    g_move_quad_off = 18u;
            }
        }
    }


    if (dir == kPktDirOut && len >= kAscMoveSize
        && opcode >= 0xB5u && opcode <= 0xFFu
        && !ObjMgrPositionOffset()) {
        uint32_t off = FindMoveQuadOffset(p, len);
        if (off) {
            char cb[96];
            g_move_quad_off = off;
            _snprintf(cb, sizeof(cb),
                "objmgr: move-packet coords found at +%u (facing +%u)", off, off + 12u);
            LogLine(cb);
        }
    }

    if (dir == kPktDirOut && g_move_quad_off && len >= g_move_quad_off + 16u) {
        float q[4];
        memcpy(q, p + g_move_quad_off, sizeof(q));
        if (q[0] == q[0] && q[3] == q[3]
            && fabsf(q[0]) > 1.f && fabsf(q[0]) < 20000.f
            && fabsf(q[1]) > 1.f && fabsf(q[1]) < 20000.f
            && q[2] > -2500.f && q[2] < 4500.f
            && q[3] >= -7.f && q[3] <= 7.f) {
            g_player_facing = q[3];
            InterlockedExchange(&g_facing_valid, 1);
            g_last_player_x = q[0];
            g_last_player_y = q[1];
            g_last_player_z = q[2];
            InterlockedExchange(&g_last_player_valid, 1);

            if (!ObjMgrFacingOffsetResolved())
                ObjMgrCalibrateFacing(q[3]);

            if (len <= sizeof(g_move_template) && g_move_quad_off + 20u <= len) {
                uint32_t cnt;
                memcpy(g_move_template, p, len);
                g_move_template_len = (LONG)len;
                SanitizeTpMoveBuf(g_move_template, (uint32_t)g_move_template_len);
                memcpy(&cnt, p + g_move_quad_off + 16u, 4);
                g_move_counter = (LONG)cnt;
            }
        }
    }
    if (PktIpcSniff(dir, opcode, p, len)) {
        InterlockedIncrement(&g_sniff_writes);
        if (g_sniff_writes <= 5) {
            char opname[64];
            if (!ProxyOpcodeName(opcode, opname, sizeof(opname)))
                _snprintf(opname, sizeof(opname), "unknown");
            _snprintf(b, sizeof(b), "sniff(%s/%s) #%ld op=0x%04X %s len=%u",
                tag, dir == kPktDirIn ? "IN" : (dir == kPktDirOut ? "OUT" : "REPLAY"),
                (long)g_sniff_writes, opcode, opname, len);
            LogLine(b);
        }
    }
}

static void SniffStore(CDataStore* packet, const char* tag)
{
    SniffStoreDir(packet, tag, kPktDirOut);
}

static void ForceState5(void* netClient, uint32_t* old_out)
{
    uint32_t* state = (uint32_t*)((uint8_t*)netClient + kNetClientStateOffset);
    *old_out = *state;
    *state = 5u;
}

static uint8_t g_inj_buf[64];
static int InjectClientPacket(const uint8_t* bytes, uint32_t n)
{
    void* nc = (void*)g_last_net_client;
    CDataStore ds;
    uint32_t old = 0;
    if (!g_send_stub || !nc || !bytes || n == 0u || n > sizeof(g_inj_buf))
        return 0;
    memcpy(g_inj_buf, bytes, n);
    if (n >= 4u) {
        uint32_t op = 0;
        memcpy(&op, g_inj_buf, 4);
        if (IsForbiddenGmTeleportOpcode(op))
            return 1;
        if (op == kOpcodeMoveFallLand) {
            uint32_t hb = kDefaultMoveOpcode;
            memcpy(g_inj_buf, &hb, 4);
            op = hb;
        }
        if (n >= kAscMoveSize && IsMoveFamilyOpcode(op)) {
            SanitizeTpMoveBuf(g_inj_buf, n);
            if (n > kAscMoveSize)
                n = kAscMoveSize;
        }
    }
    memset(&ds, 0, sizeof(ds));
    ds.buffer = g_inj_buf;
    ds.alloc = sizeof(g_inj_buf);
    ds.size = n;
    ds.read_pos = 0xFFFFFFFFu;
    if (g_fn_reset)
        tc0(g_fn_reset, &ds);
    else
        ds.read_pos = 0;
    ForceState5(nc, &old);
    tc_net_send(g_send_stub, nc, &ds);
    return 1;
}

static int PtrReadable(const void* p, size_t n);

static void ReplayViaNet(void* netClient)
{
    LONG slot;
    uint8_t* raw;
    CDataStore* pkt;
    uint32_t n;
    uint32_t old = 0;
    uint32_t opcode;
    char msg[96];

    if (!g_send_stub || !netClient)
        return;
    slot = InterlockedIncrement(&g_rep_buf_i) & (kRepBufCount - 1);
    raw = g_rep_bufs[slot];
    pkt = &g_rep_pkts[slot];
    n = PKT_REPLAY_MAX;
    if (!PktIpcTakeReplay(raw, &n) || !n)
        return;
    opcode = ReadOpcode(raw, n);
    memset(pkt, 0, sizeof(*pkt));
    pkt->buffer = raw;
    pkt->alloc = PKT_REPLAY_MAX;
    pkt->size = n;
    pkt->read_pos = 0xFFFFFFFFu;
    if (g_fn_reset)
        tc0(g_fn_reset, pkt);
    else
        pkt->read_pos = 0;
    ForceState5(netClient, &old);
    PatchReplayLegitimacy(raw, n);
    tc_net_send(g_send_stub, netClient, pkt);
    PktIpcSniff(kPktDirReplay, opcode, raw, n);
    PktIpcMarkReplayOk();
    _snprintf(msg, sizeof(msg), "replay-net op=0x%X bytes=%u", opcode, n);
    LogLine(msg);
}

/* Ensure forged CMSG looks like a live client packet before NetClient::Send
 * (session ARC4/headers are applied inside the real send path). */
static void PatchReplayLegitimacy(uint8_t* raw, uint32_t n)
{
    uint32_t opcode;
    uint32_t body;
    if (!raw || n < 6u)
        return;
    opcode = ReadOpcode(raw, n);
    body = (n >= 4u && *(uint32_t*)raw == opcode) ? 4u : 2u;

    /* Movement family: keep mover GUID + counter in sync with last sniffed send. */
    if (opcode >= 0xB5u && opcode <= 0xFFu) {
        uint32_t guid = (uint32_t)InterlockedCompareExchange(&g_mover_guid, 0, 0);
        uint32_t counter = (uint32_t)InterlockedCompareExchange(&g_move_counter, 0, 0);
        if (guid && body + 4u <= n)
            memcpy(raw + body, &guid, 4);
        if (g_move_quad_off && g_move_quad_off + 20u <= n) {
            counter += 1u;
            InterlockedExchange(&g_move_counter, (LONG)counter);
            memcpy(raw + g_move_quad_off + 16u, &counter, 4);
            if (InterlockedCompareExchange(&g_last_player_valid, 0, 0)) {
                float q[4];
                q[0] = g_last_player_x;
                q[1] = g_last_player_y;
                q[2] = g_last_player_z;
                q[3] = g_player_facing;
                memcpy(raw + g_move_quad_off, q, sizeof(q));
            }
        }
    }
}

/* ---- packet bookmarks + incoming inject (ProcessIncoming) ---- */

#define kBmSlots PKT_BOOKMARK_SLOTS
static uint8_t g_bm_data[kBmSlots][PKT_REPLAY_MAX];
static uint32_t g_bm_len[kBmSlots];
static uint8_t g_bm_dir[kBmSlots]; /* 0=out/server, 1=in/client */
static volatile LONG g_bm_loop = 0;
static volatile LONG g_bm_loop_idx = 0;
static DWORD g_bm_loop_last = 0;
static volatile LONG g_inj_in_reent = 0;
static uint8_t g_inj_exec_buf[PKT_REPLAY_MAX];

typedef void(__cdecl* ProcessIncomingCdecl)(void* ctx, CDataStore* packet);

static int DeliverIncomingNow(const uint8_t* bytes, uint32_t n)
{
    void* ctx;
    CDataStore ds;
    ProcessIncomingCdecl fn;
    DWORD age;
    if (!g_in_stub || !bytes || !n || n > PKT_REPLAY_MAX)
        return 0;
    ctx = (void*)g_last_recv_ctx;
    if (!ctx)
        return 0;
    age = GetTickCount() - g_last_recv_ctx_tick;
    if (age > 120000u) /* stale connection context */
        return 0;
    memcpy(g_inj_exec_buf, bytes, n);
    memset(&ds, 0, sizeof(ds));
    ds.buffer = g_inj_exec_buf;
    ds.alloc = PKT_REPLAY_MAX;
    ds.size = n;
    ds.read_pos = 0xFFFFFFFFu;
    if (g_fn_reset)
        tc0(g_fn_reset, &ds);
    else
        ds.read_pos = 0;
    fn = (ProcessIncomingCdecl)g_in_stub;
    fn(ctx, &ds);
    {
        char msg[96];
        uint32_t op = ReadOpcode(bytes, n);
        _snprintf(msg, sizeof(msg), "inject-recv op=0x%X bytes=%u", op, n);
        LogLine(msg);
    }
    return 1;
}

void ProxyDrainInjectIncoming(void)
{
    uint32_t n;
    if (InterlockedCompareExchange(&g_inj_in_reent, 1, 0) != 0)
        return;
    n = PKT_REPLAY_MAX;
    if (PktIpcTakeInjectIn(g_inj_exec_buf, &n) && n)
        DeliverIncomingNow(g_inj_exec_buf, n);
    InterlockedExchange(&g_inj_in_reent, 0);
}

int ProxyBookmarkSet(uint32_t slot, uint32_t dir, const uint8_t* data, uint32_t size)
{
    uint32_t i;
    if (!data || !size || size > PKT_REPLAY_MAX)
        return 0;
    if (slot < 1u || slot > kBmSlots)
        return 0;
    i = slot - 1u;
    memcpy(g_bm_data[i], data, size);
    g_bm_len[i] = size;
    g_bm_dir[i] = dir ? 1u : 0u;
    {
        char msg[80];
        _snprintf(msg, sizeof(msg), "bookmark set #%u dir=%u len=%u", slot, g_bm_dir[i], size);
        LogLine(msg);
    }
    return 1;
}

int ProxyBookmarkClear(uint32_t slot)
{
    uint32_t i;
    if (slot == 0u) {
        for (i = 0; i < kBmSlots; i++)
            g_bm_len[i] = 0;
        return 1;
    }
    if (slot < 1u || slot > kBmSlots)
        return 0;
    g_bm_len[slot - 1u] = 0;
    return 1;
}

int ProxyBookmarkFire(uint32_t slot)
{
    uint32_t i;
    if (slot < 1u || slot > kBmSlots)
        return 0;
    i = slot - 1u;
    if (!g_bm_len[i])
        return 0;
    if (g_bm_dir[i])
        return ProxyQueueInjectIncoming(g_bm_data[i], g_bm_len[i]);
    return PktIpcQueueReplay(g_bm_data[i], g_bm_len[i]);
}

void ProxyBookmarkLoopSet(uint32_t on)
{
    InterlockedExchange(&g_bm_loop, on ? 1 : 0);
    if (on)
        LogLine("bookmark loop ON");
    else
        LogLine("bookmark loop OFF");
}

uint32_t ProxyBookmarkLoopGet(void)
{
    return (uint32_t)InterlockedCompareExchange(&g_bm_loop, 0, 0);
}

int ProxyBookmarkBurst(void)
{
    uint32_t i, n = 0;
    /* Sequential fire in bookmark order (1..16). Outbound uses live net send
       so multiple CMSG can go out in one burst; inbound uses ProcessIncoming. */
    for (i = 0; i < kBmSlots; i++) {
        if (!g_bm_len[i])
            continue;
        if (g_bm_dir[i]) {
            if (DeliverIncomingNow(g_bm_data[i], g_bm_len[i]))
                n++;
            else if (ProxyQueueInjectIncoming(g_bm_data[i], g_bm_len[i]))
                n++;
        } else {
            void* nc = (void*)g_last_net_client;
            if (nc && g_send_stub) {
                LONG slot = InterlockedIncrement(&g_rep_buf_i) & (kRepBufCount - 1);
                uint8_t* raw = g_rep_bufs[slot];
                CDataStore* pkt = &g_rep_pkts[slot];
                uint32_t old = 0;
                uint32_t len = g_bm_len[i];
                memcpy(raw, g_bm_data[i], len);
                memset(pkt, 0, sizeof(*pkt));
                pkt->buffer = raw;
                pkt->alloc = PKT_REPLAY_MAX;
                pkt->size = len;
                pkt->read_pos = 0xFFFFFFFFu;
                if (g_fn_reset)
                    tc0(g_fn_reset, pkt);
                else
                    pkt->read_pos = 0;
                ForceState5(nc, &old);
                tc_net_send(g_send_stub, nc, pkt);
                PktIpcSniff(kPktDirReplay, ReadOpcode(raw, len), raw, len);
                n++;
            } else if (PktIpcQueueReplay(g_bm_data[i], g_bm_len[i])) {
                n++;
            }
        }
    }
    {
        char msg[64];
        _snprintf(msg, sizeof(msg), "bookmark burst fired=%u", n);
        LogLine(msg);
    }
    return (int)n;
}

void ProxyBookmarkLoopPulse(void)
{
    DWORD now;
    uint32_t start, i, tried;
    if (!InterlockedCompareExchange(&g_bm_loop, 0, 0))
        return;
    now = GetTickCount();
    if (now - g_bm_loop_last < 250u)
        return;
    g_bm_loop_last = now;
    start = (uint32_t)InterlockedIncrement(&g_bm_loop_idx);
    tried = 0;
    for (i = 0; i < kBmSlots; i++) {
        uint32_t slot = ((start + i - 1u) % kBmSlots) + 1u;
        if (g_bm_len[slot - 1u]) {
            ProxyBookmarkFire(slot);
            tried = 1;
            break;
        }
    }
    (void)tried;
}

static int __cdecl GmSendBookmark_Lua(void* L)
{
    uint32_t slot = (uint32_t)LuaArgNum(L, 1);
    int ok = ProxyBookmarkFire(slot);
    LuaPushNum(L, ok ? 1.0 : 0.0);
    return 1;
}

static int __cdecl SendPacketAct_Lua(void* L)
{
    return GmSendBookmark_Lua(L);
}

static int __cdecl GmPacketLoop_Lua(void* L)
{
    const char* s = LuaArgStr(L, 1);
    uint32_t on = 1u;
    if (s && s[0]) {
        if (_strnicmp(s, "off", 3) == 0 || _strnicmp(s, "stop", 4) == 0
            || _strnicmp(s, "0", 1) == 0 || _strnicmp(s, "false", 5) == 0)
            on = 0u;
        else if (s[0] >= '0' && s[0] <= '9')
            on = (LuaArgNum(L, 1) != 0.0) ? 1u : 0u;
    } else {
        on = (LuaArgNum(L, 1) != 0.0) ? 1u : 0u;
    }
    ProxyBookmarkLoopSet(on);
    LuaPushNum(L, (double)ProxyBookmarkLoopGet());
    return 1;
}

static int __cdecl GmPacketBurst_Lua(void* L)
{
    LuaPushNum(L, (double)ProxyBookmarkBurst());
    return 1;
}

static int __cdecl GmBookmarkInfo_Lua(void* L)
{
    uint32_t slot = (uint32_t)LuaArgNum(L, 1);
    uint32_t i;
    if (slot < 1u || slot > kBmSlots) {
        LuaPushNum(L, 0);
        LuaPushNum(L, 0);
        LuaPushNum(L, 0);
        return 3;
    }
    i = slot - 1u;
    LuaPushNum(L, (double)g_bm_len[i]);
    LuaPushNum(L, (double)g_bm_dir[i]);
    LuaPushNum(L, g_bm_len[i] ? (double)ReadOpcode(g_bm_data[i], g_bm_len[i]) : 0.0);
    return 3;
}

static void THISCALL HookedNetSend(void* netClient, CDataStore* packet)
{
    LONG calls;
    if (!g_send_stub)
        return;
    if (netClient)
        InterlockedExchangePointer(&g_last_net_client, netClient);
    calls = InterlockedIncrement(&g_send_hook_calls);


    if (packet && packet->buffer && packet->size >= 4u
        && PtrReadable(packet->buffer, packet->size)) {
        uint32_t op = ReadOpcode(packet->buffer, packet->size);
        if (IsForbiddenGmTeleportOpcode(op)) {
            static volatile LONG s_drop = 0;
            if ((InterlockedIncrement(&s_drop) % 8) == 1)
                LogLine("drop GM teleport opcode (player — no WORLD_TELEPORT/CHARM_PORT)");
            return;
        }
        RewriteFallLandToHeartbeat(packet);
        PatchOutboundMove(packet);
        ApplyTpLockToPacket(packet->buffer, packet->size, 0);
    }

    SniffStore(packet, "send");

    if (packet && packet->buffer && packet->size >= 4u
        && PtrReadable(packet->buffer, packet->size)) {
        uint32_t send_op = ReadOpcode(packet->buffer, packet->size);
        if (EntitlementShouldDropSend(send_op, packet->buffer, packet->size))
            return;
        if (EntitlementIsPlayerLogin(send_op)) {
            g_login_inflight = 1;
            LogLine("login: CMSG_PLAYER_LOGIN forwarded — Lua paused until world");
        }
    }

    tc_net_send(g_send_stub, netClient, packet);

    /* Keep movement / AFK light work on the send path.
     * ObjMgrPump + RegisterLuaApis moved to GetMsgProc — they froze cities. */
    ProxyPumpMove();
    ProxyAntiAfkPulse(0);

    {
        int in_world = ObjMgrPlayerGuid() ? 1 : 0;
        if (in_world) {
            if (g_login_inflight)
                LogLine("login: in-world — resume Lua");
            g_login_inflight = 0;
        }
        if (in_world && !g_lua_was_in_world) {
            g_lua_api_tick = 0;
            InterlockedExchange(&g_popup_seeded, 0);
            LogLine("lua: world enter — re-register natives");
            WakeUiForInjectAsync();
        } else if (!in_world) {
            g_lua_was_in_world = 0;
        }
        if (in_world)
            g_lua_was_in_world = 1;
    }

    if (g_fire_lua_cast)
        WakeUiForInjectAsync();

    if (InterlockedCompareExchange(&g_reentrancy, 1, 0) == 0) {
        if (PktIpcReplayPending())
            ReplayViaNet(netClient);
        ProxyDrainInjectIncoming();
        ProxyBookmarkLoopPulse();
        InterlockedExchange(&g_reentrancy, 0);
    }

    if ((calls % 10000) == 0) {
        static DWORD s_last_send_log;
        DWORD now = GetTickCount();
        if (now - s_last_send_log >= 30000u) {
            char b[96];
            s_last_send_log = now;
            _snprintf(b, sizeof(b), "send_hooks=%ld sniffs=%ld",
                (long)calls, (long)g_sniff_writes);
            LogLine(b);
        }
    }
}

static void __cdecl HookedQueue(CDataStore* packet)
{
    /* Outbound is sniffed once in HookedNetSend — avoid double ring writes. */
    if (!g_send_stub)
        SniffStore(packet, "queue");
    CallQueueTramp(packet);
}

static void HandleAscInject(void)
{
    MaybeClearTaint();
    /* Always try — internal throttle + null-L guard. UI-thread preferred path. */
    {
        void* L = ProxyLuaState();
        static int s_had_l;
        if (L && !s_had_l) {
            g_lua_api_tick = 0;
            s_had_l = 1;
            LogLine("lua: state acquired — force native re-register");
        } else if (!L) {
            s_had_l = 0;
        }
    }
    if (g_login_inflight && !ObjMgrPlayerGuid())
        return;
    RegisterLuaApis();
    /* Seed once (g_popup_seeded CAS). Safe here — no reseed-after-drain loop. */
    if (g_lua_api_tick)
        SeedPopupSuppress();
    InstBusDrainPending();
    ConsumePendingLuaCast();
}

static LRESULT CALLBACK GetMsgProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code >= 0 && lParam) {
        MSG* m = (MSG*)lParam;
        static DWORD s_last_heavy;
        DWORD now = GetTickCount();
        MaybeClearTaint();

        /* Movement/AFK stay responsive; heavy OM/Lua work is paced. */
        ProxyPumpMove();
        ProxyAntiAfkPulse(0);
        if (!s_last_heavy || (now - s_last_heavy) >= 50u) {
            s_last_heavy = now;
            ObjMgrPump();
            if (!(g_login_inflight && !ObjMgrPlayerGuid())) {
                RegisterLuaApis();
                if (g_lua_api_tick)
                    SeedPopupSuppress();
                InstBusDrainPending();
            }
        }
        if (m->message == WM_ASC_INJECT || (m->message == WM_NULL && g_fire_lua_cast))
            HandleAscInject();
    }
    return CallNextHookEx(g_msg_hook, code, wParam, lParam);
}

static LRESULT CALLBACK CallWndProc(int code, WPARAM wParam, LPARAM lParam)
{
    (void)wParam;
    if (code >= 0 && lParam) {
        CWPSTRUCT* c = (CWPSTRUCT*)lParam;

        if (c->message == WM_ASC_INJECT || (c->message == WM_NULL && g_fire_lua_cast))
            HandleAscInject();
    }
    return CallNextHookEx(g_cwp_hook, code, wParam, lParam);
}

typedef struct EnumCtx {
    DWORD pid;
    HWND hwnd;
} EnumCtx;

static BOOL CALLBACK EnumWndProc(HWND hwnd, LPARAM lp)
{
    EnumCtx* ctx = (EnumCtx*)lp;
    DWORD pid = 0;
    if (!IsWindowVisible(hwnd))
        return TRUE;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == ctx->pid) {
        ctx->hwnd = hwnd;
        return FALSE;
    }
    return TRUE;
}

static int InstallUiHook(void);

static int InstallUiHook(void)
{
    EnumCtx ctx;
    char buf[160];
    ctx.pid = GetCurrentProcessId();
    ctx.hwnd = NULL;
    EnumWindows(EnumWndProc, (LPARAM)&ctx);
    if (!ctx.hwnd) {
        LogLine("ui hook: no hwnd yet");
        return 0;
    }
    g_hwnd = ctx.hwnd;
    g_ui_tid = GetWindowThreadProcessId(ctx.hwnd, NULL);
    if (!g_cwp_hook)
        g_cwp_hook = SetWindowsHookExW(WH_CALLWNDPROC, CallWndProc, g_self, g_ui_tid);
    if (!g_msg_hook)
        g_msg_hook = SetWindowsHookExW(WH_GETMESSAGE, GetMsgProc, g_self, g_ui_tid);
    _snprintf(buf, sizeof(buf), "ui hooks tid=%lu hwnd=%p cwp=%p msg=%p",
        (unsigned long)g_ui_tid, (void*)ctx.hwnd, (void*)g_cwp_hook, (void*)g_msg_hook);
    LogLine(buf);
    if (!g_cwp_hook || !g_msg_hook) {
        LogLine("ui hooks FAIL — Lua dispatch will not run");
        return 0;
    }
    return 1;
}

static int InstallQueueHook(void)
{
    uint8_t* target;
    DWORD old_prot;
    intptr_t rel;
    uint8_t stub[16];

    if (!g_ascension || g_hooked)
        return g_hooked;
    target = g_ascension + g_off.packet_queue;
    if (target[0] != 0x55 || target[1] != 0x8B || target[2] != 0xEC)
        return 0;
    g_queue_stolen_len = 8;
    memcpy(g_queue_stolen, target, g_queue_stolen_len);
    g_queue_stub = (uint8_t*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!g_queue_stub)
        return 0;
    memcpy(stub, g_queue_stolen, g_queue_stolen_len);
    stub[g_queue_stolen_len] = 0xE9;
    rel = (intptr_t)(target + g_queue_stolen_len) - (intptr_t)(g_queue_stub + g_queue_stolen_len + 5);
    memcpy(stub + g_queue_stolen_len + 1, &rel, 4);
    memcpy(g_queue_stub, stub, g_queue_stolen_len + 5);
    g_queue_tramp = (QueuePacketFn)g_queue_stub;
    if (!VirtualProtect(target, 16, PAGE_EXECUTE_READWRITE, &old_prot))
        return 0;
    target[0] = 0xE9;
    rel = (intptr_t)&HookedQueue - (intptr_t)(target + 5);
    memcpy(target + 1, &rel, 4);
    memset(target + 5, 0x90, 3);
    VirtualProtect(target, 16, old_prot, &old_prot);
    FlushInstructionCache(GetCurrentProcess(), target, 16);
    g_hooked = 1;
    LogLine("queue hook ok");
    return 1;
}

static int InstallSendHook(void)
{
    uint8_t* target;
    DWORD old_prot;
    intptr_t rel;
    int32_t old_rel;
    uint8_t* chain_dest;
    char msg[96];

    if (!g_ascension || g_send_hooked)
        return g_send_hooked;
    target = g_ascension + g_off.net_client_send;
    if (target[0] == 0xE9 && g_send_stub) {
        g_send_hooked = 1;
        LogLine("send hook already installed");
        return 1;
    }

    g_send_stub = (uint8_t*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!g_send_stub) {
        LogLine("send hook FAIL VirtualAlloc");
        return 0;
    }


    if (target[0] == 0x55 && target[1] == 0x8B && target[2] == 0xEC && target[3] == 0x56) {
        g_send_stolen_len = 6;
        memcpy(g_send_stolen, target, g_send_stolen_len);
        memcpy(g_send_stub, g_send_stolen, g_send_stolen_len);
        g_send_stub[g_send_stolen_len] = 0xE9;
        rel = (intptr_t)(target + g_send_stolen_len)
            - (intptr_t)(g_send_stub + g_send_stolen_len + 5);
        memcpy(g_send_stub + g_send_stolen_len + 1, &rel, 4);
    } else if (target[0] == 0xE9) {
        memcpy(&old_rel, target + 1, 4);
        chain_dest = target + 5 + old_rel;
        g_send_stolen_len = 5;
        memcpy(g_send_stolen, target, 5);

        g_send_stub[0] = 0x68;
        memcpy(g_send_stub + 1, &chain_dest, 4);
        g_send_stub[5] = 0xC3;
        _snprintf(msg, sizeof(msg), "send hook chain -> %p", (void*)chain_dest);
        LogLine(msg);
    } else {
        _snprintf(msg, sizeof(msg), "send hook FAIL bytes %02X %02X %02X %02X %02X %02X",
            target[0], target[1], target[2], target[3], target[4], target[5]);
        LogLine(msg);
        VirtualFree(g_send_stub, 0, MEM_RELEASE);
        g_send_stub = NULL;
        return 0;
    }

    if (!VirtualProtect(target, 16, PAGE_EXECUTE_READWRITE, &old_prot)) {
        LogLine("send hook FAIL VirtualProtect");
        VirtualFree(g_send_stub, 0, MEM_RELEASE);
        g_send_stub = NULL;
        return 0;
    }
    target[0] = 0xE9;
    rel = (intptr_t)&HookedNetSend - (intptr_t)(target + 5);
    memcpy(target + 1, &rel, 4);
    if (g_send_stolen_len > 5)
        target[5] = 0x90;
    VirtualProtect(target, 16, old_prot, &old_prot);
    FlushInstructionCache(GetCurrentProcess(), target, 16);
    FlushInstructionCache(GetCurrentProcess(), g_send_stub, 64);
    g_send_hooked = 1;
    LogLine("send hook ok (NetClient::Send)");
    return 1;
}

static int PtrReadable(const void* p, size_t n)
{
    MEMORY_BASIC_INFORMATION mbi;
    uintptr_t q, region_end;
    static uintptr_t s_base, s_end;
    static DWORD s_prot;
    if (!p || !n)
        return 0;
    q = (uintptr_t)p;
    region_end = q + n;
    if (s_end && q >= s_base && region_end <= s_end
        && !(s_prot & (PAGE_NOACCESS | PAGE_GUARD)))
        return 1;
    if (VirtualQuery(p, &mbi, sizeof(mbi)) != sizeof(mbi))
        return 0;
    if (mbi.State != MEM_COMMIT)
        return 0;
    if (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD))
        return 0;
    s_base = (uintptr_t)mbi.BaseAddress;
    s_end = s_base + mbi.RegionSize;
    s_prot = mbi.Protect;
    return region_end <= s_end;
}

static int PtrWritable(void* p, size_t n)
{
    MEMORY_BASIC_INFORMATION mbi;
    DWORD prot;
    if (!PtrReadable(p, n))
        return 0;
    if (VirtualQuery(p, &mbi, sizeof(mbi)) != sizeof(mbi))
        return 0;
    prot = mbi.Protect & 0xFFu;
    return prot == PAGE_READWRITE || prot == PAGE_WRITECOPY
        || prot == PAGE_EXECUTE_READWRITE || prot == PAGE_EXECUTE_WRITECOPY;
}

static uint32_t* AntiAfkLhaPtr(void)
{
    uint8_t* base = g_ascension;
    uint32_t* p;
    if (!base)
        return NULL;
    p = (uint32_t*)(base + kRvaLastHardwareAction);
    if (PtrReadable(p, 4) && PtrWritable(p, 4))
        return p;

    p = (uint32_t*)(uintptr_t)0x00B499A4u;
    if (PtrReadable(p, 4) && PtrWritable(p, 4))
        return p;
    return NULL;
}

static uint32_t AntiAfkReadClock(void)
{
    uint8_t* base = g_ascension;
    uint32_t* c;
    if (base) {
        c = (uint32_t*)(base + kRvaPerfCounter);
        if (PtrReadable(c, 4) && *c != 0u)
            return *c;
        c = (uint32_t*)(base + kRvaTimeStamp);
        if (PtrReadable(c, 4) && *c != 0u)
            return *c;
    }
    c = (uint32_t*)(uintptr_t)0x00CD76ACu;
    if (PtrReadable(c, 4) && *c != 0u)
        return *c;
    c = (uint32_t*)(uintptr_t)0x00B1D618u;
    if (PtrReadable(c, 4) && *c != 0u)
        return *c;
    return GetTickCount();
}

static int ProxyAntiAfkPulse(int force)
{
    uint32_t* lha;
    uint32_t now_clk, now_tick;
    if (!g_anti_afk_enabled)
        return 0;
    now_tick = GetTickCount();
    if (!force) {
        DWORD last = g_anti_afk_last_pulse;
        DWORD iv = g_anti_afk_interval_ms;
        if (iv < kAntiAfkMinMs) iv = kAntiAfkMinMs;
        if (last && (now_tick - last) < iv)
            return 1;
    }
    lha = AntiAfkLhaPtr();
    if (!lha)
        return 0;
    now_clk = AntiAfkReadClock();
    *lha = now_clk;
    g_anti_afk_last_pulse = now_tick;
    InterlockedIncrement(&g_anti_afk_pulses);
    return 1;
}

static int InstallAfkIdlePatch(void)
{
    uint8_t* p;
    DWORD old = 0;
    if (!g_ascension || g_anti_afk_patched)
        return g_anti_afk_patched ? 1 : 0;
    p = g_ascension + kRvaAfkIdleSub;
    if (!PtrReadable(p, 6))
        return 0;

    if (p[0] != 0x2Bu || p[1] != 0x05u) {
        char b[96];
        _snprintf(b, sizeof(b), "anti-afk patch skip: bytes %02X %02X @ RVA 0x%X",
            p[0], p[1], kRvaAfkIdleSub);
        LogLine(b);
        return 0;
    }
    memcpy(g_afk_sub_saved, p, 6);
    g_afk_sub_have_saved = 1;
    if (!VirtualProtect(p, 6, PAGE_EXECUTE_READWRITE, &old))
        return 0;
    p[0] = 0x33u; p[1] = 0xC0u;
    p[2] = 0x90u; p[3] = 0x90u; p[4] = 0x90u; p[5] = 0x90u;
    VirtualProtect(p, 6, old, &old);
    FlushInstructionCache(GetCurrentProcess(), p, 6);
    InterlockedExchange(&g_anti_afk_patched, 1);
    LogLine("anti-afk: patched idle sub @ RVA 0x12B251 (xor eax,eax)");
    return 1;
}

static void RestoreAfkIdlePatch(void)
{
    uint8_t* p;
    DWORD old = 0;
    if (!g_ascension || !g_afk_sub_have_saved || !g_anti_afk_patched)
        return;
    p = g_ascension + kRvaAfkIdleSub;
    if (!VirtualProtect(p, 6, PAGE_EXECUTE_READWRITE, &old))
        return;
    memcpy(p, g_afk_sub_saved, 6);
    VirtualProtect(p, 6, old, &old);
    FlushInstructionCache(GetCurrentProcess(), p, 6);
    InterlockedExchange(&g_anti_afk_patched, 0);
}

void ProxyGetAntiAfk(AntiAfkStatus* out)
{
    DWORD last;
    if (!out) return;
    memset(out, 0, sizeof(*out));
    out->enabled = g_anti_afk_enabled ? 1u : 0u;
    out->interval_ms = g_anti_afk_interval_ms;
    out->pulse_count = (uint32_t)g_anti_afk_pulses;
    last = g_anti_afk_last_pulse;
    out->last_pulse_ms = last ? (GetTickCount() - last) : 0u;
    out->patched = g_anti_afk_patched ? 1u : 0u;
    out->have_lha = AntiAfkLhaPtr() ? 1u : 0u;
}

void ProxySetAntiAfk(uint32_t enabled, uint32_t interval_ms)
{
    InterlockedExchange(&g_anti_afk_enabled, enabled ? 1 : 0);
    if (interval_ms) {
        if (interval_ms < kAntiAfkMinMs) interval_ms = kAntiAfkMinMs;
        if (interval_ms > kAntiAfkMaxMs) interval_ms = kAntiAfkMaxMs;
        InterlockedExchange((volatile LONG*)&g_anti_afk_interval_ms, (LONG)interval_ms);
    }
    if (enabled) {
        InstallAfkIdlePatch();
        ProxyAntiAfkPulse(1);
    }
}

static DWORD WINAPI NudgeThread(LPVOID param)
{
    (void)param;
    LogLine("anti-afk: nudge thread started (LHA pulse + idle patch)");
    while (!g_stop) {
        if (g_anti_afk_enabled) {
            if (!g_anti_afk_patched)
                InstallAfkIdlePatch();
            ProxyAntiAfkPulse(0);
        }

        ProxyAutoAcceptPulse();
        Sleep(400);
    }
    return 0;
}

static void CcFillDummyAppear(int race, int classId, int sex)
{
    memset(g_cc_dummy_appear, 0, sizeof(g_cc_dummy_appear));
    if (race < 1) race = 1;
    if (classId < 1) classId = 1;
    if (sex < 0) sex = 0;
    /* Offsets consumed by CreateCharacter_impl @ RVA 0xE03EB / SetClassOnObj */
    g_cc_dummy_appear[0x18] = (uint8_t)race;
    g_cc_dummy_appear[0x1C] = (uint8_t)classId;
    g_cc_dummy_appear[0x24] = 0; /* face */
    g_cc_dummy_appear[0x28] = 0; /* skin */
    g_cc_dummy_appear[0x2C] = 0; /* hair color */
    g_cc_dummy_appear[0x30] = 0; /* hair style */
    g_cc_dummy_appear[0x34] = 0; /* facial hair */
    (void)sex;
}

static int InstallRealmExpansionUnlock(void)
{
    /* Force ClientConnection[+0x2F2D] = 0xFF so GetAvailableClasses /
       GetClassesForRace treat every ChrClasses.RequiredExpansion as met.
       This is the per-realm "custom / all classes" gate. */
    uint8_t** svc;
    uint8_t* conn;
    DWORD old = 0;
    if (!g_ascension)
        return 0;
    svc = (uint8_t**)(g_ascension + kRvaClientServicesPtr);
    if (!PtrReadable(svc, 4) || !*svc)
        return 0;
    conn = *svc;
    if (!PtrReadable(conn + kOffRealmExpansion, 1))
        return 0;
    if (!g_cc_realm_exp_have) {
        g_cc_realm_exp_saved = conn[kOffRealmExpansion];
        g_cc_realm_exp_have = 1;
    }
    g_cc_realm_exp_ptr = conn + kOffRealmExpansion;
    if (!VirtualProtect(g_cc_realm_exp_ptr, 1, PAGE_READWRITE, &old))
        return 0;
    *g_cc_realm_exp_ptr = 0xFFu;
    VirtualProtect(g_cc_realm_exp_ptr, 1, old, &old);
    {
        char b[96];
        _snprintf(b, sizeof(b), "charcreate: realm expansion byte 0x%02X → 0xFF (all classes)",
            g_cc_realm_exp_saved);
        LogLine(b);
    }
    return 1;
}

static void RestoreRealmExpansionUnlock(void)
{
    DWORD old = 0;
    if (!g_cc_realm_exp_have || !g_cc_realm_exp_ptr)
        return;
    if (VirtualProtect(g_cc_realm_exp_ptr, 1, PAGE_READWRITE, &old)) {
        *g_cc_realm_exp_ptr = g_cc_realm_exp_saved;
        VirtualProtect(g_cc_realm_exp_ptr, 1, old, &old);
    }
    g_cc_realm_exp_ptr = NULL;
}

static int PatchExpCheckAlwaysTrue(uint32_t rvaCmp, uint8_t* saved, int* have, int setgeIsCl)
{
    /* At cmp reg,[row+0x2C]; setge r8 → mov r8,1; nops
       Bytes: 3B ?? 2C  0F 9D C? */
    uint8_t* p = g_ascension + rvaCmp;
    DWORD old = 0;
    if (!PtrReadable(p, 6))
        return 0;
    if (p[0] != 0x3Bu || p[2] != 0x2Cu)
        return 0;
    if (!*have) {
        memcpy(saved, p, 6);
        *have = 1;
    }
    if (!VirtualProtect(p, 6, PAGE_EXECUTE_READWRITE, &old))
        return 0;
    /* mov cl/al, 1 ; nop nop nop nop */
    if (setgeIsCl) {
        p[0] = 0xB1u; p[1] = 0x01u; /* mov cl,1 */
    } else {
        p[0] = 0xB0u; p[1] = 0x01u; /* mov al,1 */
    }
    p[2] = 0x90u; p[3] = 0x90u; p[4] = 0x90u; p[5] = 0x90u;
    VirtualProtect(p, 6, old, &old);
    FlushInstructionCache(GetCurrentProcess(), p, 6);
    return 1;
}

static int InstallSetSelectedClassOobFix(void)
{
    /* SetSelectedClass OOB path: xor eax,eax; mov eax,[eax] → jmp to safe epilogue */
    uint8_t* p = g_ascension + kRvaSetSelectedClassOob;
    DWORD old = 0;
    if (!PtrReadable(p, 4))
        return 0;
    if (p[0] != 0x33u || p[1] != 0xC0u) /* xor eax,eax */
        return 0;
    if (!g_cc_class_oob_have) {
        memcpy(g_cc_class_oob_saved, p, 2);
        g_cc_class_oob_have = 1;
    }
    if (!VirtualProtect(p, 2, PAGE_EXECUTE_READWRITE, &old))
        return 0;
    p[0] = 0xEBu; p[1] = 0xF7u; /* jmp short -9 → 0xE1B38 safe ret */
    VirtualProtect(p, 2, old, &old);
    FlushInstructionCache(GetCurrentProcess(), p, 2);
    LogLine("charcreate: SetSelectedClass OOB null-deref → safe ret");
    return 1;
}

static void RestoreSetSelectedClassOobFix(void)
{
    uint8_t* p;
    DWORD old = 0;
    if (!g_cc_class_oob_have || !g_ascension)
        return;
    p = g_ascension + kRvaSetSelectedClassOob;
    if (VirtualProtect(p, 2, PAGE_EXECUTE_READWRITE, &old)) {
        memcpy(p, g_cc_class_oob_saved, 2);
        VirtualProtect(p, 2, old, &old);
        FlushInstructionCache(GetCurrentProcess(), p, 2);
    }
}

static int PatchPrologue6(uint8_t* p, const uint8_t patch[6], uint8_t* saved, int* have)
{
    DWORD old = 0;
    if (!PtrReadable(p, 8))
        return 0;
    if (!*have) {
        if (p[0] != 0x55u || p[1] != 0x8Bu || p[2] != 0xECu)
            return 0;
        memcpy(saved, p, 6);
        *have = 1;
    }
    if (!VirtualProtect(p, 6, PAGE_EXECUTE_READWRITE, &old))
        return 0;
    memcpy(p, patch, 6);
    VirtualProtect(p, 6, old, &old);
    FlushInstructionCache(GetCurrentProcess(), p, 6);
    return 1;
}

static void RestorePrologue6(uint8_t* p, const uint8_t* saved, int have)
{
    DWORD old = 0;
    if (!have || !p)
        return;
    if (VirtualProtect(p, 6, PAGE_EXECUTE_READWRITE, &old)) {
        memcpy(p, saved, 6);
        VirtualProtect(p, 6, old, &old);
        FlushInstructionCache(GetCurrentProcess(), p, 6);
    }
}

static int EnsureCharCreateGetObjStub(void)
{
    /* Shared get_obj: mov eax,[obj*]; test; if null → dummy; ret
       Does NOT write back into [obj*] (avoids destructor AV on fake object). */
    uint8_t* stub;
    uint32_t abs_obj;
    uint32_t abs_dummy;
    if (g_cc_appear_stub_ready && g_cc_appear_stub)
        return 1;
    CcFillDummyAppear(1, 1, 0);
    if (!g_cc_appear_stub) {
        g_cc_appear_stub = (uint8_t*)VirtualAlloc(NULL, 128, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (!g_cc_appear_stub)
            return 0;
    }
    stub = g_cc_appear_stub;
    abs_obj = (uint32_t)(uintptr_t)(g_ascension + kRvaCharCreateObjPtr);
    abs_dummy = (uint32_t)(uintptr_t)g_cc_dummy_appear;
    stub[0] = 0xA1u;
    memcpy(stub + 1, &abs_obj, 4);
    stub[5] = 0x85u; stub[6] = 0xC0u;
    stub[7] = 0x75u; stub[8] = 0x05u;
    stub[9] = 0xB8u;
    memcpy(stub + 10, &abs_dummy, 4);
    stub[14] = 0xC3u;
    FlushInstructionCache(GetCurrentProcess(), stub, 32);
    g_cc_appear_stub_ready = 1;
    return 1;
}

/* Cave for E0A96: if CharCreate obj NULL → safe function return; else load [obj+0x1C] and continue. */
static uint8_t* g_cc_e0a96_cave = NULL;

static int InstallE0A96SafeLoad(void)
{
    /* Fix AV @ VA 0x4E0A9B (GetAvailableClasses helper).
       Stock: A1 [obj]; 8B 40 1C
       Replace A1 with jmp cave; cave null-checks and either early-rets or loads +0x1C then jmps to 0xE0A9E. */
    uint8_t* site = g_ascension + 0xE0A96u;
    uint8_t* cont = g_ascension + 0xE0A9Eu; /* push edi */
    uint8_t* early = g_ascension + 0xE0A8Eu; /* pop esi; xor eax,eax; pop ebx; leave; ret */
    uint8_t* cave;
    uint32_t abs_obj;
    intptr_t rel;
    DWORD old = 0;

    if (!PtrReadable(site, 8))
        return 0;
    if (site[0] == 0xE9u)
        return 1; /* already */
    if (site[0] != 0xA1u)
        return 0;

    if (!g_cc_e0a96_cave) {
        g_cc_e0a96_cave = (uint8_t*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (!g_cc_e0a96_cave)
            return 0;
    }
    cave = g_cc_e0a96_cave;
    abs_obj = (uint32_t)(uintptr_t)(g_ascension + kRvaCharCreateObjPtr);

    /* cave:
         A1 obj
         85 C0
         74 08          jz early_jmp
         8B 40 1C       mov eax,[eax+0x1C]
         E9 cont
         E9 early
    */
    cave[0] = 0xA1u;
    memcpy(cave + 1, &abs_obj, 4);
    cave[5] = 0x85u; cave[6] = 0xC0u;
    cave[7] = 0x74u; cave[8] = 0x08u; /* jz +8 → early jmp at cave+17 */
    cave[9] = 0x8Bu; cave[10] = 0x40u; cave[11] = 0x1Cu;
    cave[12] = 0xE9u;
    rel = (intptr_t)cont - (intptr_t)(cave + 17);
    memcpy(cave + 13, &rel, 4);
    cave[17] = 0xE9u;
    rel = (intptr_t)early - (intptr_t)(cave + 22);
    memcpy(cave + 18, &rel, 4);
    FlushInstructionCache(GetCurrentProcess(), cave, 32);

    /* Save original A1 via null-site table if not already */
    if (!g_cc_null_site_count || g_cc_null_sites[0].rva != 0xE0A96u) {
        /* still record for restore */
        int slot = g_cc_null_site_count;
        if (slot < kCcNullSiteMax) {
            memcpy(g_cc_null_sites[slot].saved, site, 5);
            g_cc_null_sites[slot].rva = 0xE0A96u;
            g_cc_null_sites[slot].kind = 0;
            g_cc_null_sites[slot].have = 1;
            g_cc_null_site_count++;
        }
    }

    if (!VirtualProtect(site, 5, PAGE_EXECUTE_READWRITE, &old))
        return 0;
    site[0] = 0xE9u;
    rel = (intptr_t)cave - (intptr_t)(site + 5);
    memcpy(site + 1, &rel, 4);
    VirtualProtect(site, 5, old, &old);
    FlushInstructionCache(GetCurrentProcess(), site, 5);
    LogLine("charcreate: E0A96 safe-load cave ON (fixes AV 0xE0A9B)");
    return 1;
}

/* Sticky fake [B6B1A0] disabled — destructor AVs. Null sites use get_obj / caves. */
static void RestoreStickyCharCreateObj(void)
{
    g_cc_sticky_installed = 0;
}

static int PatchCharCreateNullSite(uint32_t rva, uint8_t kind)
{
    /* Replace mov r32,[B6B1A0] with call get_obj [+ xchg to target reg]. */
    uint8_t* site;
    uint8_t expect0, expect1 = 0;
    int need;
    DWORD old = 0;
    intptr_t rel;
    int slot;

    if (!g_ascension || !EnsureCharCreateGetObjStub())
        return 0;
    site = g_ascension + rva;
    if (!PtrReadable(site, 8))
        return 0;
    /* Already our call stub? */
    if (site[0] == 0xE8u)
        return 1;

    if (kind == 0) { expect0 = 0xA1u; need = 5; }
    else if (kind == 1) { expect0 = 0x8Bu; expect1 = 0x0Du; need = 6; }
    else if (kind == 2) { expect0 = 0x8Bu; expect1 = 0x15u; need = 6; }
    else if (kind == 3) { expect0 = 0x8Bu; expect1 = 0x1Du; need = 6; }
    else return 0;

    if (site[0] != expect0)
        return 0;
    if (kind != 0 && site[1] != expect1)
        return 0;

    if (g_cc_null_site_count >= kCcNullSiteMax)
        return 0;
    slot = g_cc_null_site_count;
    memcpy(g_cc_null_sites[slot].saved, site, (size_t)need);
    g_cc_null_sites[slot].rva = rva;
    g_cc_null_sites[slot].kind = kind;
    g_cc_null_sites[slot].have = 1;

    if (!VirtualProtect(site, 6, PAGE_EXECUTE_READWRITE, &old))
        return 0;
    site[0] = 0xE8u;
    rel = (intptr_t)g_cc_appear_stub - (intptr_t)(site + 5);
    memcpy(site + 1, &rel, 4);
    if (kind == 0) {
        /* 5-byte call replaces 5-byte A1 — done */
    } else if (kind == 1) {
        site[5] = 0x91u; /* xchg ecx, eax */
    } else if (kind == 2) {
        site[5] = 0x92u; /* xchg edx, eax */
    } else {
        site[5] = 0x93u; /* xchg ebx, eax */
    }
    VirtualProtect(site, 6, old, &old);
    FlushInstructionCache(GetCurrentProcess(), site, 6);
    g_cc_null_site_count++;
    return 1;
}

static int InstallCharCreateAppearGuard(void)
{
    /* Crash lab: [NULL+0x1C] — CreateCharacter, SetSelectedClass, GetAvailableClasses (0xE0A9B), … */
    static const struct { uint32_t rva; uint8_t kind; } kSites[] = {
        { 0xE03EBu, 0 },
        { 0xE20DFu, 1 },
        { 0xE2118u, 1 },
        { 0xE22F1u, 1 },
        { 0xE09AEu, 1 },
        { 0xE1556u, 1 },
        { 0xE15A6u, 1 },
        { 0xE19D4u, 1 },
        { 0xE0A33u, 0 },
        /* 0xE0A96 handled by InstallE0A96SafeLoad (early-out cave) */
        { 0xE2433u, 0 },
        { 0xE086Bu, 2 }, /* GetAvailableClasses */
        { 0xE18C9u, 2 },
        { 0xE2212u, 2 },
        { 0xE1227u, 3 },
        { 0xE0673u, 2 },
    };
    int i, n = 0;
    char b[128];

    /* Idempotent: already patched — done */
    if (g_cc_null_site_count > 0 && g_cc_e0a96_cave) {
        return 1;
    }
    if (!EnsureCharCreateGetObjStub()) {
        LogLine("charcreate: get_obj stub alloc failed");
        return 0;
    }
    for (i = 0; i < (int)(sizeof(kSites) / sizeof(kSites[0])); i++) {
        if (PatchCharCreateNullSite(kSites[i].rva, kSites[i].kind))
            n++;
    }
    if (InstallE0A96SafeLoad())
        n++;
    g_cc_appear_have = n > 0 ? 1 : 0;
    _snprintf(b, sizeof(b), "charcreate: NULL-guards ON sites=%d (E0A9B cave=%d)", n, g_cc_e0a96_cave ? 1 : 0);
    LogLine(b);
    return n > 0 ? 1 : 0;
}

static void RestoreCharCreateAppearGuard(void)
{
    int i;
    DWORD old = 0;
    if (!g_ascension)
        return;
    for (i = 0; i < g_cc_null_site_count; i++) {
        uint8_t* site;
        int need;
        if (!g_cc_null_sites[i].have)
            continue;
        site = g_ascension + g_cc_null_sites[i].rva;
        need = (g_cc_null_sites[i].kind == 0) ? 5 : 6;
        if (VirtualProtect(site, 6, PAGE_EXECUTE_READWRITE, &old)) {
            memcpy(site, g_cc_null_sites[i].saved, (size_t)need);
            VirtualProtect(site, 6, old, &old);
            FlushInstructionCache(GetCurrentProcess(), site, 6);
        }
        g_cc_null_sites[i].have = 0;
    }
    g_cc_null_site_count = 0;
    g_cc_appear_have = 0;
    RestoreStickyCharCreateObj();
}

static int InstallCharCreateMasks(void)
{
    /* Force race→class bitmasks to 0xFFFFFFFF.
       CRITICAL: only kCcRaceClassMaskDwords (10). Writing 12+ dwords stomps the
       class-list std::vector at VA 0xB6B238 (size/cap/buf → 0xFFFFFFFF) and
       AVs on login at 0x4E3893 reading [0xFFFFFFFF+0x18C] == 0x18B. */
    uint8_t* p = g_ascension + kRvaRaceClassMasks;
    DWORD old = 0;
    int i;
    if (!PtrReadable(p, (size_t)kCcRaceClassMaskBytes))
        return 0;
    if (!g_cc_masks_have) {
        memcpy(g_cc_masks_saved, p, (size_t)kCcRaceClassMaskBytes);
        g_cc_masks_have = 1;
    }
    if (!VirtualProtect(p, kCcRaceClassMaskBytes, PAGE_READWRITE, &old))
        return 0;
    for (i = 0; i < (int)kCcRaceClassMaskDwords; i++)
        ((uint32_t*)p)[i] = 0xFFFFFFFFu;
    VirtualProtect(p, kCcRaceClassMaskBytes, old, &old);
    LogLine("charcreate: race/class bitmasks → all bits set (10 dwords, vector-safe)");
    return 1;
}

static void RestoreCharCreateMasks(void)
{
    uint8_t* p;
    DWORD old = 0;
    if (!g_cc_masks_have || !g_ascension)
        return;
    p = g_ascension + kRvaRaceClassMasks;
    if (VirtualProtect(p, kCcRaceClassMaskBytes, PAGE_READWRITE, &old)) {
        memcpy(p, g_cc_masks_saved, (size_t)kCcRaceClassMaskBytes);
        VirtualProtect(p, kCcRaceClassMaskBytes, old, &old);
    }
}

static int InstallCharCreateUnlock(void)
{
    static const uint8_t kPatchValidateName[6] = { 0xB8u, 0x57u, 0x00u, 0x00u, 0x00u, 0xC3u }; /* mov eax,0x57; ret */
    static const uint8_t kPatchRestricted[6]  = { 0xD9u, 0xEEu, 0xC3u, 0x90u, 0x90u, 0x90u }; /* fldz; ret */
    static const uint8_t kPatchValid[6]       = { 0xB8u, 0x01u, 0x00u, 0x00u, 0x00u, 0xC3u }; /* mov eax,1; ret */
    static const uint8_t kPatchAllowed[6]     = { 0xB0u, 0x01u, 0xC3u, 0x90u, 0x90u, 0x90u }; /* mov al,1; ret */
    int ok = 0;
    if (!g_ascension)
        return 0;

    /* Crash guards FIRST — Glue UI can AV before any Lua unlock. */
    if (InstallCharCreateAppearGuard())
        ok++;
    else
        LogLine("charcreate: appear/null guards FAILED");

    if (PatchPrologue6(g_ascension + kRvaValidateName, kPatchValidateName,
                       g_validate_name_saved, &g_validate_name_have))
        ok++;
    else
        LogLine("charcreate: ValidateName patch skipped");

    if (PatchPrologue6(g_ascension + kRvaIsRaceClassRestricted, kPatchRestricted,
                       g_raceclass_saved, &g_raceclass_have))
        ok++;
    else
        LogLine("charcreate: IsRaceClassRestricted patch skipped");

    if (PatchPrologue6(g_ascension + kRvaIsRaceClassValid, kPatchValid,
                       g_raceclass_valid_saved, &g_raceclass_valid_have))
        ok++;
    else
        LogLine("charcreate: IsRaceClassValid patch skipped");

    if (PatchPrologue6(g_ascension + kRvaRaceClassAllowed, kPatchAllowed,
                       g_raceclass_allowed_saved, &g_raceclass_allowed_have))
        ok++;
    else
        LogLine("charcreate: RaceClassAllowed patch skipped");

    InstallCharCreateMasks();
    InstallRealmExpansionUnlock();
    if (PatchExpCheckAlwaysTrue(kRvaAvailClassExpCheck, g_cc_expcheck_saved, &g_cc_expcheck_have, 1))
        ok++;
    if (PatchExpCheckAlwaysTrue(kRvaClassesForRaceExpChk, g_cc_expcheck2_saved, &g_cc_expcheck2_have, 0))
        ok++;
    InstallSetSelectedClassOobFix();
    CcInstallSafeSelectWrappers();

    InterlockedExchange(&g_charcreate_unlock, ok > 0 ? 1 : 0);
    {
        char b[192];
        _snprintf(b, sizeof(b),
            "charcreate: unlock ON ok=%d (crash-guards first + realm/class)",
            ok);
        LogLine(b);
    }
    return ok > 0 ? 1 : 0;
}

static void RestoreCharCreateUnlock(void)
{
    DWORD old = 0;
    if (!g_ascension)
        return;
    RestorePrologue6(g_ascension + kRvaValidateName, g_validate_name_saved, g_validate_name_have);
    RestorePrologue6(g_ascension + kRvaIsRaceClassRestricted, g_raceclass_saved, g_raceclass_have);
    RestorePrologue6(g_ascension + kRvaIsRaceClassValid, g_raceclass_valid_saved, g_raceclass_valid_have);
    RestorePrologue6(g_ascension + kRvaRaceClassAllowed, g_raceclass_allowed_saved, g_raceclass_allowed_have);
    RestoreCharCreateMasks();
    RestoreCharCreateAppearGuard();
    RestoreRealmExpansionUnlock();
    RestoreSetSelectedClassOobFix();
    if (g_cc_expcheck_have) {
        uint8_t* p = g_ascension + kRvaAvailClassExpCheck;
        if (VirtualProtect(p, 6, PAGE_EXECUTE_READWRITE, &old)) {
            memcpy(p, g_cc_expcheck_saved, 6);
            VirtualProtect(p, 6, old, &old);
            FlushInstructionCache(GetCurrentProcess(), p, 6);
        }
    }
    if (g_cc_expcheck2_have) {
        uint8_t* p = g_ascension + kRvaClassesForRaceExpChk;
        if (VirtualProtect(p, 6, PAGE_EXECUTE_READWRITE, &old)) {
            memcpy(p, g_cc_expcheck2_saved, 6);
            VirtualProtect(p, 6, old, &old);
            FlushInstructionCache(GetCurrentProcess(), p, 6);
        }
    }
    InterlockedExchange(&g_charcreate_unlock, 0);
    LogLine("charcreate: unlock OFF (restored)");
}

/* Live CharCreate UI / preview asserts on OOB race (ERROR #134 SetSelectedRace(20)).
 * Stock races 1..11; Ascension custom classes often 12..31. Appear bytes >~12 explode models. */
enum {
    kCcLiveMaxRace = 11,
    kCcLiveMaxClass = 31,
    kCcLiveMaxAppear = 12
};

static int CcClampInt(int v, int lo, int hi)
{
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

static void CcClampLiveUi(int* race, int* classId, int* sex,
                          int* skin, int* face, int* hs, int* hc, int* fac)
{
    if (race) *race = CcClampInt(*race, 1, kCcLiveMaxRace);
    if (classId) *classId = CcClampInt(*classId, 1, kCcLiveMaxClass);
    if (sex) *sex = CcClampInt(*sex, 0, 1);
    if (skin) *skin = CcClampInt(*skin, 0, kCcLiveMaxAppear);
    if (face) *face = CcClampInt(*face, 0, kCcLiveMaxAppear);
    if (hs) *hs = CcClampInt(*hs, 0, kCcLiveMaxAppear);
    if (hc) *hc = CcClampInt(*hc, 0, kCcLiveMaxAppear);
    if (fac) *fac = CcClampInt(*fac, 0, kCcLiveMaxAppear);
}

static void CcInstallSafeSelectWrappers(void)
{
    /* Intercept Glue SetSelected* so lab/scripts cannot FATAL on race 20 etc. */
    static const char kWrap[] =
        "if not Gm_CC_SafeSelect then "
        "Gm_CC_SafeSelect=true "
        "local function clamp(v,lo,hi) v=tonumber(v) or lo; if v<lo then return lo end; if v>hi then return hi end; return math.floor(v) end "
        "if type(SetSelectedRace)=='function' then local o=SetSelectedRace; "
        "SetSelectedRace=function(r) return o(clamp(r,1,11)) end end "
        "if type(SetSelectedClass)=='function' then local o=SetSelectedClass; "
        "SetSelectedClass=function(c) return o(clamp(c,1,31)) end end "
        "if type(SetSelectedSex)=='function' then local o=SetSelectedSex; "
        "SetSelectedSex=function(s) return o(clamp(s,0,1)) end end "
        "if DEFAULT_CHAT_FRAME then DEFAULT_CHAT_FRAME:AddMessage('|cff2ecc71[CharCreate]|r safe SetSelected* wrappers ON') end "
        "end";
    LuaQueueEnqueue(kWrap, (uint32_t)strlen(kWrap));
}

static void CcPokeAppear(uint8_t* obj, int race, int classId, int sex,
                         int skin, int face, int hairStyle, int hairColor, int facial)
{
    if (!obj)
        return;
    if (race >= 0) obj[0x18] = (uint8_t)race;
    if (classId >= 0) {
        /* class stored as dword at +0x1C in several paths */
        *(uint32_t*)(obj + 0x1C) = (uint32_t)(uint8_t)classId;
        obj[0x1C] = (uint8_t)classId;
    }
    if (face >= 0) obj[0x24] = (uint8_t)face;
    if (skin >= 0) obj[0x28] = (uint8_t)skin;
    if (hairColor >= 0) obj[0x2C] = (uint8_t)hairColor;
    if (hairStyle >= 0) obj[0x30] = (uint8_t)hairStyle;
    if (facial >= 0) obj[0x34] = (uint8_t)facial;
    if (sex >= 0 && g_ascension) {
        uint32_t* sexp = (uint32_t*)(g_ascension + kRvaSelectedSex);
        if (PtrReadable(sexp, 4))
            *sexp = (uint32_t)sex;
    }
}

static int __cdecl GmCharCreateForce_Lua(void* L)
{
    /* GmCharCreateForce(race, class, sex [, skin, face, hairStyle, hairColor, facial]) */
    int race = (int)LuaArgNum(L, 1);
    int classId = (int)LuaArgNum(L, 2);
    int sex = (int)LuaArgNum(L, 3);
    int skin = (int)LuaArgNum(L, 4);
    int face = (int)LuaArgNum(L, 5);
    int hairStyle = (int)LuaArgNum(L, 6);
    int hairColor = (int)LuaArgNum(L, 7);
    int facial = (int)LuaArgNum(L, 8);
    uint8_t* obj = NULL;
    int live = 0;
    int wantRace = race, wantClass = classId;

    InstallRealmExpansionUnlock(); /* refresh if connection appeared after unlock */

    if (race < 0) race = 1;
    if (classId < 0) classId = 1;
    if (sex < 0) sex = 0;

    if (g_ascension) {
        uint8_t** slot = (uint8_t**)(g_ascension + kRvaCharCreateObjPtr);
        if (PtrReadable(slot, 4) && *slot && PtrReadable(*slot, 0x38)) {
            obj = *slot;
            live = 1;
        }
    }

    /* LIVE object drives model preview — must stay in ChrRaces/ChrClasses range. */
    if (live)
        CcClampLiveUi(&race, &classId, &sex, &skin, &face, &hairStyle, &hairColor, &facial);

    CcFillDummyAppear(race, classId, sex);
    if (skin > 0 || face > 0 || hairStyle > 0 || hairColor > 0 || facial > 0 ||
        LuaArgNum(L, 4) != 0.0 || LuaArgNum(L, 5) != 0.0) {
        g_cc_dummy_appear[0x24] = (uint8_t)face;
        g_cc_dummy_appear[0x28] = (uint8_t)skin;
        g_cc_dummy_appear[0x2C] = (uint8_t)hairColor;
        g_cc_dummy_appear[0x30] = (uint8_t)hairStyle;
        g_cc_dummy_appear[0x34] = (uint8_t)facial;
    }
    CcPokeAppear(obj ? obj : g_cc_dummy_appear, race, classId, sex, skin, face, hairStyle, hairColor, facial);
    if (live && (wantRace != race || wantClass != classId)) {
        char b[128];
        _snprintf(b, sizeof(b),
            "charcreate: Force clamped live want r/c=%d/%d -> %d/%d (preview-safe)",
            wantRace, wantClass, race, classId);
        LogLine(b);
    }
    LuaPushNum(L, live ? 1.0 : 0.0);
    LuaPushNum(L, (double)race);
    LuaPushNum(L, (double)classId);
    return 3;
}

static int __cdecl GmCharCreateChaos_Lua(void* L)
{
    /* Chaos lab: randomize, but NEVER poke OOB into live CharCreate (FATAL #134 preview). */
    unsigned seed = (unsigned)LuaArgNum(L, 1);
    int race, classId, sex, skin, face, hs, hc, fac;
    int wantRace, wantClass;
    uint8_t* obj = NULL;
    int live = 0;
    if (seed == 0)
        seed = (unsigned)GetTickCount();
    race = (int)(seed % 32);
    classId = (int)((seed >> 3) % 40);
    sex = (int)((seed >> 7) % 3);
    skin = (int)((seed >> 2) % 256);
    face = (int)((seed >> 5) % 256);
    hs = (int)((seed >> 9) % 256);
    hc = (int)((seed >> 11) % 256);
    fac = (int)((seed >> 13) % 256);
    wantRace = race;
    wantClass = classId;
    InstallCharCreateUnlock();

    if (g_ascension) {
        uint8_t** slot = (uint8_t**)(g_ascension + kRvaCharCreateObjPtr);
        if (PtrReadable(slot, 4) && *slot && PtrReadable(*slot, 0x38)) {
            obj = *slot;
            live = 1;
        }
    }
    if (live || !obj) {
        /* Always sanitize values applied to UI / CreateCharacter path. */
        CcClampLiveUi(&race, &classId, &sex, &skin, &face, &hs, &hc, &fac);
    }
    CcFillDummyAppear(race, classId, sex);
    CcPokeAppear(obj ? obj : g_cc_dummy_appear, race, classId, sex, skin, face, hs, hc, fac);
    {
        char b[160];
        _snprintf(b, sizeof(b),
            "charcreate: CHAOS want r/c=%d/%d -> live %d/%d sex=%d appear=%d/%d/%d/%d/%d",
            wantRace, wantClass, race, classId, sex, skin, face, hs, hc, fac);
        LogLine(b);
    }
    LuaPushNum(L, (double)race);
    LuaPushNum(L, (double)classId);
    LuaPushNum(L, (double)sex);
    LuaPushNum(L, (double)skin);
    LuaPushNum(L, (double)face);
    LuaPushNum(L, (double)hs);
    return 6;
}

static int __cdecl GmCharCreateUnlock_Lua(void* L)
{
    const char* s = LuaArgStr(L, 1);
    double n = LuaArgNum(L, 1);
    int want = -1;
    if (s && s[0]) {
        if (_strnicmp(s, "off", 3) == 0 || _strnicmp(s, "false", 5) == 0 || (s[0] == '0' && !s[1]))
            want = 0;
        else
            want = 1;
    } else if (n == 0.0 && !(s && s[0])) {
        want = -1; /* status only */
    } else {
        want = (n != 0.0) ? 1 : 0;
    }
    if (want == 1)
        InstallCharCreateUnlock();
    else if (want == 0)
        RestoreCharCreateUnlock();
    LuaPushNum(L, (double)g_charcreate_unlock);
    LuaPushNum(L, (double)g_validate_name_have);
    LuaPushNum(L, (double)(g_raceclass_have + g_raceclass_valid_have + g_raceclass_allowed_have));
    return 3;
}

static int __cdecl GmAntiAfk_Lua(void* L)
{
    AntiAfkStatus st;
    const char* s = LuaArgStr(L, 1);
    if (s && s[0]) {
        uint32_t on = 1u;
        uint32_t iv = (uint32_t)LuaArgNum(L, 2);
        if (s[0] >= '0' && s[0] <= '9')
            on = (LuaArgNum(L, 1) != 0.0) ? 1u : 0u;
        else if (_strnicmp(s, "off", 3) == 0 || _strnicmp(s, "false", 5) == 0)
            on = 0u;
        else
            on = 1u;
        ProxySetAntiAfk(on, iv);
    }
    ProxyGetAntiAfk(&st);
    LuaPushNum(L, (double)st.enabled);
    LuaPushNum(L, (double)st.pulse_count);
    LuaPushNum(L, (double)st.last_pulse_ms);
    LuaPushNum(L, (double)st.patched);
    LuaPushNum(L, (double)st.have_lha);
    return 5;
}

static void __cdecl RecvSniffThunk(void* ctx, void* packet)
{
    LONG n = InterlockedIncrement(&g_in_hook_calls);
    if (ctx && PtrReadable(ctx, 0x20u)) {
        InterlockedExchangePointer(&g_last_recv_ctx, ctx);
        g_last_recv_ctx_tick = GetTickCount();
    }
    if (packet && PtrReadable(packet, sizeof(CDataStore))) {
        CDataStore* ds = (CDataStore*)packet;

        if (ds->buffer && ds->size >= 4u && PtrReadable(ds->buffer, ds->size))
            ApplyTpLockToPacket(ds->buffer, ds->size, 1);
        SniffStoreDir(ds, "recv", kPktDirIn);
    }
    ProxyDrainInjectIncoming();
    if ((n % 10000) == 0) {
        static DWORD s_last_recv_log;
        DWORD now = GetTickCount();
        if (now - s_last_recv_log >= 30000u) {
            char b[80];
            s_last_recv_log = now;
            _snprintf(b, sizeof(b), "recv_hooks=%ld sniffs=%ld", (long)n, (long)g_sniff_writes);
            LogLine(b);
        }
    }
}

static int InstallIncomingHook(void)
{
    uint8_t* target;
    uint8_t* d;
    DWORD old_prot;
    intptr_t rel;
    uint32_t thunk_addr;
    char msg[96];

    if (!g_real_base || g_in_hooked)
        return g_in_hooked;
    target = g_real_base + g_off.ext_process_incoming;

    if (target[0] != 0x55 || target[1] != 0x8B || target[2] != 0xEC
        || target[3] != 0x6A || target[4] != 0xFF || target[5] != 0x68) {
        _snprintf(msg, sizeof(msg), "recv hook FAIL prologue %02X %02X %02X %02X %02X %02X",
            target[0], target[1], target[2], target[3], target[4], target[5]);
        LogLine(msg);
        return 0;
    }
    g_in_stolen_len = 10;
    memcpy(g_in_stolen, target, g_in_stolen_len);


    g_in_stub = (uint8_t*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!g_in_stub)
        return 0;
    memcpy(g_in_stub, g_in_stolen, g_in_stolen_len);
    g_in_stub[g_in_stolen_len] = 0xE9;
    rel = (intptr_t)(target + g_in_stolen_len) - (intptr_t)(g_in_stub + g_in_stolen_len + 5);
    memcpy(g_in_stub + g_in_stolen_len + 1, &rel, 4);


    g_in_detour = (uint8_t*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!g_in_detour) {
        VirtualFree(g_in_stub, 0, MEM_RELEASE);
        g_in_stub = NULL;
        return 0;
    }
    thunk_addr = (uint32_t)(uintptr_t)&RecvSniffThunk;
    d = g_in_detour;
    *d++ = 0x60;
    *d++ = 0x9C;


    *d++ = 0xFF; *d++ = 0x74; *d++ = 0x24; *d++ = 0x2C;
    *d++ = 0xFF; *d++ = 0x74; *d++ = 0x24; *d++ = 0x2C;
    *d++ = 0xB8;
    memcpy(d, &thunk_addr, 4); d += 4;
    *d++ = 0xFF; *d++ = 0xD0;
    *d++ = 0x83; *d++ = 0xC4; *d++ = 0x08;
    *d++ = 0x9D;
    *d++ = 0x61;
    *d++ = 0xE9;
    rel = (intptr_t)g_in_stub - (intptr_t)(d + 4);
    memcpy(d, &rel, 4); d += 4;


    if (!VirtualProtect(target, 16, PAGE_EXECUTE_READWRITE, &old_prot)) {
        VirtualFree(g_in_stub, 0, MEM_RELEASE); g_in_stub = NULL;
        VirtualFree(g_in_detour, 0, MEM_RELEASE); g_in_detour = NULL;
        LogLine("recv hook FAIL VirtualProtect");
        return 0;
    }
    target[0] = 0xE9;
    rel = (intptr_t)g_in_detour - (intptr_t)(target + 5);
    memcpy(target + 1, &rel, 4);
    memset(target + 5, 0x90, 5);
    VirtualProtect(target, 16, old_prot, &old_prot);
    FlushInstructionCache(GetCurrentProcess(), target, 16);
    FlushInstructionCache(GetCurrentProcess(), g_in_stub, 64);
    FlushInstructionCache(GetCurrentProcess(), g_in_detour, 64);
    g_in_hooked = 1;
    LogLine("recv hook ok (ProcessIncomingPacket, jmp-through detour)");
    return 1;
}

static void RemoveHooks(void)
{
    DWORD old_prot;
    if (g_msg_hook) {
        UnhookWindowsHookEx(g_msg_hook);
        g_msg_hook = NULL;
    }
    if (g_cwp_hook) {
        UnhookWindowsHookEx(g_cwp_hook);
        g_cwp_hook = NULL;
    }
    if (g_hooked && g_ascension) {
        uint8_t* target = g_ascension + kPacketQueueRva;
        if (VirtualProtect(target, 16, PAGE_EXECUTE_READWRITE, &old_prot)) {
            memcpy(target, g_queue_stolen, g_queue_stolen_len);
            VirtualProtect(target, 16, old_prot, &old_prot);
        }
        g_hooked = 0;
    }
    if (g_send_hooked && g_ascension) {
        uint8_t* target = g_ascension + kNetClientSendRva;
        if (VirtualProtect(target, 16, PAGE_EXECUTE_READWRITE, &old_prot)) {
            memcpy(target, g_send_stolen, g_send_stolen_len);
            VirtualProtect(target, 16, old_prot, &old_prot);
        }
        g_send_hooked = 0;
    }
    if (g_in_hooked && g_real_base) {
        uint8_t* target = g_real_base + g_off.ext_process_incoming;
        if (VirtualProtect(target, 16, PAGE_EXECUTE_READWRITE, &old_prot)) {
            memcpy(target, g_in_stolen, g_in_stolen_len);
            VirtualProtect(target, 16, old_prot, &old_prot);
        }
        g_in_hooked = 0;
    }
    if (g_cast_by_name_hooked && g_ascension) {
        uint8_t* target = g_ascension + kCastSpellByNameLuaRva;
        if (VirtualProtect(target, 16, PAGE_EXECUTE_READWRITE, &old_prot)) {
            memcpy(target, g_cast_by_name_stolen, g_cast_by_name_stolen_len);
            VirtualProtect(target, 16, old_prot, &old_prot);
        }
        g_cast_by_name_hooked = 0;
    }
    if (g_cast_by_id_hooked && g_ascension) {
        uint8_t* target = g_ascension + kCastSpellByIdLuaRva;
        if (VirtualProtect(target, 16, PAGE_EXECUTE_READWRITE, &old_prot)) {
            memcpy(target, g_cast_by_id_stolen, g_cast_by_id_stolen_len);
            VirtualProtect(target, 16, old_prot, &old_prot);
        }
        g_cast_by_id_hooked = 0;
    }
    if (g_queue_stub) {
        VirtualFree(g_queue_stub, 0, MEM_RELEASE);
        g_queue_stub = NULL;
        g_queue_tramp = NULL;
    }
    if (g_send_stub) {
        VirtualFree(g_send_stub, 0, MEM_RELEASE);
        g_send_stub = NULL;
    }
    if (g_in_stub) {
        VirtualFree(g_in_stub, 0, MEM_RELEASE);
        g_in_stub = NULL;
    }
    if (g_in_detour) {
        VirtualFree(g_in_detour, 0, MEM_RELEASE);
        g_in_detour = NULL;
    }
    if (g_cast_by_name_stub) {
        VirtualFree(g_cast_by_name_stub, 0, MEM_RELEASE);
        g_cast_by_name_stub = NULL;
        g_cast_by_name_tramp = NULL;
    }
    if (g_cast_by_id_stub) {
        VirtualFree(g_cast_by_id_stub, 0, MEM_RELEASE);
        g_cast_by_id_stub = NULL;
        g_cast_by_id_tramp = NULL;
    }
}

static int TryLoadExtensionsFromDir(const wchar_t* dir)
{
    wchar_t path[MAX_PATH];
    size_t n;
    if (!dir || !dir[0])
        return 0;
    n = wcslen(dir);
    if (n + 16 >= MAX_PATH)
        return 0;
    lstrcpyW(path, dir);
    if (path[n - 1] != L'\\' && path[n - 1] != L'/')
        lstrcatW(path, L"\\");
    lstrcatW(path, L"Extensions.dll");
    g_real = LoadLibraryW(path);
    if (!g_real)
        return 0;
    g_real_base = (uint8_t*)g_real;
    return 1;
}

static int LoadRealExtensions(void)
{
    wchar_t path[MAX_PATH];
    wchar_t* slash;
    DWORD n;

    /* 1) Beside ExtProxy64.dll (legacy: both lived in the game folder). */
    n = GetModuleFileNameW(g_self, path, MAX_PATH);
    if (n > 0 && n < MAX_PATH) {
        slash = wcsrchr(path, L'\\');
        if (slash) {
            slash[1] = 0;
            if (TryLoadExtensionsFromDir(path))
                return 1;
        }
    }

    /* 2) Process cwd — AscensionBoot sets this to the stock game live dir. */
    n = GetCurrentDirectoryW(MAX_PATH, path);
    if (n > 0 && n < MAX_PATH && TryLoadExtensionsFromDir(path))
        return 1;

    /* 3) Beside Ascension.launch.exe / main module (if ever co-located). */
    n = GetModuleFileNameW(NULL, path, MAX_PATH);
    if (n > 0 && n < MAX_PATH) {
        slash = wcsrchr(path, L'\\');
        if (slash) {
            slash[1] = 0;
            if (TryLoadExtensionsFromDir(path))
                return 1;
        }
    }

    return 0;
}

static void LoadExtProxyConfig(void)
{
    char path[MAX_PATH];
    char line[MAX_PATH + 96];
    FILE* f;
    DWORD n;
    char* slash;
    int loaded = 0;

    n = GetModuleFileNameA(g_self, path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH)
        return;
    slash = strrchr(path, '\\');
    if (!slash)
        return;
    slash[1] = 0;
    if (strlen(path) + 14 >= MAX_PATH)
        return;
    strcat(path, "ExtProxy.cfg");
    f = fopen(path, "r");
    if (!f) {
        ProxyLogLine("config: ExtProxy.cfg not found beside ExtProxy64.dll");
        return;
    }
    while (fgets(line, (int)sizeof(line), f)) {
        char* nl = strchr(line, '\n');
        char* cr = strchr(line, '\r');
        char* eq;
        char* key;
        char* val;
        if (nl) *nl = 0;
        if (cr) *cr = 0;
        if (line[0] == '#' || line[0] == ';' || line[0] == 0)
            continue;
        eq = strchr(line, '=');
        if (!eq)
            continue;
        *eq = 0;
        key = line;
        val = eq + 1;
        while (*key == ' ' || *key == '\t') key++;
        while (*val == ' ' || *val == '\t') val++;
        if (!val[0])
            continue;
        /* mmaps / mmtiles = directory of .mmtile files */
        if (_stricmp(key, "mmaps") == 0 || _stricmp(key, "mmtiles") == 0) {
            NavHeightSetRoot(val);
            loaded = 1;
        } else if (_stricmp(key, "maps") == 0) {
            NavMapsSetRoot(val);
            loaded = 1;
        } else if (_stricmp(key, "instance_id") == 0) {
            unsigned id = 0;
            if (sscanf(val, "%u", &id) == 1 && id > 0) {
                PktIpcSetCfgInstanceId((uint32_t)id);
                loaded = 1;
            }
        }
    }
    fclose(f);
    if (!loaded)
        ProxyLogLine("config: ExtProxy.cfg had no maps/mmaps keys");
}

static DWORD WINAPI InitThread(LPVOID param)
{
    int tries;
    (void)param;

    Sleep(200);
    LoadExtProxyConfig();
    for (tries = 0; tries < 300; ++tries) {
        g_ascension = (uint8_t*)GetModuleHandleA(NULL);
        if (g_ascension)
            break;
        Sleep(20);
    }
    if (!g_ascension) {
        LogLine("init fail: no ascension");
        return 1;
    }

    /* Validate stock RVAs / masked AOB remap BEFORE any hooks. */
    OffsetResolve_Init(g_ascension, g_real_base, LogLine);

    ObjMgrInit(g_ascension);
    FogInit(g_ascension);

    g_fn_reset = g_ascension + kCDataStoreResetReadRva;
    if (g_real_base) {
        void* r = *(void**)(g_real_base + kExtResetFnPtrRva);
        /* Must look like Ascension CDataStore::ResetRead (mov dword[ecx+14],0; ret)
         * or any readable executable — never adopt .text garbage after .data slid. */
        if (r && PtrReadable(r, 4)) {
            const uint8_t* b = (const uint8_t*)r;
            if ((b[0] == 0xC7 && b[1] == 0x41 && b[2] == 0x14)
                || (b[0] == 0x55 && b[1] == 0x8B && b[2] == 0xEC)
                || b[0] == 0xE9) {
                g_fn_reset = r;
            } else {
                LogLine("reset-fn ptr rejected (bad prologue) — using Ascension CDataStore::ResetRead");
            }
        }
    }

    if (!InstallSendHook())
        LogLine("send hook FAIL (inject/fly patch need Send)");
    if (!InstallQueueHook())
        LogLine("queue hook FAIL");

    if (!InstallIncomingHook())
        LogLine("recv hook FAIL (inbound sniff limited to arrival markers)");
    if (!InstallUnrestrictProtectedApis())
        LogLine("unrestrict protected APIs FAIL (partial)");

    /* Init: ONLY the E0A96 early-out cave. Full unlock (masks/realm/exp) must
     * wait for explicit GmCharCreateUnlock — InstallCharCreateMasks previously
     * wrote 16 dwords and stomped class UI vector @ VA 0xB6B238 → login AV. */
    if (InstallE0A96SafeLoad())
        LogLine("charcreate: E0A96 safe-load cave ON at init (full unlock deferred)");
    else
        LogLine("charcreate: E0A96 cave at init skipped");

    LogLine("lua CastSpellByName/ID hooks skipped (gates + stay-unlocked)");
    ForceClearTaint();
    {
        char um[128];
        _snprintf(um, sizeof(um),
            "unlock: always=%ld castHooks=0 queue=%d (gates=JE→JMP)",
            (long)g_always_unrestricted, kLuaQueueSlots);
        LogLine(um);
    }

    for (tries = 0; tries < 300; ++tries) {
        if (InstallUiHook()) {
            /* Do NOT RegisterLuaApis/SeedPopupSuppress here — lua_State is often still
             * NULL when hwnd appears (AV 0xC0000005 @ RVA 0x44E408, null+0x14).
             * Wake UI; HandleAscInject/HookedSend register once ProxyLuaState()!=NULL. */
            WakeUiForInjectAsync();
            break;
        }
        Sleep(500);
    }


    InstallAfkIdlePatch();
    ProxyAntiAfkPulse(1);
    Overlay_Init();
    if (!g_nudge_thread) {
        g_nudge_thread = CreateThread(NULL, 0, NudgeThread, NULL, 0, NULL);
        if (!g_nudge_thread)
            LogLine("anti-afk: nudge thread create FAILED");
    }
    {
        AntiAfkStatus st;
        char b[160];
        ProxyGetAntiAfk(&st);
        _snprintf(b, sizeof(b),
            "anti-afk: enabled=%u patched=%u haveLha=%u interval=%ums",
            st.enabled, st.patched, st.have_lha, st.interval_ms);
        LogLine(b);
    }
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = hinst;
        DisableThreadLibraryCalls(hinst);
        InitializeCriticalSection(&g_cast_cs);
        if (!LoadRealExtensions())
            return FALSE;
        {
            char boot[80];
            _snprintf(boot, sizeof(boot), "proxy attached pid=%lu Extensions loaded",
                (unsigned long)GetCurrentProcessId());
            LogLine(boot);
        }
        PktIpcStart();
        ChatReportStart(GetCurrentProcessId());
        TeleMirrorStart();
        InstBusStart();
        {
            HANDLE t = CreateThread(NULL, 0, InitThread, NULL, 0, NULL);
            if (t)
                CloseHandle(t);
        }
    } else if (reason == DLL_PROCESS_DETACH) {
        g_stop = 1;
        Overlay_Shutdown();
        InstBusStop();
        TeleMirrorStop();
        ChatReportStop();
        PktIpcStop();
        if (g_nudge_thread) {
            if (g_hwnd)
                PostMessageW(g_hwnd, WM_NULL, 0, 0);
            WaitForSingleObject(g_nudge_thread, 1000);
            CloseHandle(g_nudge_thread);
            g_nudge_thread = NULL;
        }
        RestoreAfkIdlePatch();
        RemoveHooks();
        DeleteCriticalSection(&g_cast_cs);
        if (g_real) {
            FreeLibrary(g_real);
            g_real = NULL;
        }
    }
    return TRUE;
}

__declspec(dllexport) void __stdcall ClientExtensionsDummy(void)
{
    typedef void(__stdcall* DummyFn)(void);
    static DummyFn real_dummy = NULL;
    if (!real_dummy && g_real)
        real_dummy = (DummyFn)GetProcAddress(g_real, "ClientExtensionsDummy");
    if (real_dummy)
        real_dummy();
}
