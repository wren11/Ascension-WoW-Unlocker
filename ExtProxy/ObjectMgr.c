

#include <windows.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

#include "ObjectMgr.h"

void ProxyLogLine(const char* msg);
uint32_t PktIpcOwnerPid(void);

enum {
    kFieldObjectType = 0x02u,

    kUnitBlockOff = 0xD0u,
    kUnitBlockMaxHealth = 0x68u,
    kUnitBlockLevel = 0xC0u,
    kUnitBlockFaction = 0xC4u,

    kUnitHealthDirect = 0xFB0u,



    kFieldEntry          = 0x06u,


    kFieldUnitDynFlags   = 0x4Fu,
    kFieldUnitDynFlagsAlt = 0x6Eu,


    kFieldUnitFlags      = 0x3Au,
    kFieldUnitFlagsAlt   = 0x48u,
    kFieldTargetGuid     = 0x60u,
};

static uint32_t EntryFromGuid(uint64_t guid)
{
    return (uint32_t)((guid >> 24) & 0xFFFFFFull);
}

enum {
    kMaxWalk = 4096u,
    /* Towns pack hundreds of GOs. 192 mixed slots dropped far players. */
    kMaxSnapshotUnits = 512u,
    /* Default cadence; force-pump / Invalidate resets tick for combat refresh. */
    kPumpIntervalMs = 200u,
    kCalibScanBytes = 0x1400u,
    kCalibCandidate = 0x798u,
    /* CGObject GetPosition copies these three floats (non-units). */
    kGoPosOff = 0xE8u,
    kGoFacingOff = 0xF8u,
};

static uint8_t* g_base;
static uint32_t g_tls_index;
static int g_tls_ok;

/* Last VirtualQuery region — thread-local so Present + Lua cannot tear the
 * cache (stale g_rq_end with a new g_rq_base made PAGE_EXECUTE look readable
 * and turned into a NULL deref). */
static _Thread_local uintptr_t g_rq_base;
static _Thread_local uintptr_t g_rq_end;
static _Thread_local DWORD g_rq_prot;
static uint32_t g_pos_off;

static uint32_t g_facing_off;
static CRITICAL_SECTION g_lock;
static int g_lock_ready;

static ObjMgrUnit g_cache[kMaxSnapshotUnits];
static uint32_t g_cache_n;
static uint32_t g_cache_tick;
static uint32_t g_cache_gen; /* bumps on every successful publish */
static float g_cache_px, g_cache_py, g_cache_pz;
static uint64_t g_cache_pguid;

/* Short-lived player object cache — Lua/GM APIs hit this constantly. */
static void* g_player_obj_cache;
static uint64_t g_player_obj_guid;
static DWORD g_player_obj_tick;

/* Last CollectUnits player XYZ (same walk as the unit list). */
static float g_collect_px, g_collect_py, g_collect_pz;
static int g_collect_have_self;

/* CGObject GetPosition (vtable slot 12, RVA 0x306500) copies this+0xE8.
 * GetFacing (slot 14, RVA 0x306550) is fld [ecx+0xF8]. Units override
 * slot 12 via [this+0xD8]+0x10 (typically 0x798). */

static int ProtectAllowsRead(DWORD prot)
{
    switch (prot & 0xFFu) {
    case PAGE_READONLY:
    case PAGE_READWRITE:
    case PAGE_WRITECOPY:
    case PAGE_EXECUTE_READ:
    case PAGE_EXECUTE_READWRITE:
    case PAGE_EXECUTE_WRITECOPY:
        return 1;
    default:
        /* PAGE_NOACCESS / PAGE_EXECUTE (no read) / PAGE_GUARD / 0 */
        return 0;
    }
}

static int Readable(const void* p, size_t n)
{
    MEMORY_BASIC_INFORMATION mbi;
    uintptr_t q, end;
    if (!p || !n)
        return 0;
    q = (uintptr_t)p;
    end = q + n;
    if (end < q)
        return 0;
    /* Fast path: same committed readable region as the previous probe. */
    if (g_rq_end && q >= g_rq_base && end <= g_rq_end
        && ProtectAllowsRead(g_rq_prot)
        && !(g_rq_prot & PAGE_GUARD))
        return 1;
    if (VirtualQuery(p, &mbi, sizeof(mbi)) != sizeof(mbi))
        return 0;
    if (mbi.State != MEM_COMMIT)
        return 0;
    if (mbi.Protect & PAGE_GUARD)
        return 0;
    if (!ProtectAllowsRead(mbi.Protect))
        return 0;
    g_rq_base = (uintptr_t)mbi.BaseAddress;
    g_rq_end = g_rq_base + mbi.RegionSize;
    g_rq_prot = mbi.Protect;
    return end <= g_rq_end;
}

static int Read32(const void* p, uint32_t* out)
{
    if (!Readable(p, 4))
        return 0;
    *out = *(const uint32_t*)p;
    return 1;
}

static int ReadPtr(const void* p, void** out)
{
    uint32_t v;
    if (!Read32(p, &v))
        return 0;
    *out = (void*)(uintptr_t)v;
    return 1;
}

static int ValidWorld(float f)
{
    return (f == f) && fabsf(f) < 100000.0f;
}

static void* TlsBlock(void)
{
    uint32_t teb_tls;
    void* block = NULL;

    __asm__ __volatile__("movl %%fs:0x2c, %0" : "=r"(teb_tls));
    if (!teb_tls)
        return NULL;
    if (!ReadPtr((const void*)(uintptr_t)(teb_tls + g_tls_index * 4u), &block))
        return NULL;
    return block;
}

void ObjMgrInit(uint8_t* ascension_base)
{
    if (!g_lock_ready) {
        InitializeCriticalSection(&g_lock);
        g_lock_ready = 1;
    }
    if (!ascension_base)
        return;
    g_base = ascension_base;
    if (Read32(g_base + kCurMgrTlsIndexRva, &g_tls_index))
        g_tls_ok = 1;
    /* Stock Ascension unit position until a move-packet calibration refines it.
     * Without this, ObjMgrPump early-outs and HuntingBot sees zero units. */
    if (!g_pos_off)
        g_pos_off = kCalibCandidate;
}

void* ObjMgrCurrent(void)
{
    void* block;
    void* mgr = NULL;
    if (!g_tls_ok)
        return NULL;
    block = TlsBlock();
    if (!block)
        return NULL;
    if (!ReadPtr((const uint8_t*)block + 8, &mgr))
        return NULL;
    return mgr;
}

int ObjMgrReady(void)
{
    return ObjMgrCurrent() != NULL;
}

uint64_t ObjMgrPlayerGuid(void)
{

    uint8_t* mgr = (uint8_t*)ObjMgrCurrent();
    uint32_t lo = 0, hi = 0;
    if (!mgr)
        return 0;
    if (!Read32(mgr + 0xC0, &lo) || !Read32(mgr + 0xC4, &hi))
        return 0;
    return ((uint64_t)hi << 32) | lo;
}

uint32_t ObjMgrField32(void* obj, uint32_t index)
{
    void* desc = NULL;
    uint32_t v = 0;
    if (!obj)
        return 0;
    if (!ReadPtr((const uint8_t*)obj + kObjDescriptorsOff, &desc) || !desc)
        return 0;
    if (!Read32((const uint8_t*)desc + index * 4u, &v))
        return 0;
    return v;
}

uint64_t ObjMgrField64(void* obj, uint32_t index)
{
    uint32_t lo = ObjMgrField32(obj, index);
    uint32_t hi = ObjMgrField32(obj, index + 1u);
    return ((uint64_t)hi << 32) | lo;
}

uint64_t ObjMgrObjectGuid(void* obj)
{
    uint32_t lo = 0, hi = 0;
    if (!obj)
        return 0;
    if (!Read32((const uint8_t*)obj + kObjGuidOff, &lo))
        return 0;
    if (!Read32((const uint8_t*)obj + kObjGuidOff + 4u, &hi))
        return 0;
    return ((uint64_t)hi << 32) | lo;
}

uint32_t ObjMgrUnitHealth(void* obj)
{
    uint32_t v = 0;
    if (!obj)
        return 0;
    if (!Read32((const uint8_t*)obj + kUnitHealthDirect, &v))
        return 0;
    return v;
}

uint32_t ObjMgrUnitBlock32(void* obj, uint32_t byte_off)
{
    void* blk = NULL;
    uint32_t v = 0;
    if (!obj)
        return 0;
    if (!ReadPtr((const uint8_t*)obj + kUnitBlockOff, &blk) || !blk)
        return 0;
    if (!Read32((const uint8_t*)blk + byte_off, &v))
        return 0;
    return v;
}

uint32_t ObjMgrTypeMask(void* obj)
{
    return ObjMgrField32(obj, kFieldObjectType);
}

uint32_t ObjMgrUnitFlags(void* obj)
{
    uint32_t v;
    if (!obj)
        return 0;
    v = ObjMgrField32(obj, kFieldUnitFlags);
    if (v)
        return v;
    return ObjMgrField32(obj, kFieldUnitFlagsAlt);
}

uint32_t ObjMgrDynFlags(void* obj)
{
    /* Primary WotLK UNIT_FIELD_DYNAMIC_FLAGS — probe one alt only if empty. */
    uint32_t v;
    if (!obj)
        return 0;
    v = ObjMgrField32(obj, kFieldUnitDynFlags);
    if (v)
        return v;
    return ObjMgrField32(obj, kFieldUnitDynFlagsAlt);
}

typedef int (*ObjVisitFn)(void* obj, void* ctx);

static uint32_t WalkObjects(ObjVisitFn fn, void* ctx)
{
    uint8_t* mgr = (uint8_t*)ObjMgrCurrent();
    uint32_t link_off = 0, cur = 0, seen = 0;
    if (!mgr)
        return 0;
    if (!Read32(mgr + kObjMgrLinkOffsetOff, &link_off))
        return 0;
    if (!Read32(mgr + kObjMgrFirstObjOff, &cur))
        return 0;
    if (link_off > 0x4000u)
        return 0;
    while (cur && !(cur & 1u) && seen < kMaxWalk) {
        uint32_t next = 0;
        seen++;
        if (!fn((void*)(uintptr_t)cur, ctx))
            break;
        if (!Read32((const void*)(uintptr_t)(cur + link_off + 4u), &next))
            break;
        cur = next;
    }
    return seen;
}

typedef struct FindCtx {
    uint64_t want;
    void* found;
} FindCtx;

static int FindVisit(void* obj, void* ctx)
{
    FindCtx* f = (FindCtx*)ctx;
    if (ObjMgrObjectGuid(obj) == f->want) {
        f->found = obj;
        return 0;
    }
    return 1;
}

void* ObjMgrFindByGuid(uint64_t guid)
{
    FindCtx f;
    if (!guid)
        return NULL;
    f.want = guid;
    f.found = NULL;
    WalkObjects(FindVisit, &f);
    return f.found;
}

int ObjMgrLiveByGuid(uint64_t guid, ObjMgrUnit* out)
{
    void* obj;
    void* self;
    float px = 0, py = 0, pz = 0, dx, dy, dz;
    uint32_t mask;
    int isUnit, isPlayer, isGo, isCorpse, isContainer;

    if (!guid || !out)
        return 0;
    obj = ObjMgrFindByGuid(guid);
    if (!obj)
        return 0;

    memset(out, 0, sizeof(*out));
    out->guid = guid;
    if (!ObjMgrPosition(obj, &out->x, &out->y, &out->z, NULL))
        return 0;

    self = ObjMgrPlayerObject();
    if (self && ObjMgrPosition(self, &px, &py, &pz, NULL)) {
        dx = out->x - px;
        dy = out->y - py;
        dz = out->z - pz;
        out->dist = sqrtf(dx * dx + dy * dy + dz * dz);
    } else {
        out->dist = 0.0f;
    }

    mask = ObjMgrTypeMask(obj);
    out->type_mask = mask;
    out->entry = EntryFromGuid(guid);

    isUnit      = (mask & kTypeMaskUnit)       != 0;
    isPlayer    = (mask & kTypeMaskPlayer)     != 0;
    isGo        = (mask & kTypeMaskGameObject) != 0;
    isContainer = (mask & kTypeMaskContainer)  != 0;
    isCorpse    = (mask & kTypeMaskCorpse)     != 0;

    if (isUnit || isPlayer || isCorpse) {
        out->health = ObjMgrUnitHealth(obj);
        out->max_health = ObjMgrUnitBlock32(obj, kUnitBlockMaxHealth);
        out->level = ObjMgrUnitBlock32(obj, kUnitBlockLevel);
        out->faction = ObjMgrUnitBlock32(obj, kUnitBlockFaction);
        out->dyn_flags = ObjMgrDynFlags(obj);
        out->unit_flags = ObjMgrUnitFlags(obj);
        out->target_guid = ObjMgrField64(obj, kFieldTargetGuid);
    } else if (isGo || isContainer) {
        out->dyn_flags = ObjMgrGoFlags(obj);
        out->unit_flags = out->dyn_flags;
        out->level = ObjMgrGoType(obj);
        out->faction = ObjMgrGoDynamic(obj);
    }
    return 1;
}

void* ObjMgrPlayerObject(void)
{
    uint64_t guid = ObjMgrPlayerGuid();
    DWORD now = GetTickCount();
    if (!guid)
        return NULL;
    if (g_player_obj_cache && g_player_obj_guid == guid
        && (now - g_player_obj_tick) < 100u) {
        /* Stale pointer guard — object may have been freed after logout/teleport. */
        if (Readable(g_player_obj_cache, kObjGuidOff + 8u)
            && ObjMgrObjectGuid(g_player_obj_cache) == guid)
            return g_player_obj_cache;
        g_player_obj_cache = NULL;
    }
    g_player_obj_cache = ObjMgrFindByGuid(guid);
    g_player_obj_guid = guid;
    g_player_obj_tick = now;
    return g_player_obj_cache;
}

uint32_t ObjMgrPositionOffset(void)
{
    return g_pos_off;
}

static uint32_t FacingOffAbs(void)
{
    if (g_facing_off)
        return g_facing_off;
    return g_pos_off ? (g_pos_off + 12u) : 0u;
}

uint32_t ObjMgrFacingOffset(void)
{
    return FacingOffAbs();
}

int ObjMgrFacingOffsetResolved(void)
{
    return g_facing_off != 0u;
}

static int PlausibleFacing(float f)
{
    return (f == f) && f > -7.0f && f < 10.0f;
}

static float NormFacing(float f)
{
    const float twopi = 6.2831853f;
    while (f < 0.f) f += twopi;
    while (f >= twopi) f -= twopi;
    return f;
}

static float AngDiff(float a, float b)
{
    float d = NormFacing(a) - NormFacing(b);
    if (d < 0.f) d = -d;
    if (d > 3.14159265f) d = 6.2831853f - d;
    return d;
}

static const int kFacingRels[] = {
    0x10, 0x14, 0x18, 0x1C, 0x20, 0x24, 0x28, 0x08, 0x0C, -0x04, -0x08
};
enum { kFacingRelCount = sizeof(kFacingRels) / sizeof(kFacingRels[0]) };

static float s_prev_xy[2];
static int s_prev_xy_ok;
static float s_prev_cand[kFacingRelCount];
static uint8_t s_prev_cand_ok[kFacingRelCount];

int ObjMgrReadClientFacing(void* obj, float* out_facing)
{
    const uint8_t* base;
    float x = 0.f, y = 0.f, z = 0.f;
    float moved = 0.f;
    float facing = 0.f;
    int i;
    int best_i = -1;
    float best_delta = 0.05f;
    int is_player;
    char msg[128];

    /* Probe into a local, then write back once. Never reload *out_facing —
     * clang spilled that pointer to [esp+0x60] and a later VirtualQuery MBI
     * can smash the slot → AV on `movss xmm0,[eax]` (WER 0xC0000005 @ 0x2882A). */
    if (!obj || !g_pos_off)
        return 0;

    {
        const float* p = (const float*)((const uint8_t*)obj + g_pos_off);
        if (!Readable(p, sizeof(float) * 3))
            return 0;
        if (!ValidWorld(p[0]) || !ValidWorld(p[1]) || !ValidWorld(p[2]))
            return 0;
        x = p[0]; y = p[1]; z = p[2];
    }
    (void)z;
    base = (const uint8_t*)obj;
    is_player = (obj == ObjMgrPlayerObject());


    if (g_facing_off) {
        const float* pf = (const float*)(base + g_facing_off);
        float f;
        if (Readable(pf, sizeof(float))) {
            f = *pf;
            if (PlausibleFacing(f)) {
                facing = NormFacing(f);
                if (is_player)
                    goto sample_prev;
                if (out_facing)
                    *out_facing = facing;
                return 1;
            }
        }

        if (is_player)
            g_facing_off = 0;
        else {
            if (out_facing)
                *out_facing = 0.f;
            return 0;
        }
    }


    if (!is_player) {
        for (i = 0; i < kFacingRelCount; i++) {
            int rel = kFacingRels[i];
            int abs_off = (int)g_pos_off + rel;
            const float* pf;
            if (rel == 0x0C || abs_off < 0)
                continue;
            pf = (const float*)(base + abs_off);
            if (!Readable(pf, sizeof(float)))
                continue;
            if (!PlausibleFacing(*pf))
                continue;
            facing = NormFacing(*pf);
            if (out_facing)
                *out_facing = facing;
            return 1;
        }
        if (out_facing)
            *out_facing = 0.f;
        return 0;
    }

    if (s_prev_xy_ok) {
        float dx = x - s_prev_xy[0], dy = y - s_prev_xy[1];
        moved = sqrtf(dx * dx + dy * dy);
    }


    for (i = 0; i < kFacingRelCount; i++) {
        int rel = kFacingRels[i];
        int abs_off = (int)g_pos_off + rel;
        const float* pf;
        float f, delta;
        if (abs_off < 0)
            continue;
        pf = (const float*)(base + abs_off);
        if (!Readable(pf, sizeof(float)))
            continue;
        f = *pf;
        if (!PlausibleFacing(f))
            continue;

        if (rel == 0x0C && fabsf(f) < 1e-4f)
            continue;
        if (s_prev_cand_ok[i]) {
            delta = AngDiff(f, s_prev_cand[i]);

            if (moved < 0.08f && delta > best_delta) {
                best_delta = delta;
                best_i = i;
            }

            if (moved >= 0.08f && delta > best_delta) {
                best_delta = delta;
                best_i = i;
            }
        }
    }

    if (best_i >= 0) {
        int abs_off = (int)g_pos_off + kFacingRels[best_i];
        const float* pf = (const float*)(base + abs_off);
        g_facing_off = (uint32_t)abs_off;
        facing = NormFacing(*pf);
        /* One-shot calibration — do not log (login noise / disk churn). */
        goto sample_prev;
    }



    for (i = 0; i < kFacingRelCount; i++) {
        int rel = kFacingRels[i];
        int abs_off = (int)g_pos_off + rel;
        const float* pf;
        float f;
        if (abs_off < 0)
            continue;
        pf = (const float*)(base + abs_off);
        if (!Readable(pf, sizeof(float)))
            continue;
        f = *pf;
        if (!PlausibleFacing(f))
            continue;
        if (rel == 0x0C)
            continue;
        if (rel == 0x10) {
            g_facing_off = (uint32_t)abs_off;
            facing = NormFacing(f);
            /* Soft-bind is expected when motion facing scan misses — not an error; quiet. */
            (void)msg;
            goto sample_prev;
        }
    }

    for (i = 0; i < kFacingRelCount; i++) {
        int rel = kFacingRels[i];
        int abs_off = (int)g_pos_off + rel;
        const float* pf;
        float f;
        if (rel == 0x0C || rel == 0x10 || abs_off < 0)
            continue;
        pf = (const float*)(base + abs_off);
        if (!Readable(pf, sizeof(float)))
            continue;
        f = *pf;
        if (!PlausibleFacing(f))
            continue;
        facing = NormFacing(f);
        goto sample_prev;
    }

    facing = 0.f;


sample_prev:
    s_prev_xy[0] = x;
    s_prev_xy[1] = y;
    s_prev_xy_ok = 1;
    for (i = 0; i < kFacingRelCount; i++) {
        int abs_off = (int)g_pos_off + kFacingRels[i];
        const float* pf;
        s_prev_cand_ok[i] = 0;
        if (abs_off < 0)
            continue;
        pf = (const float*)(base + abs_off);
        if (!Readable(pf, sizeof(float)))
            continue;
        if (!PlausibleFacing(*pf))
            continue;
        s_prev_cand[i] = *pf;
        s_prev_cand_ok[i] = 1;
    }
    if (out_facing)
        *out_facing = facing;
    return PlausibleFacing(facing) ? 1 : 0;
}

int ObjMgrPeekFacing(void* obj, float* out_facing)
{
    const float* pf;
    float f;
    if (!obj || !out_facing || !g_facing_off)
        return 0;
    pf = (const float*)((const uint8_t*)obj + g_facing_off);
    if (!Readable(pf, sizeof(float)))
        return 0;
    f = *pf;
    if (!PlausibleFacing(f))
        return 0;
    *out_facing = NormFacing(f);
    return 1;
}

static int ObjMgrPositionGo(void* obj, float* x, float* y, float* z)
{
    const float* p;
    if (!obj)
        return 0;
    p = (const float*)((const uint8_t*)obj + kGoPosOff);
    if (!Readable(p, sizeof(float) * 3))
        return 0;
    if (!ValidWorld(p[0]) || !ValidWorld(p[1]) || !ValidWorld(p[2]))
        return 0;
    if (x) *x = p[0];
    if (y) *y = p[1];
    if (z) *z = p[2];
    return 1;
}

static void WriteFacingOut(void* obj, float* facing, int is_go)
{
    float f = 0.0f;
    if (!facing)
        return;
    if (is_go) {
        const float* pf = (const float*)((const uint8_t*)obj + kGoFacingOff);
        if (Readable(pf, sizeof(float)) && PlausibleFacing(*pf))
            f = NormFacing(*pf);
    } else if (!ObjMgrReadClientFacing(obj, &f)) {
        f = 0.0f;
    }
    *facing = f;
}

int ObjMgrPosition(void* obj, float* x, float* y, float* z, float* facing)
{
    uint32_t mask;
    const float* p;

    if (!obj)
        return 0;

    mask = ObjMgrTypeMask(obj);
    if (mask & kTypeMaskGameObject) {
        if (!ObjMgrPositionGo(obj, x, y, z))
            return 0;
        WriteFacingOut(obj, facing, 1);
        return 1;
    }

    if (!g_pos_off)
        return 0;
    p = (const float*)((const uint8_t*)obj + g_pos_off);
    if (!Readable(p, sizeof(float) * 3))
        return 0;
    if (!ValidWorld(p[0]) || !ValidWorld(p[1]) || !ValidWorld(p[2]))
        return 0;
    if (x) *x = p[0];
    if (y) *y = p[1];
    if (z) *z = p[2];
    WriteFacingOut(obj, facing, 0);
    return 1;
}

float ObjMgrReadFloatAt(void* obj, int byte_off)
{
    const float* p;
    if (!obj || !g_pos_off)
        return 0.0f;
    p = (const float*)((const uint8_t*)obj + (int)g_pos_off + byte_off);
    if (!Readable(p, sizeof(float)))
        return 0.0f;
    return *p;
}

int ObjMgrSetFacing(void* obj, float facing)
{
    float* p;
    uint32_t foff;
    SIZE_T wrote = 0;
    int ok = 0;
    if (!obj || !g_pos_off)
        return 0;
    foff = FacingOffAbs();
    if (!foff)
        return 0;
    p = (float*)((uint8_t*)obj + foff);
    if (!Readable(p, sizeof(float)))
        return 0;

    if (WriteProcessMemory(GetCurrentProcess(), p, &facing, sizeof(facing), &wrote)
        && wrote == sizeof(facing))
        ok = 1;

    return ok;
}

uint32_t ObjMgrCalibrateFacing(float known_facing)
{
    uint8_t* obj;
    int rel;
    int lo, hi;
    char msg[128];

    if (g_facing_off)
        return g_facing_off;
    if (!g_pos_off)
        return 0;

    if (!(known_facing == known_facing) || known_facing < -7.0f || known_facing > 7.0f)
        return 0;
    obj = (uint8_t*)ObjMgrPlayerObject();
    if (!obj)
        return 0;



    lo = -0x10;
    hi = 0x48;
    {
        int best_off = 0;
        float best_err = 0.03f;
        for (rel = lo; rel + 4 <= hi; rel += 4) {
            const float* pf = (const float*)(obj + (int)g_pos_off + rel);
            float f, err;
            if ((int)g_pos_off + rel < 0)
                continue;
            if (!Readable(pf, sizeof(float)))
                continue;
            f = *pf;
            if (!(f == f) || f < 0.0f || f > 6.2831854f)
                continue;
            err = f - known_facing;
            if (err < 0.0f) err = -err;

            if (err > 3.14159265f) err = 6.2831853f - err;
            if (err < best_err) {
                best_err = err;
                best_off = (int)g_pos_off + rel;
            }
        }
        if (best_off) {
            g_facing_off = (uint32_t)best_off;
            snprintf(msg, sizeof(msg),
                "objmgr: facing offset = 0x%X (pos+0x%X, err<0.03) known=%.3f",
                g_facing_off, g_facing_off - g_pos_off, known_facing);
            ProxyLogLine(msg);
        }
    }
    return g_facing_off;
}

int ObjMgrSetPosition(void* obj, float x, float y, float z, float facing)
{
    float quad[4];
    SIZE_T wrote = 0;
    void* p;
    uint32_t foff;
    if (!obj || !g_pos_off)
        return 0;
    p = (uint8_t*)obj + g_pos_off;
    if (!Readable(p, sizeof(quad)))
        return 0;
    quad[0] = x;
    quad[1] = y;
    quad[2] = z;
    quad[3] = facing;
    if (!WriteProcessMemory(GetCurrentProcess(), p, quad, sizeof(quad), &wrote)
        || wrote != sizeof(quad))
        return 0;

    foff = FacingOffAbs();
    if (foff && foff != g_pos_off + 12u) {
        float* pf = (float*)((uint8_t*)obj + foff);
        if (Readable(pf, sizeof(float)))
            WriteProcessMemory(GetCurrentProcess(), pf, &facing, sizeof(facing), &wrote);
    }
    return 1;
}

static int MatchesAt(const uint8_t* obj, uint32_t off, float kx, float ky, float kz)
{
    const float* p = (const float*)(obj + off);
    float o;
    if (!Readable(p, sizeof(float) * 4))
        return 0;
    if (!(fabsf(p[0] - kx) < 0.5f && fabsf(p[1] - ky) < 0.5f
          && fabsf(p[2] - kz) < 1.0f))
        return 0;
    o = p[3];
    return (o == o) && o > -7.0f && o < 7.0f;
}

uint32_t ObjMgrCalibrate(float known_x, float known_y, float known_z)
{
    uint8_t* obj;
    uint32_t off;
    char msg[128];

    if (g_pos_off)
        return g_pos_off;
    if (!ValidWorld(known_x) || !ValidWorld(known_y) || !ValidWorld(known_z))
        return 0;
    obj = (uint8_t*)ObjMgrPlayerObject();
    if (!obj)
        return 0;


    if (MatchesAt(obj, kCalibCandidate, known_x, known_y, known_z)) {
        g_pos_off = kCalibCandidate;
    } else {
        for (off = 0; off + 12u <= kCalibScanBytes; off += 4u) {
            if (MatchesAt(obj, off, known_x, known_y, known_z)) {
                g_pos_off = off;
                break;
            }
        }
    }

    if (g_pos_off) {
        snprintf(msg, sizeof(msg), "objmgr: position offset = 0x%X%s",
            g_pos_off, g_pos_off == kCalibCandidate ? " (stock)" : " (scanned)");
        ProxyLogLine(msg);
    }
    return g_pos_off;
}

typedef struct CollectCtx {
    ObjMgrUnit* out;
    uint32_t max;
    uint32_t n;
    float radius;
    float px, py, pz;
    uint64_t self;
    int have_self;
} CollectCtx;

static int CollectWorse(const ObjMgrUnit* a, const ObjMgrUnit* b);

static int CollectVisit(void* obj, void* ctx)
{
    CollectCtx* c = (CollectCtx*)ctx;
    ObjMgrUnit u;
    uint32_t mask = ObjMgrTypeMask(obj);
    float dx, dy, dz;
    int isUnit, isPlayer, isGo, isCorpse, isContainer;

    isUnit      = (mask & kTypeMaskUnit)        != 0;
    isPlayer    = (mask & kTypeMaskPlayer)      != 0;
    isGo        = (mask & kTypeMaskGameObject)  != 0;
    isContainer = (mask & kTypeMaskContainer)   != 0;
    isCorpse    = (mask & kTypeMaskCorpse)      != 0;
    if (!(isUnit || isPlayer || isGo || isContainer || isCorpse))
        return 1;

    memset(&u, 0, sizeof(u));
    u.guid = ObjMgrObjectGuid(obj);
    if (!u.guid || (c->self && u.guid == c->self))
        return 1;

    /* Snapshot needs XYZ for distance; facing probe is expensive — skip on collect. */
    if (!ObjMgrPosition(obj, &u.x, &u.y, &u.z, NULL))
        return 1;

    dx = u.x - c->px;
    dy = u.y - c->py;
    dz = u.z - c->pz;
    u.dist = sqrtf(dx * dx + dy * dy + dz * dz);
    if (c->radius > 0.0f && u.dist > c->radius)
        return 1;

    u.type_mask = mask;
    u.entry = EntryFromGuid(u.guid);

    if (isUnit || isPlayer || isCorpse) {
        /* Distant units: GUID/pos/type only — skip descriptor probes in cities. */
        if (u.dist <= 80.0f) {
            u.health = ObjMgrUnitHealth(obj);
            u.max_health = ObjMgrUnitBlock32(obj, kUnitBlockMaxHealth);
            u.level = ObjMgrUnitBlock32(obj, kUnitBlockLevel);
            u.faction = ObjMgrUnitBlock32(obj, kUnitBlockFaction);
            u.dyn_flags = ObjMgrDynFlags(obj);
            u.unit_flags = ObjMgrUnitFlags(obj);
            u.target_guid = ObjMgrField64(obj, kFieldTargetGuid);
        }
    } else if (isGo) {
        u.dyn_flags = ObjMgrGoFlags(obj);
        u.unit_flags = u.dyn_flags;
        /* Reuse level slot: GO type (CHEST=3, GOOBER=10, …) for ESP/Hunt. */
        u.level = ObjMgrGoType(obj);
        /* Reuse faction slot: GAMEOBJECT_DYNAMIC (sparkle/activate). */
        u.faction = ObjMgrGoDynamic(obj);
    }

    if (c->n < c->max)
        c->out[c->n++] = u;
    else {
        uint32_t i, worst = 0;
        for (i = 1; i < c->n; i++)
            if (CollectWorse(&c->out[i], &c->out[worst]))
                worst = i;
        /* Prefer players over NPCs/GOs; among equals keep the closer object. */
        if (CollectWorse(&c->out[worst], &u))
            c->out[worst] = u;
    }
    return 1;
}

static int CollectRank(uint32_t mask)
{
    if (mask & kTypeMaskPlayer)
        return 0;
    if (mask & kTypeMaskUnit)
        return 1;
    if (mask & kTypeMaskCorpse)
        return 2;
    return 3;
}

static int CollectWorse(const ObjMgrUnit* a, const ObjMgrUnit* b)
{
    int ra, rb;
    int a_near_go, b_near_go;
    if (!a || !b)
        return 0;
    /* Visible chests are type GO. Never evict a nearby GO for a far player. */
    a_near_go = ((a->type_mask & kTypeMaskGameObject) != 0) && a->dist <= 80.0f;
    b_near_go = ((b->type_mask & kTypeMaskGameObject) != 0) && b->dist <= 80.0f;
    if (a_near_go && !b_near_go)
        return 0;
    if (b_near_go && !a_near_go)
        return 1;
    ra = CollectRank(a->type_mask);
    rb = CollectRank(b->type_mask);
    if (ra != rb)
        return ra > rb;
    return a->dist > b->dist;
}

static void SortByDist(ObjMgrUnit* a, uint32_t n)
{
    uint32_t i, j;
    for (i = 1; i < n; i++) {
        ObjMgrUnit key = a[i];
        for (j = i; j > 0 && a[j - 1].dist > key.dist; j--)
            a[j] = a[j - 1];
        a[j] = key;
    }
}

uint32_t ObjMgrCollectUnits(ObjMgrUnit* out, uint32_t max, float radius)
{
    CollectCtx c;
    void* self_obj;

    if (!out || !max)
        return 0;
    memset(&c, 0, sizeof(c));
    c.out = out;
    c.max = max;
    c.radius = radius;
    c.self = ObjMgrPlayerGuid();
    if (!c.self)
        return 0;

    /*
     * Resolve local player first (cached FindByGuid), then one list walk.
     * Avoids the late-player bug where max slots fill before self is visited.
     */
    self_obj = ObjMgrPlayerObject();
    if (!self_obj || !ObjMgrPosition(self_obj, &c.px, &c.py, &c.pz, NULL))
        return 0;
    c.have_self = 1;

    WalkObjects(CollectVisit, &c);
    SortByDist(out, c.n);
    g_collect_px = c.px;
    g_collect_py = c.py;
    g_collect_pz = c.pz;
    g_collect_have_self = 1;
    return c.n;
}

void ObjMgrInvalidate(void)
{
    /* Soft invalidate: force next Pump, keep last snapshot so Lua never sees
     * an empty hole mid-frame (rehydrate from previous until fresh walk). */
    if (!g_lock_ready) {
        g_cache_tick = 0;
        return;
    }
    EnterCriticalSection(&g_lock);
    g_cache_tick = 0;
    g_player_obj_cache = NULL;
    g_player_obj_guid = 0;
    g_player_obj_tick = 0;
    LeaveCriticalSection(&g_lock);
}

uint32_t ObjMgrCacheAgeMs(void)
{
    uint32_t tick, now = GetTickCount();
    if (!g_lock_ready)
        return 0xFFFFFFFFu;
    EnterCriticalSection(&g_lock);
    tick = g_cache_tick;
    LeaveCriticalSection(&g_lock);
    if (!tick)
        return 0xFFFFFFFFu;
    return now - tick;
}

uint32_t ObjMgrCacheGen(void)
{
    uint32_t g;
    if (!g_lock_ready)
        return 0;
    EnterCriticalSection(&g_lock);
    g = g_cache_gen;
    LeaveCriticalSection(&g_lock);
    return g;
}

void ObjMgrPump(void)
{
    ObjMgrUnit local[kMaxSnapshotUnits];
    uint32_t n, now = GetTickCount();
    float px = 0, py = 0, pz = 0;
    uint64_t pguid;

    if (!g_lock_ready || !ObjMgrReady())
        return;

    EnterCriticalSection(&g_lock);
    if (g_cache_tick && (now - g_cache_tick) < kPumpIntervalMs) {
        LeaveCriticalSection(&g_lock);
        return;
    }
    g_cache_tick = now;
    LeaveCriticalSection(&g_lock);

    if (!g_pos_off)
        g_pos_off = kCalibCandidate;

    pguid = ObjMgrPlayerGuid();
    g_collect_have_self = 0;
    n = ObjMgrCollectUnits(local, kMaxSnapshotUnits, 0.0f);
    if (g_collect_have_self) {
        px = g_collect_px;
        py = g_collect_py;
        pz = g_collect_pz;
    } else if (!n) {
        /* Loading / no self — keep prior snapshot; allow sooner retry. */
        EnterCriticalSection(&g_lock);
        g_cache_tick = now - (kPumpIntervalMs / 2u);
        LeaveCriticalSection(&g_lock);
        return;
    }

    EnterCriticalSection(&g_lock);
    memcpy(g_cache, local, sizeof(ObjMgrUnit) * n);
    g_cache_n = n;
    g_cache_pguid = pguid;
    g_cache_gen++;
    if (g_cache_gen == 0u)
        g_cache_gen = 1u; /* skip 0 = never pumped */
    if (g_collect_have_self) {
        g_cache_px = px;
        g_cache_py = py;
        g_cache_pz = pz;
    }
    LeaveCriticalSection(&g_lock);
}

uint32_t ObjMgrCacheCount(void)
{
    uint32_t n;
    if (!g_lock_ready)
        return 0;
    EnterCriticalSection(&g_lock);
    n = g_cache_n;
    LeaveCriticalSection(&g_lock);
    return n;
}

int ObjMgrCacheGet(uint32_t index, ObjMgrUnit* out)
{
    int ok = 0;
    if (!out || !g_lock_ready)
        return 0;
    EnterCriticalSection(&g_lock);
    if (index < g_cache_n) {
        *out = g_cache[index];
        ok = 1;
    }
    LeaveCriticalSection(&g_lock);
    return ok;
}

int ObjMgrCacheFind(uint64_t guid, ObjMgrUnit* out)
{
    uint32_t i;
    int ok = 0;
    if (!out || !g_lock_ready || !guid)
        return 0;
    EnterCriticalSection(&g_lock);
    for (i = 0; i < g_cache_n; i++) {
        if (g_cache[i].guid == guid) {
            *out = g_cache[i];
            ok = 1;
            break;
        }
    }
    LeaveCriticalSection(&g_lock);
    return ok;
}

uint32_t ObjMgrSnapshot(void* out, uint32_t out_cap)
{
    ObjMgrSnapshotHeader hdr;
    uint8_t* p = (uint8_t*)out;
    uint32_t n, room, bytes;

    if (!out || out_cap < sizeof(hdr))
        return 0;

    memset(&hdr, 0, sizeof(hdr));
    hdr.magic = OBJ_SNAPSHOT_MAGIC;
    hdr.pos_off = g_pos_off;

    if (!g_lock_ready) {
        memcpy(p, &hdr, sizeof(hdr));
        return (uint32_t)sizeof(hdr);
    }

    room = (out_cap - (uint32_t)sizeof(hdr)) / (uint32_t)sizeof(ObjMgrUnit);

    EnterCriticalSection(&g_lock);
    n = g_cache_n;
    if (n > room)
        n = room;
    hdr.player_guid = g_cache_pguid;
    hdr.player_x = g_cache_px;
    hdr.player_y = g_cache_py;
    hdr.player_z = g_cache_pz;
    hdr.count = n;
    hdr.owner_pid = PktIpcOwnerPid();
    bytes = n * (uint32_t)sizeof(ObjMgrUnit);
    memcpy(p, &hdr, sizeof(hdr));
    if (bytes)
        memcpy(p + sizeof(hdr), g_cache, bytes);
    LeaveCriticalSection(&g_lock);

    return (uint32_t)sizeof(hdr) + bytes;
}

/* ===================== Extended accessors (GmApiExt) ===================== */

float ObjMgrObjectFloatField(void* obj, uint32_t index)
{
    void* desc = NULL;
    const float* p;
    if (!obj)
        return 0.0f;
    if (!ReadPtr((const uint8_t*)obj + kObjDescriptorsOff, &desc) || !desc)
        return 0.0f;
    p = (const float*)((const uint8_t*)desc + index * 4u);
    if (!Readable(p, sizeof(float)))
        return 0.0f;
    return *p;
}

uint64_t ObjMgrObjectGuidField(void* obj, uint32_t index)
{
    /* GUIDs are 64-bit update fields stored as two consecutive 32-bit descriptors. */
    return ObjMgrField64(obj, index);
}

uint32_t ObjMgrPlayerFlags(void* obj)
{
    uint32_t mask;
    if (!obj)
        return 0;
    mask = ObjMgrTypeMask(obj);
    if ((mask & kTypeMaskPlayer) == 0)
        return 0;
    return ObjMgrField32(obj, kFieldPlayerFlags);
}

int ObjMgrPlayerFlagsLookSane(uint32_t flags)
{
    if (flags == 0 || flags == 0xFFFFFFFFu)
        return 0;
    if ((flags & ~kPlayerFlagsKnownMask) != 0u)
        return 0;
    return 1;
}

int ObjMgrPlayerIsGmStaff(uint32_t flags)
{
    if (!ObjMgrPlayerFlagsLookSane(flags))
        return 0;
    return (flags & (kPlayerFlagGm | kPlayerFlagDeveloper)) != 0u;
}

uint32_t ObjMgrNpcFlags(void* obj)
{
    return ObjMgrField32(obj, kFieldUnitNpcFlags);
}

uint32_t ObjMgrUnitFlags2(void* obj)
{
    return ObjMgrField32(obj, kFieldUnitFlags2);
}

float ObjMgrBoundingRadius(void* obj)
{
    return ObjMgrObjectFloatField(obj, kFieldUnitBoundingRadius);
}

float ObjMgrCombatReach(void* obj)
{
    return ObjMgrObjectFloatField(obj, kFieldUnitCombatReach);
}

uint64_t ObjMgrCreatedBy(void* obj)
{
    return ObjMgrObjectGuidField(obj, kFieldUnitCreatedBy);
}

uint64_t ObjMgrSummonedBy(void* obj)
{
    return ObjMgrObjectGuidField(obj, kFieldUnitSummonedBy);
}

uint64_t ObjMgrCharmedBy(void* obj)
{
    return ObjMgrObjectGuidField(obj, kFieldUnitCharmedBy);
}

uint32_t ObjMgrUnitBytes0(void* obj)
{
    return ObjMgrField32(obj, kFieldUnitBytes0);
}

uint32_t ObjMgrUnitBytes1(void* obj)
{
    return ObjMgrField32(obj, kFieldUnitBytes1);
}

uint32_t ObjMgrUnitBytes2(void* obj)
{
    return ObjMgrField32(obj, kFieldUnitBytes2);
}

uint32_t ObjMgrCreatureFamily(void* obj)
{
    /* UNIT_FIELD_BYTES_0 packs: byte0=race, byte1=class, byte2=gender, byte3=powerType.
     * For NPC units there is no separate creature-family descriptor exposed in
     * the public 3.3.5a table; we expose class (byte1) as the closest stable
     * "type" classification usable for filtering. */
    return (ObjMgrUnitBytes0(obj) >> 8) & 0xFFu;
}

uint32_t ObjMgrDisplayId(void* obj)
{
    return ObjMgrField32(obj, kFieldUnitDisplayId);
}

uint32_t ObjMgrGoFlags(void* obj)
{
    return obj ? ObjMgrField32(obj, kFieldGoFlags) : 0;
}

uint32_t ObjMgrGoDynamic(void* obj)
{
    return obj ? ObjMgrField32(obj, kFieldGoDynamic) : 0;
}

int ObjMgrGoTypeIsInteractLoot(uint32_t go_type)
{
    switch (go_type) {
    case 2:  /* questgiver */
    case 3:  /* chest (LC treasures, veins, herbs) */
    case 5:  /* generic */
    case 6:  /* trap / gather */
    case 8:  /* spell focus */
    case 10: /* goober */
    case 19: /* mailbox */
    case 25: /* fishing hole */
        return 1;
    default:
        return 0;
    }
}

uint32_t ObjMgrGoPosOffset(void)
{
    return kGoPosOff;
}

uint32_t ObjMgrGoType(void* obj)
{
    return obj ? ((ObjMgrField32(obj, kFieldGoBytes1) >> 8) & 0xFFu) : 0;
}

uint32_t ObjMgrGoState(void* obj)
{
    return obj ? (ObjMgrField32(obj, kFieldGoBytes1) & 0xFFu) : 0;
}

uint32_t ObjMgrGoAnimProgress(void* obj)
{
    return obj ? ((ObjMgrField32(obj, kFieldGoBytes1) >> 24) & 0xFFu) : 0;
}

uint32_t ObjMgrStandState(void* obj)
{
    return ObjMgrUnitBytes1(obj) & 0xFFu;
}

int ObjMgrIsTappedByMe(void* obj)
{
    return (ObjMgrDynFlags(obj) & kUnitDynTappedByPlayer) != 0u;
}

int ObjMgrIsTapped(void* obj)
{
    return (ObjMgrDynFlags(obj) & (kUnitDynTapped | kUnitDynTappedByPlayer
                                   | kUnitDynTappedByAllThreat)) != 0u;
}

uint32_t ObjMgrMovementFlags(void* obj)
{
    /* Primary: the live UnitMovement struct sits a fixed offset above the
     * position block. The movement-flags dword is the first field of that
     * struct. kUnitMovementFlagsOff is relative to the object base. */
    const uint32_t* p;
    uint32_t v = 0;
    if (!obj)
        return 0;
    p = (const uint32_t*)((const uint8_t*)obj + kUnitMovementFlagsOff);
    if (Readable(p, sizeof(uint32_t)))
        v = *p;
    /* Augment with descriptor-side UNIT_FIELD_FLAGS where the struct read fails
     * (e.g. on players whose movement block layout differs slightly). */
    if (v == 0u)
        v = ObjMgrUnitFlags(obj);
    return v;
}

float ObjMgrPitch(void* obj)
{
    /* Pitch lives in the UnitMovement struct immediately after the orientation
     * dword (offset position+0x14 -> x,y,z,o then pitch at +0x10 from o). We
     * compute it relative to the calibrated position offset so it follows the
     * same auto-calibration as facing. */
    const float* p;
    if (!obj || !g_pos_off)
        return 0.0f;
    /* pos: +0 x, +4 y, +8 z, +0xC o, +0x10 pitch. */
    p = (const float*)((const uint8_t*)obj + g_pos_off + 0x10);
    if (!Readable(p, sizeof(float)))
        return 0.0f;
    {
        float f = *p;
        if (f != f || f < -3.14159f || f > 3.14159f)
            return 0.0f;
        return f;
    }
}

/* ---- Object name resolution ----
 * WotLK 3.3.5a stores unit names on a per-object cache via a `char*` pointer.
 * The offset is stable for units/players at +0x01A8 (the CGUnit::m_name slot).
 * GameObjects carry no client name; their descriptor has no name field, so we
 * fall back to reading the entry-derived label only if present. We probe a few
 * candidate offsets and validate ASCII-printability. */
static int ValidNameChar(char c)
{
    return (c >= 0x20 && c < 0x7F) || c == 0;
}

/* Player/NPC display names: letters/digits/space/'/-/_ only. Rejects tooltip
 * format junk like "$N:;%N;" that printable-ASCII probes otherwise accept. */
static int IsPlausibleUnitName(const char* p, unsigned len)
{
    unsigned i, letters = 0;
    if (!p || len < 2 || len > 48)
        return 0;
    if (!((p[0] >= 'A' && p[0] <= 'Z') || (p[0] >= 'a' && p[0] <= 'z')))
        return 0;
    for (i = 0; i < len; i++) {
        char c = p[i];
        if (c == '$' || c == '%' || c == ';' || c == '\\' || c == '<' || c == '>'
            || c == '"' || c == '{' || c == '}' || c == '|' || c == ':')
            return 0;
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
            letters++;
        else if (!((c >= '0' && c <= '9') || c == ' ' || c == '\'' || c == '-' || c == '_'))
            return 0;
    }
    return letters >= 2;
}

/* "ABCxABC" — 0x1A8 misreads often look like NUOyNUO / GYOIGYO. */
static int IsDoubledJunkName(const char* p, unsigned len)
{
    if (len != 7 || !p)
        return 0;
    return p[0] == p[4] && p[1] == p[5] && p[2] == p[6];
}

static int CopyNameFromPtr(const char* p, char* dst, unsigned dstsz)
{
    unsigned len = 0;
    if (!p || !dst || dstsz < 3 || !Readable(p, 2))
        return 0;

    /* UTF-16LE ASCII names: A \0 B \0 C \0 \0 */
    if (p[1] == 0 && p[0] != 0 && Readable(p, 8) && p[3] == 0) {
        unsigned i;
        char tmp[48];
        for (i = 0; i < 24; i++) {
            const char* q = p + (i * 2u);
            if (!Readable(q, 2))
                return 0;
            if (q[1] != 0)
                break;
            if (q[0] == 0) {
                tmp[len] = 0;
                if (IsPlausibleUnitName(tmp, len) && !IsDoubledJunkName(tmp, len)) {
                    memcpy(dst, tmp, len + 1u);
                    return 1;
                }
                break;
            }
            if (!((q[0] >= 0x20 && q[0] < 0x7F)))
                break;
            tmp[len++] = q[0];
        }
    }

    len = 0;
    while (len + 1u < dstsz && len < 48u && Readable(p + len, 1)
           && ValidNameChar(p[len]) && p[len] != 0)
        len++;
    if (len < 2 || !Readable(p + len, 1) || p[len] != 0)
        return 0;
    if (!IsPlausibleUnitName(p, len) || IsDoubledJunkName(p, len))
        return 0;
    memcpy(dst, p, len);
    dst[len] = 0;
    return 1;
}

const char* ObjMgrObjectName(void* obj)
{
    /* DO NOT spray CGUnit name-cache offsets. Crash dumps 2026-08-23 18:09 and
     * 18:44: ACCESS_VIOLATION reading 0 at 0x720BA7BA, Lua stack
     * pcall(GmUnitName/GmObjectName) from GatherBot.goDisplayName and
     * ActionFlow.objName while walking gameobjects. pcall does not catch AV.
     * Those offsets are unit-only; on a GO (or a half-created unit) the
     * pointer is NULL and the process dies. Names come from DBC/catalog. */
    (void)obj;
    return NULL;
}

typedef struct PlayerCollectCtx {
    ObjMgrNamed* out;
    uint32_t max;
    uint32_t n;
    float px, py, pz;
    float max_dist;
    uint64_t self;
} PlayerCollectCtx;

/* WotLK / Ascension: player ObjectGuid high word is 0x0000. */
static int GuidLooksLikePlayer(uint64_t guid)
{
    return ((uint16_t)(guid >> 48)) == 0u && guid != 0ull;
}

static int CollectPlayersVisit(void* obj, void* ctx)
{
    PlayerCollectCtx* c = (PlayerCollectCtx*)ctx;
    ObjMgrNamed row;
    const char* name;
    float dx, dy, dz;
    uint32_t mask;
    uint64_t guid;
    int is_player;

    mask = ObjMgrTypeMask(obj);
    guid = ObjMgrObjectGuid(obj);
    is_player = (mask & kTypeMaskPlayer) != 0;
    if (!is_player && GuidLooksLikePlayer(guid))
        is_player = 1;
    if (!is_player)
        return 1;

    memset(&row, 0, sizeof(row));
    row.unit.guid = guid;
    if (!row.unit.guid || (c->self && row.unit.guid == c->self))
        return 1;
    if (!ObjMgrPosition(obj, &row.unit.x, &row.unit.y, &row.unit.z, NULL))
        return 1;

    dx = row.unit.x - c->px;
    dy = row.unit.y - c->py;
    dz = row.unit.z - c->pz;
    row.unit.dist = sqrtf(dx * dx + dy * dy + dz * dz);
    if (c->max_dist > 0.0f && row.unit.dist > c->max_dist)
        return 1;

    row.unit.type_mask = mask | kTypeMaskPlayer;
    row.unit.entry = EntryFromGuid(row.unit.guid);
    /* Keep players even when the name pointer probe misses — Lua resolves via
     * GmTargetGuid + UnitName. Empty name is OK. */
    name = ObjMgrObjectName(obj);
    row.name[0] = 0;
    if (name && name[0]) {
        unsigned nl = (unsigned)strlen(name);
        if (nl > kObjMgrNameMax)
            nl = kObjMgrNameMax;
        memcpy(row.name, name, nl);
        row.name[nl] = 0;
    }

    if (row.unit.dist <= 400.0f) {
        row.unit.health = ObjMgrUnitHealth(obj);
        row.unit.max_health = ObjMgrUnitBlock32(obj, kUnitBlockMaxHealth);
        row.unit.level = ObjMgrUnitBlock32(obj, kUnitBlockLevel);
        row.unit.faction = ObjMgrUnitBlock32(obj, kUnitBlockFaction);
    }

    if (c->n < c->max)
        c->out[c->n++] = row;
    else {
        uint32_t i, worst = 0;
        for (i = 1; i < c->n; i++)
            if (c->out[i].unit.dist > c->out[worst].unit.dist)
                worst = i;
        if (row.unit.dist < c->out[worst].unit.dist)
            c->out[worst] = row;
    }
    return 1;
}

uint32_t ObjMgrCollectPlayers(ObjMgrNamed* out, uint32_t max, float max_dist)
{
    PlayerCollectCtx c;
    void* self_obj;
    if (!out || !max)
        return 0;
    memset(&c, 0, sizeof(c));
    c.out = out;
    c.max = max;
    c.max_dist = (max_dist > 0.0f) ? max_dist : 200.0f;
    c.self = ObjMgrPlayerGuid();
    self_obj = ObjMgrPlayerObject();
    if (!self_obj || !ObjMgrPosition(self_obj, &c.px, &c.py, &c.pz, NULL))
        return 0;
    WalkObjects(CollectPlayersVisit, &c);
    return c.n;
}

/* ---- Live object list (uncached) ---- */
typedef struct ListCtx {
    void** out;
    uint32_t max;
    uint32_t n;
} ListCtx;

static int ListVisit(void* obj, void* ctx)
{
    ListCtx* c = (ListCtx*)ctx;
    if (c->n < c->max) {
        c->out[c->n++] = obj;
        return 1;
    }
    return 0; /* full */
}

uint32_t ObjMgrListObjects(void** out, uint32_t max)
{
    ListCtx c;
    if (!out || !max)
        return 0;
    c.out = out;
    c.max = max;
    c.n = 0;
    WalkObjects(ListVisit, &c);
    return c.n;
}
