

#include <windows.h>
#include <math.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>

#include "NavPath.h"
#include "NavHeight.h"

void ProxyLogLine(const char* msg);
extern char* NavHeightRoot(void);

#define TILE_SIZE 533.33333f
#define VPP 6
#define POLY_BYTES 32
#define DT_EXT_LINK 0x8000u

typedef struct Poly {
    float cx, cy, cz;
    const float* v[VPP];
    uint16_t neis[VPP];
    uint8_t vc;
} Poly;

typedef struct Region {
    uint8_t* files[64];
    int fileCount;
    float* vbuf[64];
    Poly* polys;
    uint32_t* polyTileBase;
    int polyCount;
    int cap;

    int** adj;
    int* adjN;
    int* adjCap;
} Region;

static uint32_t rd_u32(const uint8_t* p) { uint32_t v; memcpy(&v, p, 4); return v; }
static uint16_t rd_u16(const uint8_t* p) { uint16_t v; memcpy(&v, p, 2); return v; }

static uint8_t* ReadFile_(const char* path, uint32_t* out_len)
{
    HANDLE h = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING,
                           FILE_ATTRIBUTE_NORMAL, NULL);
    DWORD size, got = 0;
    uint8_t* buf;
    if (h == INVALID_HANDLE_VALUE)
        return NULL;
    size = GetFileSize(h, NULL);
    if (size == INVALID_FILE_SIZE || size < 120 || size > 64u * 1024u * 1024u) {
        CloseHandle(h); return NULL;
    }
    buf = (uint8_t*)malloc(size);
    if (!buf) { CloseHandle(h); return NULL; }
    if (!ReadFile(h, buf, size, &got, NULL) || got != size) {
        CloseHandle(h); free(buf); return NULL;
    }
    CloseHandle(h);
    *out_len = size;
    return buf;
}

static void adjAdd(Region* r, int a, int b)
{
    if (r->adjN[a] >= r->adjCap[a]) {
        int nc = r->adjCap[a] ? r->adjCap[a] * 2 : 6;
        r->adj[a] = (int*)realloc(r->adj[a], nc * sizeof(int));
        r->adjCap[a] = nc;
    }

    for (int i = 0; i < r->adjN[a]; i++)
        if (r->adj[a][i] == b) return;
    r->adj[a][r->adjN[a]++] = b;
}
static void adjLink(Region* r, int a, int b) { adjAdd(r, a, b); adjAdd(r, b, a); }

typedef struct Ext { int gi; int side; float lo, hi, h, coord; int axis; } Ext;

static int ParseFileInto(Region* R, uint8_t* d, uint32_t len)
{
    uint32_t ints[15], base, off, pc, vc, i;
    float* vbuf;
    int fileIdx = R->fileCount;
    (void)base;
    if (len < 120) return 0;
    if (rd_u32(d) != 0x4D4D4150u) return 0;
    for (i = 0; i < 15; i++) ints[i] = rd_u32(d + 20 + i * 4);
    if (ints[0] != 0x444E4156u) return 0;
    pc = ints[6]; vc = ints[7];
    off = 20 + 100;
    vbuf = (float*)(d + off);

    R->files[fileIdx] = d;
    R->vbuf[fileIdx] = vbuf;
    R->polyTileBase[fileIdx] = (uint32_t)R->polyCount;
    R->fileCount++;

    const uint8_t* polyBytes = d + off + vc * 12u;
    for (i = 0; i < pc; i++) {
        const uint8_t* pb = polyBytes + i * POLY_BYTES;
        Poly* P;
        uint8_t pvc = pb[30];
        int k;
        if (R->polyCount >= R->cap) return 0;
        P = &R->polys[R->polyCount];
        memset(P, 0, sizeof(*P));
        P->vc = pvc;
        if (pvc > 0 && pvc <= VPP) {
            float sx = 0, sy = 0, sz = 0;
            for (k = 0; k < pvc; k++) {
                const float* vv = vbuf + (uint32_t)rd_u16(pb + 4 + k * 2) * 3u;
                P->v[k] = vv;
                P->neis[k] = rd_u16(pb + 16 + k * 2);
                sx += vv[0]; sy += vv[1]; sz += vv[2];
            }
            P->cx = sx / pvc; P->cy = sy / pvc; P->cz = sz / pvc;
        }
        R->polyCount++;
    }
    return 1;
}

static void FreeRegion(Region* R)
{
    int i;
    for (i = 0; i < R->fileCount; i++) free(R->files[i]);
    for (i = 0; i < R->polyCount; i++) free(R->adj[i]);
    free(R->polys); free(R->polyTileBase);
    free(R->adj); free(R->adjN); free(R->adjCap);
}

static int BuildRegion(Region* R, uint32_t map, int gx0, int gy0, int gx1, int gy1)
{
    char path[MAX_PATH];
    const char* root = NavHeightRoot();
    int gx, gy;
    Ext* ext = NULL; int extN = 0, extCap = 0;

    memset(R, 0, sizeof(*R));

    if (!root || !root[0])
        return 0;
    if (!NavMapMeshExists(map))
        return 0;

    R->cap = 0;


    R->cap = 4096;
    R->polys = (Poly*)malloc(sizeof(Poly) * R->cap);
    R->polyTileBase = (uint32_t*)malloc(sizeof(uint32_t) * 64);

    for (gy = gy0; gy <= gy1; gy++) {
        for (gx = gx0; gx <= gx1; gx++) {
            uint32_t flen;
            uint8_t* d;
            uint32_t pc;
            if (R->fileCount >= 64) break;
            _snprintf(path, sizeof(path), "%s\\%04u%02u%02u.mmtile",
                      root, map, (unsigned)gx, (unsigned)gy);
            d = ReadFile_(path, &flen);
            if (!d) continue;
            pc = rd_u32(d + 20 + 6 * 4);

            while (R->polyCount + (int)pc > R->cap) {
                R->cap *= 2;
                R->polys = (Poly*)realloc(R->polys, sizeof(Poly) * R->cap);
            }
            if (!ParseFileInto(R, d, flen))
                free(d);
        }
    }
    if (R->polyCount == 0) { free(R->polys); free(R->polyTileBase); return 0; }


    R->adj = (int**)calloc(R->polyCount, sizeof(int*));
    R->adjN = (int*)calloc(R->polyCount, sizeof(int));
    R->adjCap = (int*)calloc(R->polyCount, sizeof(int));


    {
        int fi;
        for (fi = 0; fi < R->fileCount; fi++) {
            uint32_t tbase = R->polyTileBase[fi];
            uint32_t tend = (fi + 1 < R->fileCount) ? R->polyTileBase[fi + 1]
                                                    : (uint32_t)R->polyCount;
            uint32_t gi;
            for (gi = tbase; gi < tend; gi++) {
                Poly* P = &R->polys[gi];
                int k;
                for (k = 0; k < P->vc; k++) {
                    uint16_t ne = P->neis[k];
                    if (ne != 0 && !(ne & DT_EXT_LINK)) {
                        int nb = (int)tbase + (ne - 1);
                        if (nb >= (int)tbase && nb < (int)tend)
                            adjLink(R, (int)gi, nb);
                    } else if (ne & DT_EXT_LINK) {
                        const float* a = P->v[k];
                        const float* b = P->v[(k + 1) % P->vc];
                        int side = ne & 0xff;
                        Ext e;
                        e.gi = (int)gi; e.side = side;
                        e.h = (a[1] + b[1]) * 0.5f;
                        if (side == 0 || side == 4) {
                            e.axis = 0; e.coord = a[0];
                            e.lo = a[2] < b[2] ? a[2] : b[2];
                            e.hi = a[2] > b[2] ? a[2] : b[2];
                        } else if (side == 2 || side == 6) {
                            e.axis = 1; e.coord = a[2];
                            e.lo = a[0] < b[0] ? a[0] : b[0];
                            e.hi = a[0] > b[0] ? a[0] : b[0];
                        } else {
                            continue;
                        }
                        if (extN >= extCap) {
                            extCap = extCap ? extCap * 2 : 256;
                            ext = (Ext*)realloc(ext, extCap * sizeof(Ext));
                        }
                        ext[extN++] = e;
                    }
                }
            }
        }
    }



    {
        int i, j;
        for (i = 0; i < extN; i++) {
            for (j = i + 1; j < extN; j++) {
                Ext* A = &ext[i];
                Ext* B = &ext[j];
                if (A->gi == B->gi) continue;
                if (A->axis != B->axis) continue;
                if ((A->side ^ 4) != B->side) continue;
                if (fabsf(A->coord - B->coord) > 0.5f) continue;
                if (fabsf(A->h - B->h) > 2.0f) continue;
                {
                    float lo = A->lo > B->lo ? A->lo : B->lo;
                    float hi = A->hi < B->hi ? A->hi : B->hi;
                    if (hi - lo > 0.01f)
                        adjLink(R, A->gi, B->gi);
                }
            }
        }
    }
    free(ext);
    return 1;
}

static float d3(const Poly* a, const Poly* b)
{
    float dx = a->cx - b->cx, dy = a->cy - b->cy, dz = a->cz - b->cz;
    return sqrtf(dx * dx + dy * dy + dz * dz);
}

static int PointInPoly(const Poly* P, float px, float pz)
{
    int inside = 0, i, j = P->vc - 1;
    for (i = 0; i < P->vc; i++) {
        const float* vi = P->v[i];
        const float* vj = P->v[j];
        if (((vi[2] > pz) != (vj[2] > pz)) &&
            (px < (vj[0] - vi[0]) * (pz - vi[2]) / (vj[2] - vi[2]) + vi[0]))
            inside = !inside;
        j = i;
    }
    return inside;
}

static int NearestPoly(Region* R, float rx, float rz, float ry)
{
    int best = -1, i;
    float bd = 1e30f;
    for (i = 0; i < R->polyCount; i++) {
        Poly* P = &R->polys[i];
        if (P->vc == 0) continue;
        if (PointInPoly(P, rx, rz)) {
            float dh = fabsf(P->cy - ry);
            if (dh < bd) { bd = dh; best = i; }
        }
    }
    if (best >= 0) return best;
    for (i = 0; i < R->polyCount; i++) {
        Poly* P = &R->polys[i];
        float dx, dz, dd;
        if (P->vc == 0) continue;
        dx = P->cx - rx; dz = P->cz - rz;
        dd = dx * dx + dz * dz + (P->cy - ry) * (P->cy - ry) * 9.0f;
        if (dd < bd) { bd = dd; best = i; }
    }
    return best;
}

typedef struct { float f; int p; } HNode;
typedef struct { HNode* a; int n, cap; } Heap;
static void hpush(Heap* h, float f, int p)
{
    int i;
    if (h->n >= h->cap) { h->cap = h->cap ? h->cap * 2 : 256; h->a = (HNode*)realloc(h->a, h->cap * sizeof(HNode)); }
    i = h->n++;
    h->a[i].f = f; h->a[i].p = p;
    while (i > 0) {
        int par = (i - 1) / 2;
        if (h->a[par].f <= h->a[i].f) break;
        { HNode t = h->a[par]; h->a[par] = h->a[i]; h->a[i] = t; }
        i = par;
    }
}
static int hpop(Heap* h, int* outp)
{
    int i = 0;
    if (h->n == 0) return 0;
    *outp = h->a[0].p;
    h->a[0] = h->a[--h->n];
    for (;;) {
        int l = 2 * i + 1, rr = 2 * i + 2, sm = i;
        if (l < h->n && h->a[l].f < h->a[sm].f) sm = l;
        if (rr < h->n && h->a[rr].f < h->a[sm].f) sm = rr;
        if (sm == i) break;
        { HNode t = h->a[sm]; h->a[sm] = h->a[i]; h->a[i] = t; }
        i = sm;
    }
    return 1;
}

static int AStar(Region* R, int s, int t, int** outPath)
{
    float* g = (float*)malloc(sizeof(float) * R->polyCount);
    int* came = (int*)malloc(sizeof(int) * R->polyCount);
    char* closed = (char*)calloc(R->polyCount, 1);
    Heap h; int i, cur, count = 0, *path = NULL;
    for (i = 0; i < R->polyCount; i++) { g[i] = 1e30f; came[i] = -1; }
    h.a = NULL; h.n = h.cap = 0;
    g[s] = 0;
    hpush(&h, d3(&R->polys[s], &R->polys[t]), s);
    while (hpop(&h, &cur)) {
        if (closed[cur]) continue;
        closed[cur] = 1;
        if (cur == t) break;
        for (i = 0; i < R->adjN[cur]; i++) {
            int nb = R->adj[cur][i];
            float ng;
            if (R->polys[nb].vc == 0) continue;
            ng = g[cur] + d3(&R->polys[cur], &R->polys[nb]);
            if (ng < g[nb]) {
                g[nb] = ng; came[nb] = cur;
                hpush(&h, ng + d3(&R->polys[nb], &R->polys[t]), nb);
            }
        }
    }
    if (closed[t] || came[t] != -1 || s == t) {
        int n = 0, c = t;
        while (c != -1) { n++; if (c == s) break; c = came[c]; }
        if (c == s || s == t) {
            path = (int*)malloc(sizeof(int) * n);
            c = t;
            for (i = n - 1; i >= 0; i--) { path[i] = c; c = came[c]; }
            count = n;
        }
    }
    free(g); free(came); free(closed); free(h.a);
    *outPath = path;
    return count;
}

static float triarea2(float ax, float az, float bx, float bz, float cx, float cz)
{
    return (bx - ax) * (cz - az) - (cx - ax) * (bz - az);
}

static int Portal(const Poly* p, const Poly* q,
                  float* lx, float* lz, float* rx, float* rz)
{
    int k;
    for (k = 0; k < p->vc; k++) {
        const float* a = p->v[k];
        const float* b = p->v[(k + 1) % p->vc];
        int fa = 0, fb = 0, m;
        for (m = 0; m < q->vc; m++) {
            const float* qv = q->v[m];
            if (fabsf(qv[0] - a[0]) < 0.05f && fabsf(qv[2] - a[2]) < 0.05f) fa = 1;
            if (fabsf(qv[0] - b[0]) < 0.05f && fabsf(qv[2] - b[2]) < 0.05f) fb = 1;
        }
        if (fa && fb) {

            float s = triarea2(p->cx, p->cz, q->cx, q->cz, a[0], a[2]);
            if (s >= 0.0f) {
                *lx = a[0]; *lz = a[2]; *rx = b[0]; *rz = b[2];
            } else {
                *lx = b[0]; *lz = b[2]; *rx = a[0]; *rz = a[2];
            }
            return 1;
        }
    }
    return 0;
}

static int Funnel(Region* R, int* corridor, int n, float* outXZ, int maxPts)
{
    int np = 0, i;
    float portalX[512 * 2], portalZ[512 * 2];
    int pc = 0;
    Poly* sp = &R->polys[corridor[0]];
    Poly* gp = &R->polys[corridor[n - 1]];

    if (n > 500) n = 500;

    portalX[pc * 2 + 0] = sp->cx; portalZ[pc * 2 + 0] = sp->cz;
    portalX[pc * 2 + 1] = sp->cx; portalZ[pc * 2 + 1] = sp->cz;
    pc++;
    for (i = 0; i < n - 1; i++) {
        float lx, lz, rx, rz;
        if (Portal(&R->polys[corridor[i]], &R->polys[corridor[i + 1]], &lx, &lz, &rx, &rz)) {
            portalX[pc * 2 + 0] = lx; portalZ[pc * 2 + 0] = lz;
            portalX[pc * 2 + 1] = rx; portalZ[pc * 2 + 1] = rz;
            pc++;
        }
    }
    portalX[pc * 2 + 0] = gp->cx; portalZ[pc * 2 + 0] = gp->cz;
    portalX[pc * 2 + 1] = gp->cx; portalZ[pc * 2 + 1] = gp->cz;
    pc++;

    {
        float apexX = portalX[0], apexZ = portalZ[0];
        float lX = portalX[0], lZ = portalZ[0];
        float rX = portalX[1], rZ = portalZ[1];
        int apexI = 0, lI = 0, rI = 0;
        if (np < maxPts) { outXZ[np * 2] = apexX; outXZ[np * 2 + 1] = apexZ; np++; }
        i = 1;
        while (i < pc) {
            float nlX = portalX[i * 2 + 0], nlZ = portalZ[i * 2 + 0];
            float nrX = portalX[i * 2 + 1], nrZ = portalZ[i * 2 + 1];

            if (triarea2(apexX, apexZ, rX, rZ, nrX, nrZ) <= 0.0f) {
                if ((apexX == rX && apexZ == rZ) ||
                    triarea2(apexX, apexZ, lX, lZ, nrX, nrZ) > 0.0f) {
                    rX = nrX; rZ = nrZ; rI = i;
                } else {
                    if (np < maxPts) { outXZ[np * 2] = lX; outXZ[np * 2 + 1] = lZ; np++; }
                    apexX = lX; apexZ = lZ; apexI = lI;
                    lX = apexX; lZ = apexZ; rX = apexX; rZ = apexZ;
                    lI = apexI; rI = apexI;
                    i = apexI + 1;
                    continue;
                }
            }

            if (triarea2(apexX, apexZ, lX, lZ, nlX, nlZ) >= 0.0f) {
                if ((apexX == lX && apexZ == lZ) ||
                    triarea2(apexX, apexZ, rX, rZ, nlX, nlZ) < 0.0f) {
                    lX = nlX; lZ = nlZ; lI = i;
                } else {
                    if (np < maxPts) { outXZ[np * 2] = rX; outXZ[np * 2 + 1] = rZ; np++; }
                    apexX = rX; apexZ = rZ; apexI = rI;
                    lX = apexX; lZ = apexZ; rX = apexX; rZ = apexZ;
                    lI = apexI; rI = apexI;
                    i = apexI + 1;
                    continue;
                }
            }
            i++;
        }
        if (np < maxPts) {
            outXZ[np * 2] = gp->cx; outXZ[np * 2 + 1] = gp->cz; np++;
        }
    }
    return np;
}

static float clampf(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

int NavFindPath(uint32_t map,
                float sx, float sy, float sz,
                float ex, float ey, float ez,
                float* out, int max_pts)
{
    Region R;
    int gxs, gys, gxe, gye, gx0, gy0, gx1, gy1;
    int sPoly, ePoly, corridorN, *corridor = NULL;
    int nwp = 0, i;
    float xz[256 * 2];



    gxs = (int)floorf(32.0f - sx / TILE_SIZE);
    gys = (int)floorf(32.0f - sy / TILE_SIZE);
    gxe = (int)floorf(32.0f - ex / TILE_SIZE);
    gye = (int)floorf(32.0f - ey / TILE_SIZE);
    gx0 = (gxs < gxe ? gxs : gxe) - 1;
    gx1 = (gxs > gxe ? gxs : gxe) + 1;
    gy0 = (gys < gye ? gys : gye) - 1;
    gy1 = (gys > gye ? gys : gye) + 1;

    if (gx1 - gx0 > 6) gx1 = gx0 + 6;
    if (gy1 - gy0 > 6) gy1 = gy0 + 6;

    {
        char b[192];
        _snprintf(b, sizeof(b),
            "navpath: map=%u start(%.0f,%.0f,%.0f) end(%.0f,%.0f,%.0f) grid gx[%d..%d] gy[%d..%d]",
            map, sx, sy, sz, ex, ey, ez, gx0, gx1, gy0, gy1);
        ProxyLogLine(b);
    }

    if (!BuildRegion(&R, map, gx0, gy0, gx1, gy1)) {
        ProxyLogLine("navpath: BuildRegion failed (no tiles loaded)");
        return 0;
    }
    {
        char b[128];
        _snprintf(b, sizeof(b), "navpath: region files=%d polys=%d",
                  R.fileCount, R.polyCount);
        ProxyLogLine(b);
    }


    sPoly = NearestPoly(&R, sy, sx, sz);
    if (sPoly < 0) {
        ProxyLogLine("navpath: sPoly=NONE (player not on mesh)");
        FreeRegion(&R);
        return 0;
    }



    {
        char* inComp = (char*)calloc(R.polyCount, 1);
        int* stack = (int*)malloc(sizeof(int) * R.polyCount);
        int sp = 0, compN = 0, k;
        float bestD = 1e30f;
        float grx = ey, grz = ex, gry = ez;
        inComp[sPoly] = 1; stack[sp++] = sPoly;
        ePoly = -1;
        while (sp > 0) {
            int cur = stack[--sp];
            Poly* P = &R.polys[cur];
            float dx, dz, dd;
            compN++;

            if (P->vc) {
                if (PointInPoly(P, grx, grz)) {
                    float dh = fabsf(P->cy - gry) + 0.0f;
                    if (dh < bestD) { bestD = dh; ePoly = cur; }
                } else if (ePoly < 0 || bestD > 1e20f) {
                    dx = P->cx - grx; dz = P->cz - grz;
                    dd = dx * dx + dz * dz + (P->cy - gry) * (P->cy - gry) * 4.0f;
                    if (dd < bestD) { bestD = dd; ePoly = cur; }
                }
            }
            for (k = 0; k < R.adjN[cur]; k++) {
                int nb = R.adj[cur][k];
                if (!inComp[nb] && R.polys[nb].vc) { inComp[nb] = 1; stack[sp++] = nb; }
            }
        }
        free(inComp); free(stack);
        {
            char b[192];
            _snprintf(b, sizeof(b),
                "navpath: sPoly=%d sc(%.0f,%.0f,%.0f) component=%d ePoly=%d ec(%.0f,%.0f,%.0f)",
                sPoly, R.polys[sPoly].cz, R.polys[sPoly].cx, R.polys[sPoly].cy, compN, ePoly,
                ePoly >= 0 ? R.polys[ePoly].cz : 0, ePoly >= 0 ? R.polys[ePoly].cx : 0,
                ePoly >= 0 ? R.polys[ePoly].cy : 0);
            ProxyLogLine(b);
        }
    }
    if (ePoly < 0) { FreeRegion(&R); return 0; }

    corridorN = AStar(&R, sPoly, ePoly, &corridor);
    {
        char b[96];
        _snprintf(b, sizeof(b), "navpath: corridor=%d %s", corridorN,
                  corridorN >= 1 ? "OK" : "NO PATH (disconnected)");
        ProxyLogLine(b);
    }
    if (corridorN < 1 || !corridor) { FreeRegion(&R); return 0; }

    {
        int m = Funnel(&R, corridor, corridorN, xz, 256);


        for (i = 0; i < m && nwp < max_pts; i++) {
            float rx = xz[i * 2 + 0];
            float rz = xz[i * 2 + 1];
            float wy = rx, wx = rz;
            float bestY = (i == 0) ? sz : (i == m - 1 ? ez : out[(nwp > 0 ? nwp - 1 : 0) * 3 + 2]);
            float bestD = 1e30f;
            int c;
            for (c = 0; c < corridorN; c++) {
                Poly* P = &R.polys[corridor[c]];
                float dx, dz, dd;
                if (P->vc == 0) continue;
                if (PointInPoly(P, rx, rz)) { bestY = P->cy; break; }
                dx = P->cx - rx; dz = P->cz - rz;
                dd = dx * dx + dz * dz;
                if (dd < bestD) { bestD = dd; bestY = P->cy; }
            }
            out[nwp * 3 + 0] = wx;
            out[nwp * 3 + 1] = wy;
            out[nwp * 3 + 2] = bestY;
            nwp++;
        }
    }
    (void)clampf;
    free(corridor);
    FreeRegion(&R);
    return nwp;
}
