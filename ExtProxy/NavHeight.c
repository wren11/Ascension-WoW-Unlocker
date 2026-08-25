

#include <windows.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

#include "NavHeight.h"

void ProxyLogLine(const char* msg);

#define TILE_SIZE 533.33333f
#define VERTS_PER_POLY 6
#define POLY_BYTES (4 + 2 * VERTS_PER_POLY + 2 * VERTS_PER_POLY + 2 + 1 + 1)
#define LINK_BYTES 16
#define DTL_BYTES 12
#define MMAP_MAGIC 0x4D4D4150u
#define DNAV_MAGIC 0x444E4156u

static char g_root[MAX_PATH] = "";       /* .mmtile directory (mmtiles) */
static char g_maps_root[MAX_PATH] = "";  /* .mmap directory (maps) */

typedef struct NavTile {
    uint32_t map, gx, gy;
    int loaded;
    uint8_t* data;
    uint32_t polyCount, vertCount, detailMeshCount, detailVertCount, detailTriCount;
    const float* verts;
    const uint8_t* polys;
    const uint8_t* dmeshes;
    const float* dverts;
    const uint8_t* dtris;
    DWORD last_use;
} NavTile;

enum { kTileCache = 48 };
static NavTile g_tiles[kTileCache];
static CRITICAL_SECTION g_lock;
static int g_lock_ready;

static void EnsureLock(void)
{
    if (!g_lock_ready) {
        InitializeCriticalSection(&g_lock);
        g_lock_ready = 1;
    }
}

void NavHeightSetRoot(const char* dir)
{
    if (dir && dir[0]) {
        lstrcpynA(g_root, dir, MAX_PATH);
        ProxyLogLine("navheight: mmtiles (.mmtile) root set");
    }
}

char* NavHeightRoot(void) { return g_root; }

void NavMapsSetRoot(const char* dir)
{
    if (dir && dir[0]) {
        lstrcpynA(g_maps_root, dir, MAX_PATH);
        ProxyLogLine("navheight: maps (.mmap) root set");
    }
}

char* NavMapsRoot(void) { return g_maps_root; }

int NavMapMeshExists(uint32_t map)
{
    char path[MAX_PATH];
    if (!g_maps_root[0])
        return 1;
    _snprintf(path, sizeof(path), "%s\\%04u.mmap", g_maps_root, (unsigned)map);
    return GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES;
}

static uint32_t rd_u32(const uint8_t* p) { uint32_t v; memcpy(&v, p, 4); return v; }
static uint16_t rd_u16(const uint8_t* p) { uint16_t v; memcpy(&v, p, 2); return v; }

static int ParseTile(NavTile* t, uint32_t len)
{
    const uint8_t* d = t->data;
    uint32_t base, off, ints[15];
    int i;
    if (len < 120)
        return 0;
    if (rd_u32(d) != MMAP_MAGIC)
        return 0;
    base = 20;
    for (i = 0; i < 15; i++)
        ints[i] = rd_u32(d + base + i * 4);
    if (ints[0] != DNAV_MAGIC)
        return 0;
    t->polyCount = ints[6];
    t->vertCount = ints[7];
    {
        uint32_t maxLink = ints[8];
        t->detailMeshCount = ints[9];
        t->detailVertCount = ints[10];
        t->detailTriCount = ints[11];
        off = base + 100;

        t->verts = (const float*)(d + off);
        off += t->vertCount * 12u;
        t->polys = d + off;
        off += t->polyCount * POLY_BYTES;
        off += maxLink * LINK_BYTES;
        t->dmeshes = d + off;
        off += t->detailMeshCount * DTL_BYTES;
        t->dverts = (const float*)(d + off);
        off += t->detailVertCount * 12u;
        t->dtris = d + off;
        off += t->detailTriCount * 4u;
    }
    if (off > len)
        return 0;
    return 1;
}

static NavTile* LoadTile(uint32_t map, uint32_t gx, uint32_t gy)
{
    char path[MAX_PATH];
    HANDLE h;
    DWORD size, got = 0;
    NavTile* slot = NULL;
    DWORD oldest = 0xFFFFFFFFu;
    int i;

    if (!g_root[0])
        return NULL;
    if (!NavMapMeshExists(map))
        return NULL;

    for (i = 0; i < kTileCache; i++) {
        NavTile* t = &g_tiles[i];
        if (t->loaded && t->map == map && t->gx == gx && t->gy == gy) {
            t->last_use = GetTickCount();
            return t->loaded == 1 ? t : NULL;
        }
    }
    for (i = 0; i < kTileCache; i++) {
        if (!g_tiles[i].loaded) { slot = &g_tiles[i]; break; }
        if (g_tiles[i].last_use < oldest) { oldest = g_tiles[i].last_use; slot = &g_tiles[i]; }
    }
    if (slot->data) { HeapFree(GetProcessHeap(), 0, slot->data); slot->data = NULL; }
    memset(slot, 0, sizeof(*slot));
    slot->map = map; slot->gx = gx; slot->gy = gy;
    slot->last_use = GetTickCount();
    slot->loaded = -1;

    _snprintf(path, sizeof(path), "%s\\%04u%02u%02u.mmtile", g_root, map, gx, gy);
    h = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE)
        return NULL;
    size = GetFileSize(h, NULL);
    if (size == INVALID_FILE_SIZE || size < 120 || size > 64u * 1024u * 1024u) {
        CloseHandle(h);
        return NULL;
    }
    slot->data = (uint8_t*)HeapAlloc(GetProcessHeap(), 0, size);
    if (!slot->data) { CloseHandle(h); return NULL; }
    if (!ReadFile(h, slot->data, size, &got, NULL) || got != size) {
        CloseHandle(h);
        HeapFree(GetProcessHeap(), 0, slot->data);
        slot->data = NULL;
        return NULL;
    }
    CloseHandle(h);
    if (!ParseTile(slot, size)) {
        HeapFree(GetProcessHeap(), 0, slot->data);
        slot->data = NULL;
        return NULL;
    }
    slot->loaded = 1;
    return slot;
}

static int TriHeight(float px, float pz,
                     const float* a, const float* b, const float* c, float* out_y)
{
    float det = (b[2] - c[2]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[2] - c[2]);
    float u, v, w;
    if (fabsf(det) < 1e-6f)
        return 0;
    u = ((b[2] - c[2]) * (px - c[0]) + (c[0] - b[0]) * (pz - c[2])) / det;
    v = ((c[2] - a[2]) * (px - c[0]) + (a[0] - c[0]) * (pz - c[2])) / det;
    w = 1.0f - u - v;
    if (u < -0.001f || v < -0.001f || w < -0.001f)
        return 0;
    *out_y = u * a[1] + v * b[1] + w * c[1];
    return 1;
}

static int TileHeights(const NavTile* t, float rx, float rz, float* out, int cap)
{
    uint32_t pi;
    int n = 0;
    for (pi = 0; pi < t->polyCount && n < cap; pi++) {
        const uint8_t* poly = t->polys + pi * POLY_BYTES;
        uint8_t vc = poly[4 + 2 * VERTS_PER_POLY + 2 * VERTS_PER_POLY + 2];
        const uint8_t* dm = t->dmeshes + pi * DTL_BYTES;
        uint32_t vbase = rd_u32(dm);
        uint32_t tbase = rd_u32(dm + 4);
        uint8_t tcount = dm[9];
        uint32_t k;
        const float* pv[VERTS_PER_POLY];
        if (vc == 0 || vc > VERTS_PER_POLY)
            continue;
        for (k = 0; k < vc; k++)
            pv[k] = t->verts + rd_u16(poly + 4 + k * 2) * 3u;
        for (k = 0; k < tcount && n < cap; k++) {
            const uint8_t* tri = t->dtris + (tbase + k) * 4u;
            const float* c[3];
            int j;
            float y;
            for (j = 0; j < 3; j++) {
                uint8_t idx = tri[j];
                c[j] = (idx < vc) ? pv[idx] : (t->dverts + (vbase + (idx - vc)) * 3u);
            }
            if (TriHeight(rx, rz, c[0], c[1], c[2], &y))
                out[n++] = y;
        }
    }
    return n;
}

int NavHeightAt(uint32_t map, float x, float y, float z_hint, float* out_z)
{
    uint32_t gx, gy;
    NavTile* t;
    float hits[64];
    int n, i;
    float best = 0;
    int have = 0;
    int dummy_hint;
    if (!out_z)
        return 0;

    gx = (uint32_t)floorf(32.0f - x / TILE_SIZE);
    gy = (uint32_t)floorf(32.0f - y / TILE_SIZE);
    if (gx > 63 || gy > 63)
        return 0;

    /* 3000 was used as "any ground" and selected tree-tops / roofs (highest
     * poly below 3002). Dummy hints pick the LOWEST walkable sheet instead. */
    dummy_hint = (z_hint > 400.f || z_hint < -200.f);

    EnsureLock();
    EnterCriticalSection(&g_lock);
    t = LoadTile(map, gx, gy);
    if (t) {
        n = TileHeights(t, y, x, hits, 64);
        for (i = 0; i < n; i++) {
            float h = hits[i];
            if (!have) {
                best = h;
                have = 1;
                continue;
            }
            if (dummy_hint) {
                if (h < best)
                    best = h;
                continue;
            }
            /* Real hint: floor you are on = highest poly still under the unit
             * (+2 yd). Never jump to a canopy 8+ yd above the hint. */
            if (h <= z_hint + 2.0f && h >= z_hint - 80.0f) {
                if (best > z_hint + 2.0f || best < z_hint - 80.0f || h > best)
                    best = h;
            } else if (best > z_hint + 2.0f || best < z_hint - 80.0f) {
                float err = fabsf(h - z_hint);
                float best_err = fabsf(best - z_hint);
                if (err < best_err)
                    best = h;
            }
        }
    }
    LeaveCriticalSection(&g_lock);

    if (have)
        *out_z = best;
    return have;
}

int NavIsContinentMap(uint32_t map)
{
    return map == 0u || map == 1u || map == 530u || map == 571u;
}

enum { kContinentZPreferYd = 20 };

static uint32_t NavPickBestMap(const uint32_t* maps, int n,
                               float x, float y, float z_hint,
                               float* out_err)
{
    uint32_t best = 0xFFFFFFFFu;
    float best_err = 1e30f;
    int i;
    for (i = 0; i < n; i++) {
        float z = 0.f;
        float err;
        if (!NavHeightAt(maps[i], x, y, z_hint, &z))
            continue;
        /* Dummy hints (3000 / 500): lowest walkable sheet, not the continent
         * whose ground happens to sit closer to the dummy value. */
        if (z_hint > 400.f || z_hint < -200.f)
            err = z;
        else
            err = fabsf(z - z_hint);
        if (err < best_err) {
            best_err = err;
            best = maps[i];
        }
    }
    if (out_err)
        *out_err = best_err;
    return best;
}

uint32_t NavGuessMap(float x, float y, float z_hint)
{
    static const uint32_t kMaps[] = { 0u, 1u, 530u, 571u };
    return NavPickBestMap(kMaps, 4, x, y, z_hint, NULL);
}

uint32_t NavGuessMapInclusive(float x, float y, float z_hint)
{
    /* Pick the mesh whose ground Z best matches z_hint across continents AND
     * instances. BG XY often collides with continent tiles (same gx/gy); the
     * correct land Z is whichever poly sits nearest the unit's feet — not a
     * hard-coded continent-first or instance-first policy. */
    static const uint32_t kContinents[] = { 0u, 1u, 530u, 571u };
    static const uint32_t kInstances[] = {
        25u, 29u, 30u, 33u, 34u, 35u, 36u, 37u, 42u, 43u, 44u, 47u, 48u,
        70u, 90u, 109u, 129u, 169u, 189u, 209u, 229u, 230u, 249u, 269u,
        289u, 309u, 329u, 349u, 369u, 389u, 409u, 429u, 449u, 450u, 451u,
        469u, 489u, 509u, 529u, 531u, 533u,
        2720u, 2784u, 2789u, 2791u, 2804u, 2806u, 2807u, 2817u, 2832u,
        2853u, 2856u, 2868u, 2875u, 2902u, 2921u
    };
    float cont_err = 1e30f, inst_err = 1e30f;
    uint32_t cont, inst;

    cont = NavPickBestMap(kContinents, (int)(sizeof(kContinents) / sizeof(kContinents[0])),
                          x, y, z_hint, &cont_err);
    inst = NavPickBestMap(kInstances, (int)(sizeof(kInstances) / sizeof(kInstances[0])),
                          x, y, z_hint, &inst_err);

    if (inst == 0xFFFFFFFFu)
        return cont;
    if (cont == 0xFFFFFFFFu)
        return inst;
    return (inst_err <= cont_err) ? inst : cont;
}

int NavLineOfSight(uint32_t map,
                   float ax, float ay, float az,
                   float bx, float by, float bz,
                   float tolerance)
{
    float dx = bx - ax, dy = by - ay;
    float dist = sqrtf(dx * dx + dy * dy);
    int steps, i;
    if (tolerance <= 0.0f)
        tolerance = 2.0f;
    if (dist < 0.5f)
        return 1;
    steps = (int)(dist / 4.0f);
    if (steps < 2) steps = 2;
    if (steps > 256) steps = 256;

    for (i = 1; i < steps; i++) {
        float f = (float)i / (float)steps;
        float px = ax + dx * f;
        float py = ay + dy * f;
        float sight_z = az + (bz - az) * f;
        float ground;
        if (NavHeightAt(map, px, py, sight_z, &ground)) {

            if (ground > sight_z + tolerance)
                return 0;
        }
    }
    return 1;
}
