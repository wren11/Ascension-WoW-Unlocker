#include "FogExplore.h"
#include "ObjectMgr.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>

static uint8_t* g_base = NULL;

static uint32_t g_at_min = 0;
static uint32_t g_at_max = 0;
static void* g_at_rows = NULL;
static int g_at_ok = 0;

enum { kAreaCacheSlots = 1024 };
typedef struct FogAreaCacheEnt {
    uint32_t area;
    uint32_t bit;
    int always;
    int valid;
} FogAreaCacheEnt;
static FogAreaCacheEnt g_area_cache[kAreaCacheSlots];

void FogInit(uint8_t* ascension_base)
{
    g_base = ascension_base;
    g_at_ok = 0;
    g_at_min = g_at_max = 0;
    g_at_rows = NULL;
    ZeroMemory(g_area_cache, sizeof(g_area_cache));
}

static int Readable(const void* p, size_t n)
{
    MEMORY_BASIC_INFORMATION mbi;
    uintptr_t q, end;
    if (!p || !n)
        return 0;
    if (VirtualQuery(p, &mbi, sizeof(mbi)) != sizeof(mbi))
        return 0;
    if (mbi.State != MEM_COMMIT)
        return 0;
    if (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD))
        return 0;
    q = (uintptr_t)p;
    end = (uintptr_t)mbi.BaseAddress + mbi.RegionSize;
    return (q + n) <= end;
}

static int Read32(const void* p, uint32_t* out)
{
    if (!Readable(p, 4) || !out)
        return 0;
    *out = *(const uint32_t*)p;
    return 1;
}

static int ReadPtr(const void* p, void** out)
{
    uint32_t v;
    if (!Read32(p, &v) || !out)
        return 0;
    *out = (void*)(uintptr_t)v;
    return 1;
}

static int EnsureAreaTable(void)
{
    uint32_t min_id = 0, max_id = 0;
    void* rows = NULL;
    if (g_at_ok)
        return 1;
    if (!g_base)
        return 0;
    if (!Read32(g_base + kAreaTableMinIdRva, &min_id))
        return 0;
    if (!Read32(g_base + kAreaTableMaxIdRva, &max_id))
        return 0;
    if (!ReadPtr(g_base + kAreaTableRowsRva, &rows) || !rows)
        return 0;
    g_at_min = min_id;
    g_at_max = max_id;
    g_at_rows = rows;
    g_at_ok = 1;
    return 1;
}

uint32_t FogExploredWord(uint32_t word_index)
{
    void* player;
    if (word_index >= kPlayerExploredZonesWords)
        return 0;
    player = ObjMgrPlayerObject();
    if (!player)
        return 0;
    return ObjMgrField32(player, kFieldPlayerExploredZones1 + word_index);
}

int FogIsExploredBit(uint32_t area_bit)
{
    uint32_t word, mask;
    void* player;

    if (area_bit >= 4096u)
        return -1;
    player = ObjMgrPlayerObject();
    if (!player)
        return -1;
    word = ObjMgrField32(player, kFieldPlayerExploredZones1 + (area_bit >> 5));
    mask = 1u << (area_bit & 31u);
    return (word & mask) ? 1 : 0;
}

static void* AreaTableEntry(uint32_t area_id)
{
    void* entry = NULL;
    uint32_t index;
    if (!EnsureAreaTable())
        return NULL;
    if (area_id < g_at_min || area_id > g_at_max)
        return NULL;
    index = area_id - g_at_min;
    if (!ReadPtr((const uint8_t*)g_at_rows + index * 4u, &entry) || !entry)
        return NULL;
    if (!Readable(entry, kAreaTableExplLevelOff + 4u))
        return NULL;
    return entry;
}

static FogAreaCacheEnt* ResolveAreaCached(uint32_t area_id)
{
    FogAreaCacheEnt* slot;
    void* entry;
    uint32_t bit = 0;
    int32_t expl_level = 0;

    slot = &g_area_cache[area_id % kAreaCacheSlots];
    if (slot->valid != 0 && slot->area == area_id)
        return slot;

    slot->area = area_id;
    slot->bit = 0;
    slot->always = 0;
    slot->valid = -1;

    entry = AreaTableEntry(area_id);
    if (!entry)
        return slot;
    if (!Read32((const uint8_t*)entry + kAreaTableExplLevelOff, (uint32_t*)&expl_level))
        return slot;
    if (expl_level < 0) {
        slot->always = 1;
        slot->valid = 1;
        return slot;
    }
    if (!Read32((const uint8_t*)entry + kAreaTableAreaBitOff, &bit))
        return slot;
    if (bit >= 4096u)
        return slot;
    slot->bit = bit;
    slot->valid = 1;
    return slot;
}

int FogAreaBit(uint32_t area_id, uint32_t* out_bit)
{
    FogAreaCacheEnt* e = ResolveAreaCached(area_id);
    if (!e || e->valid != 1 || e->always || !out_bit)
        return 0;
    *out_bit = e->bit;
    return 1;
}

int FogIsAreaExplored(uint32_t area_id)
{
    FogAreaCacheEnt* e = ResolveAreaCached(area_id);
    if (!e || e->valid != 1)
        return -1;
    if (e->always)
        return 1;
    return FogIsExploredBit(e->bit);
}
