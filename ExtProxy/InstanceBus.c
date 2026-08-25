#include "InstanceBus.h"
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <ctype.h>

void ProxyLogLine(const char* msg);
uint32_t PktIpcOwnerPid(void);
uint32_t PktIpcThisInstance(void);
int ProxyRequestRunLua(const char* script, uint32_t len);
void ProxyConsumeLuaCast(void);
void ProxyWakeUiForInject(void);

static HANDLE g_map;
static InstBusHeader* g_bus;
static CRITICAL_SECTION g_cs;
static int g_ready;
static volatile LONG g_exec_seq;
static volatile LONG g_exec_slot = -1;

static uint32_t OurPid(void)
{
    uint32_t pid = PktIpcOwnerPid();
    return pid ? pid : GetCurrentProcessId();
}

static uint32_t OurInstanceId(void)
{
    uint32_t id = PktIpcThisInstance();
    return id ? id : 1;
}

/* Caller must hold g_cs. Returns slot index. */
static uint32_t InstBusClaimSeatUnlocked(void)
{
    uint32_t id, pid, i, slot;
    if (!g_bus) return 0;
    id = OurInstanceId();
    pid = OurPid();
    slot = (id - 1u) % INST_BUS_MAX_INST;
    for (i = 0; i < INST_BUS_MAX_INST; ++i) {
        if (g_bus->dir[i].pid == pid) {
            slot = i;
            if (g_bus->dir[i].instance_id)
                id = g_bus->dir[i].instance_id;
            break;
        }
    }
    for (i = 0; i < INST_BUS_MAX_INST; ++i) {
        if (g_bus->dir[i].pid == pid && i != slot)
            memset(&g_bus->dir[i], 0, sizeof(g_bus->dir[i]));
    }
    g_bus->dir[slot].instance_id = id;
    g_bus->dir[slot].pid = pid;
    g_bus->dir[slot].tick_ms = GetTickCount();
    g_bus->dir[slot].flags |= INST_BUS_FLAG_ONLINE;
    return slot;
}

static void SanitizeName(char* dst, int cap, const char* src)
{
    int i = 0, j = 0;
    if (!dst || cap < 2) return;
    dst[0] = 0;
    if (!src) return;
    while (src[i] && j < cap - 1) {
        unsigned char c = (unsigned char)src[i++];
        if (c < 32) continue;
        dst[j++] = (char)c;
    }
    dst[j] = 0;
}

static int NameEq(const char* a, const char* b)
{
    if (!a || !b) return 0;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b))
            return 0;
        ++a; ++b;
    }
    return *a == 0 && *b == 0;
}

void InstBusStart(void)
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
        0, (DWORD)sizeof(InstBusHeader), INST_BUS_NAME);
    if (!g_map) {
        ProxyLogLine("instbus: CreateFileMapping failed");
        return;
    }
    g_bus = (InstBusHeader*)MapViewOfFile(g_map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(InstBusHeader));
    if (!g_bus) {
        ProxyLogLine("instbus: MapViewOfFile failed");
        CloseHandle(g_map);
        g_map = NULL;
        return;
    }
    if (g_bus->magic != INST_BUS_MAGIC || g_bus->version != INST_BUS_VERSION) {
        memset(g_bus, 0, sizeof(*g_bus));
        g_bus->magic = INST_BUS_MAGIC;
        g_bus->version = INST_BUS_VERSION;
    }
    g_ready = 1;
    EnterCriticalSection(&g_cs);
    InstBusClaimSeatUnlocked();
    LeaveCriticalSection(&g_cs);
    ProxyLogLine("instbus V2: ready (name dir + Lua RPC)");
}

void InstBusStop(void)
{
    uint32_t i, pid;
    if (!g_ready) return;
    EnterCriticalSection(&g_cs);
    pid = OurPid();
    if (g_bus) {
        for (i = 0; i < INST_BUS_MAX_INST; ++i) {
            if (g_bus->dir[i].pid == pid)
                memset(&g_bus->dir[i], 0, sizeof(g_bus->dir[i]));
        }
        UnmapViewOfFile(g_bus);
        g_bus = NULL;
    }
    if (g_map) { CloseHandle(g_map); g_map = NULL; }
    LeaveCriticalSection(&g_cs);
    DeleteCriticalSection(&g_cs);
    g_ready = 0;
}

void InstBusPublishName(const char* name)
{
    uint32_t slot;
    char clean[INST_BUS_NAME_LEN];
    if (!g_ready || !g_bus) return;
    SanitizeName(clean, (int)sizeof(clean), name);
    if (!clean[0]) return;

    EnterCriticalSection(&g_cs);
    slot = InstBusClaimSeatUnlocked();
    g_bus->dir[slot].flags |= INST_BUS_FLAG_ONLINE | INST_BUS_FLAG_OCCUPIED;
    memcpy(g_bus->dir[slot].name, clean, INST_BUS_NAME_LEN);
    LeaveCriticalSection(&g_cs);
}

int InstBusTryOccupy(uint32_t max_inst)
{
    uint32_t i, slot, now, count;
    int ok = 0;
    if (!g_ready || !g_bus) return 1; /* fail-open: do not disconnect if the bus is not up */
    if (max_inst < 1) max_inst = 1;
    if (max_inst > INST_BUS_MAX_INST) max_inst = INST_BUS_MAX_INST;

    EnterCriticalSection(&g_cs);
    slot = InstBusClaimSeatUnlocked();
    if (g_bus->dir[slot].flags & INST_BUS_FLAG_OCCUPIED) {
        LeaveCriticalSection(&g_cs);
        return 1;
    }
    now = GetTickCount();
    count = 0;
    for (i = 0; i < INST_BUS_MAX_INST; ++i) {
        InstBusDir* d = &g_bus->dir[i];
        if (!(d->flags & INST_BUS_FLAG_ONLINE) && !d->name[0]) continue;
        if ((now - d->tick_ms) > 15000u) continue;
        if ((d->flags & INST_BUS_FLAG_OCCUPIED) || d->name[0])
            count++;
    }
    if (count < max_inst) {
        g_bus->dir[slot].flags |= INST_BUS_FLAG_ONLINE | INST_BUS_FLAG_OCCUPIED;
        ok = 1;
    }
    LeaveCriticalSection(&g_cs);
    return ok;
}

int InstBusResolve(const char* nameOrId, uint32_t* out_instance, uint32_t* out_pid,
                   char* out_name, int name_cap)
{
    uint32_t i, now, best_age;
    int as_num;
    char* endp;
    if (!g_ready || !g_bus || !nameOrId || !nameOrId[0]) return 0;
    if (out_instance) *out_instance = 0;
    if (out_pid) *out_pid = 0;
    if (out_name && name_cap > 0) out_name[0] = 0;

    as_num = (int)strtoul(nameOrId, &endp, 10);
    if (endp != nameOrId && *endp == 0 && as_num >= 1 && as_num <= (int)INST_BUS_MAX_INST) {
        InstBusDir* d = &g_bus->dir[(uint32_t)(as_num - 1) % INST_BUS_MAX_INST];
        if (d->flags && d->instance_id == (uint32_t)as_num) {
            if (out_instance) *out_instance = d->instance_id;
            if (out_pid) *out_pid = d->pid;
            if (out_name && name_cap > 0) {
                strncpy(out_name, d->name, (size_t)name_cap - 1);
                out_name[name_cap - 1] = 0;
            }
            return 1;
        }
    }

    now = GetTickCount();
    best_age = 0xFFFFFFFFu;
    EnterCriticalSection(&g_cs);
    for (i = 0; i < INST_BUS_MAX_INST; ++i) {
        InstBusDir* d = &g_bus->dir[i];
        uint32_t age;
        if (!(d->flags & 1u) || !d->name[0]) continue;
        if (!NameEq(d->name, nameOrId)) continue;
        age = now - d->tick_ms;
        if (age > 15000u) continue; /* stale >15s */
        if (age <= best_age) {
            best_age = age;
            if (out_instance) *out_instance = d->instance_id;
            if (out_pid) *out_pid = d->pid;
            if (out_name && name_cap > 0) {
                strncpy(out_name, d->name, (size_t)name_cap - 1);
                out_name[name_cap - 1] = 0;
            }
        }
    }
    LeaveCriticalSection(&g_cs);
    return best_age != 0xFFFFFFFFu;
}

int InstBusCopyDir(InstBusDir* out, int max_n)
{
    int n = 0;
    uint32_t i, now;
    if (!g_ready || !g_bus || !out || max_n < 1) return 0;
    now = GetTickCount();
    EnterCriticalSection(&g_cs);
    for (i = 0; i < INST_BUS_MAX_INST && n < max_n; ++i) {
        InstBusDir* d = &g_bus->dir[i];
        if (!(d->flags & 1u) || !d->name[0]) continue;
        if ((now - d->tick_ms) > 15000u) continue;
        out[n++] = *d;
    }
    LeaveCriticalSection(&g_cs);
    return n;
}

static void FormatArgLiteral(char* dst, int cap, const InstBusArg* a)
{
    if (!dst || cap < 4 || !a) {
        if (dst && cap > 0) dst[0] = 0;
        return;
    }
    if (a->kind == kInstArgNumber) {
        _snprintf(dst, cap, "%.14g", a->num);
    } else if (a->kind == kInstArgString) {
        /* Escape quotes/backslashes for Lua string literal. */
        int i = 0, o = 0;
        dst[o++] = '"';
        while (a->str[i] && o < cap - 3) {
            char c = a->str[i++];
            if (c == '\\' || c == '"') {
                dst[o++] = '\\';
                if (o >= cap - 2) break;
            }
            if ((unsigned char)c < 32) continue;
            dst[o++] = c;
        }
        dst[o++] = '"';
        dst[o] = 0;
    } else {
        strncpy(dst, "nil", (size_t)cap - 1);
        dst[cap - 1] = 0;
    }
}

static int BuildRpcScript(char* out, int cap, const InstBusRpc* rpc)
{
    char lit[INST_BUS_MAX_ARGS][128];
    char args[640];
    uint32_t i;
    int n;
    if (!out || cap < 64 || !rpc || !rpc->fn[0]) return 0;
    args[0] = 0;
    for (i = 0; i < rpc->argc && i < INST_BUS_MAX_ARGS; ++i) {
        FormatArgLiteral(lit[i], (int)sizeof(lit[i]), &rpc->args[i]);
        if (i) strncat(args, ",", sizeof(args) - strlen(args) - 1);
        strncat(args, lit[i], sizeof(args) - strlen(args) - 1);
    }
    /* Execute on peer Lua: capture exact return count. */
    n = _snprintf(out, cap,
        "do local f=_G[\"%s\"]; "
        "if type(f)~=\"function\" then GmRpcFail(\"missing %s\") else "
        "(function(...) GmRpcCapture(select(\"#\",...), ...) end)(f(%s)) "
        "end end",
        rpc->fn, rpc->fn, args);
    return n > 0 && n < cap;
}

void InstBusDrainPending(void)
{
    uint32_t i, me;
    char script[1024];
    if (!g_ready || !g_bus) return;
    me = PktIpcThisInstance();
    if (me == 0) me = 1;
    EnterCriticalSection(&g_cs);
    InstBusClaimSeatUnlocked();
    LeaveCriticalSection(&g_cs);

    for (i = 0; i < INST_BUS_MAX_RPC; ++i) {
        InstBusRpc* rpc = &g_bus->rpc[i];
        LONG prev;
        if (rpc->state != kInstRpcPending) continue;
        if (rpc->target_instance != me) continue;
        prev = InterlockedCompareExchange((volatile LONG*)&rpc->state,
            (LONG)kInstRpcRunning, (LONG)kInstRpcPending);
        if (prev != (LONG)kInstRpcPending) continue;

        InterlockedExchange(&g_exec_seq, (LONG)rpc->seq);
        InterlockedExchange(&g_exec_slot, (LONG)i);

        if (!BuildRpcScript(script, (int)sizeof(script), rpc)) {
            strncpy(rpc->err, "bad rpc script", sizeof(rpc->err) - 1);
            rpc->retc = 0;
            InterlockedExchange((volatile LONG*)&rpc->state, (LONG)kInstRpcError);
            InterlockedExchange(&g_exec_slot, -1);
            continue;
        }
        if (!ProxyRequestRunLua(script, (uint32_t)strlen(script))) {
            strncpy(rpc->err, "lua queue full", sizeof(rpc->err) - 1);
            InterlockedExchange((volatile LONG*)&rpc->state, (LONG)kInstRpcError);
            InterlockedExchange(&g_exec_slot, -1);
            continue;
        }
        ProxyConsumeLuaCast();
        return; /* one RPC per drain tick to keep UI responsive */
    }
}

void InstBusCaptureReturns(const InstBusArg* rets, uint32_t retc)
{
    LONG slot = InterlockedCompareExchange(&g_exec_slot, -1, -1);
    InstBusRpc* rpc;
    uint32_t i;
    if (!g_ready || !g_bus || slot < 0 || (uint32_t)slot >= INST_BUS_MAX_RPC) return;
    rpc = &g_bus->rpc[(uint32_t)slot];
    if (rpc->seq != (uint32_t)g_exec_seq) return;
    if (rpc->state != kInstRpcRunning) return;
    rpc->retc = retc > INST_BUS_MAX_RETS ? INST_BUS_MAX_RETS : retc;
    for (i = 0; i < rpc->retc; ++i)
        rpc->rets[i] = rets[i];
    rpc->err[0] = 0;
    InterlockedExchange((volatile LONG*)&rpc->state, (LONG)kInstRpcDone);
    InterlockedExchange(&g_exec_slot, -1);
}

void InstBusCaptureError(const char* msg)
{
    LONG slot = InterlockedCompareExchange(&g_exec_slot, -1, -1);
    InstBusRpc* rpc;
    if (!g_ready || !g_bus || slot < 0 || (uint32_t)slot >= INST_BUS_MAX_RPC) return;
    rpc = &g_bus->rpc[(uint32_t)slot];
    if (rpc->seq != (uint32_t)g_exec_seq) return;
    if (rpc->state != kInstRpcRunning) return;
    rpc->retc = 0;
    strncpy(rpc->err, msg ? msg : "rpc error", sizeof(rpc->err) - 1);
    rpc->err[sizeof(rpc->err) - 1] = 0;
    InterlockedExchange((volatile LONG*)&rpc->state, (LONG)kInstRpcError);
    InterlockedExchange(&g_exec_slot, -1);
}

uint32_t InstBusCurrentExecSeq(void)
{
    return (uint32_t)g_exec_seq;
}

int InstBusRemoteCall(uint32_t target_instance, const char* fn,
                      const InstBusArg* args, uint32_t argc,
                      InstBusArg* rets, uint32_t* retc, char* err, int err_cap,
                      uint32_t timeout_ms)
{
    uint32_t i, seq, slot, start, me;
    InstBusRpc* rpc = NULL;
    if (retc) *retc = 0;
    if (err && err_cap > 0) err[0] = 0;
    if (!g_ready || !g_bus || !fn || !fn[0] || target_instance == 0)
        return 0;
    me = PktIpcThisInstance();
    if (me == 0) me = 1;

    for (i = 0; fn[i]; ++i) {
        char c = fn[i];
        if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
              || (c >= '0' && c <= '9') || c == '_')) {
            if (err && err_cap > 0)
                strncpy(err, "bad fn name", (size_t)err_cap - 1);
            return 0;
        }
    }

    EnterCriticalSection(&g_cs);
    slot = 0xFFFFFFFFu;
    for (i = 0; i < INST_BUS_MAX_RPC; ++i) {
        uint32_t st = g_bus->rpc[i].state;
        if (st == kInstRpcEmpty || st == kInstRpcDone || st == kInstRpcError) {
            slot = i;
            break;
        }
    }
    if (slot == 0xFFFFFFFFu) {
        LeaveCriticalSection(&g_cs);
        if (err && err_cap > 0) strncpy(err, "rpc bus full", (size_t)err_cap - 1);
        return 0;
    }
    rpc = &g_bus->rpc[slot];
    memset(rpc, 0, sizeof(*rpc));
    seq = (uint32_t)InterlockedIncrement((volatile LONG*)&g_bus->rpc_seq_gen);
    if (seq == 0)
        seq = (uint32_t)InterlockedIncrement((volatile LONG*)&g_bus->rpc_seq_gen);
    rpc->seq = seq;
    rpc->target_instance = target_instance;
    rpc->source_instance = me;
    rpc->source_pid = PktIpcOwnerPid();
    strncpy(rpc->fn, fn, INST_BUS_FN_LEN - 1);
    rpc->argc = argc > INST_BUS_MAX_ARGS ? INST_BUS_MAX_ARGS : argc;
    for (i = 0; i < rpc->argc; ++i)
        rpc->args[i] = args[i];
    InterlockedExchange((volatile LONG*)&rpc->state, (LONG)kInstRpcPending);
    LeaveCriticalSection(&g_cs);

    /* Nudge our UI pump; peer instances poll InstBusDrainPending on their hooks. */
    ProxyWakeUiForInject();

    start = GetTickCount();
    if (timeout_ms < 50u) timeout_ms = 50u;
    if (timeout_ms > 5000u) timeout_ms = 5000u;
    for (;;) {
        uint32_t st = rpc->state;
        if (st == kInstRpcDone) {
            if (retc) *retc = rpc->retc;
            if (rets) {
                for (i = 0; i < rpc->retc && i < INST_BUS_MAX_RETS; ++i)
                    rets[i] = rpc->rets[i];
            }
            InterlockedExchange((volatile LONG*)&rpc->state, (LONG)kInstRpcEmpty);
            return 1;
        }
        if (st == kInstRpcError) {
            if (err && err_cap > 0) {
                strncpy(err, rpc->err[0] ? rpc->err : "rpc failed", (size_t)err_cap - 1);
                err[err_cap - 1] = 0;
            }
            InterlockedExchange((volatile LONG*)&rpc->state, (LONG)kInstRpcEmpty);
            return 0;
        }
        if ((GetTickCount() - start) >= timeout_ms) {
            if (err && err_cap > 0) strncpy(err, "rpc timeout", (size_t)err_cap - 1);
            /* Mark Error (not Empty) so a late peer Done/Error sees state != Running
             * and refuses to overwrite — avoids Empty-while-peer-still-Running race. */
            if (rpc->state == kInstRpcPending || rpc->state == kInstRpcRunning) {
                strncpy(rpc->err, "rpc timeout", sizeof(rpc->err) - 1);
                rpc->err[sizeof(rpc->err) - 1] = 0;
                InterlockedExchange((volatile LONG*)&rpc->state, (LONG)kInstRpcError);
            }
            return 0;
        }
        /* Self-target: drain+consume inline. Peer-target: peer hooks drain. */
        InstBusDrainPending();
        ProxyConsumeLuaCast();
        Sleep(1);
    }
}
