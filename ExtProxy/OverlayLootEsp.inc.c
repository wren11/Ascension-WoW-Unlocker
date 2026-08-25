/* Native loot ESP — included from OverlayD3d9.c after FlushBatch.
 * Runs every Present without Lua. Radar is screen-space (no W2S).
 * World line/boxes need a non-identity view/proj captured during the world pass.
 */

enum { kEspLootMax = 48 };

typedef struct EspLoot {
    float x, y, z, dist;
    uint32_t type_mask;
    uint32_t entry;
    int kind; /* 1 = GO, 2 = unit/corpse */
    char name[40];
} EspLoot;

static int g_loot_esp_on = 1;
static float g_loot_esp_radius = 100.f;
static unsigned g_loot_esp_count = 0;
static int g_esp_logged;

void Overlay_SetLootEsp(int on) { g_loot_esp_on = on ? 1 : 0; }
int Overlay_GetLootEsp(void) { return g_loot_esp_on; }

void Overlay_SetLootEspRadius(float yards)
{
    if (yards < 10.f) yards = 10.f;
    if (yards > 250.f) yards = 250.f;
    g_loot_esp_radius = yards;
}

float Overlay_GetLootEspRadius(void) { return g_loot_esp_radius; }
unsigned Overlay_LootEspCount(void) { return g_loot_esp_count; }

/* Keep in sync with ProxyMain.c UnitIsLootCandidate. Cache-only: never
 * FindByGuid / GoType / ObjectName from Present (those walk the live OM). */
static int EspIsLootCandidate(const ObjMgrUnit* u)
{
    int is_player = (u->type_mask & kTypeMaskPlayer) != 0;
    int is_corpse = (u->type_mask & kTypeMaskCorpse) != 0;
    int is_unit = (u->type_mask & kTypeMaskUnit) != 0;
    int is_go = (u->type_mask & kTypeMaskGameObject) != 0;
    int flagged = (u->dyn_flags & kUnitDynLootable) != 0;
    uint32_t gt, goflags;

    if (is_go) {
        goflags = u->dyn_flags;
        if (goflags & (kGoFlagInUse | kGoFlagLocked | kGoFlagNoInteract))
            return 0;
        if (u->faction & kGoDynNoInteract)
            return 0;
        /* CollectVisit stores GAMEOBJECT type in level, DYNAMIC in faction. */
        gt = u->level;
        if (ObjMgrGoTypeIsInteractLoot(gt))
            return 1;
        if (u->faction & (kGoDynActivate | kGoDynSparkle))
            return 1;
        if (u->dist <= 40.f)
            return 1;
        return 0;
    }
    if (!(is_unit || is_corpse))
        return 0;
    if (is_player)
        return 0;
    if (!flagged)
        return 0;
    if ((u->dyn_flags & kUnitDynTapped) &&
        !(u->dyn_flags & kUnitDynTappedByPlayer) &&
        !(u->dyn_flags & kUnitDynTappedByAllThreat))
        return 0;
    return 1;
}

static void EspCopyName(char* dst, size_t cap, const char* src, uint32_t entry)
{
    size_t i, n;
    if (!dst || cap < 2)
        return;
    dst[0] = 0;
    if (src && src[0]) {
        n = 0;
        for (i = 0; src[i] && n + 1 < cap && n < 28; i++) {
            char c = src[i];
            if ((unsigned char)c < 32)
                continue;
            dst[n++] = c;
        }
        dst[n] = 0;
        if (dst[0])
            return;
    }
    snprintf(dst, cap, "E%u", (unsigned)entry);
}

static void EspCollect(EspLoot* out, int* nout, float* px, float* py, float* pz, float* pf)
{
    void* po;
    uint32_t i, c;
    uint64_t self;
    *nout = 0;
    *px = *py = *pz = *pf = 0.f;
    g_loot_esp_count = 0;
    if (!ObjMgrReady())
        return;
    /* Cache snapshot only. Pump/FindByGuid/ObjectName from Present AVd the
     * client (ExtProxy 0xC0000005 in ObjMgrReadClientFacing @ Crossroads). */
    po = ObjMgrPlayerObject();
    if (po) {
        ObjMgrPosition(po, px, py, pz, NULL);
        if (!ObjMgrPeekFacing(po, pf))
            *pf = 0.f;
    }
    self = ObjMgrPlayerGuid();
    c = ObjMgrCacheCount();
    for (i = 0; i < c && *nout < kEspLootMax; i++) {
        ObjMgrUnit u;
        EspLoot* e;
        float dx, dy, dz, dist;
        if (!ObjMgrCacheGet(i, &u))
            continue;
        if (self && u.guid == self)
            continue;
        dx = u.x - *px;
        dy = u.y - *py;
        dz = u.z - *pz;
        dist = sqrtf(dx * dx + dy * dy + dz * dz);
        if (dist > g_loot_esp_radius)
            continue;
        if (!EspIsLootCandidate(&u))
            continue;
        e = &out[*nout];
        e->x = u.x;
        e->y = u.y;
        e->z = u.z;
        e->dist = dist;
        e->type_mask = u.type_mask;
        e->entry = u.entry;
        e->kind = (u.type_mask & kTypeMaskGameObject) ? 1 : 2;
        EspCopyName(e->name, sizeof(e->name), NULL, u.entry);
        (*nout)++;
    }
    g_loot_esp_count = (unsigned)*nout;
}

static void BatchLineThick(float x1, float y1, float x2, float y2, DWORD color)
{
    BatchLine(x1, y1, x2, y2, color);
    BatchLine(x1 + 1.f, y1, x2 + 1.f, y2, color);
    BatchLine(x1, y1 + 1.f, x2, y2 + 1.f, color);
}

static void DrawLootEspNative(IDirect3DDevice9* dev)
{
    EspLoot loot[kEspLootMax];
    int n = 0, i, nearest = -1;
    float px, py, pz, pf;
    float best = 1.0e9f;
    OvViewport vp;
    float radar_x, radar_y, radar_s, radar_r, cx, cy, scale;
    DWORD col_frame, col_near, col_go, col_unit, col_txt, col_you;
    char line[96];
    int have_vp;
    float sf, cf;

    if (!g_loot_esp_on || !dev)
        return;

    EspCollect(loot, &n, &px, &py, &pz, &pf);
    for (i = 0; i < n; i++) {
        if (loot[i].dist < best) {
            best = loot[i].dist;
            nearest = i;
        }
    }

    have_vp = GetViewportSafe(dev, &vp);
    if (!have_vp) {
        vp.X = 0;
        vp.Y = 0;
        vp.Width = 1280;
        vp.Height = 720;
    }

    col_frame = ARGB(230, 40, 200, 90);
    col_near = ARGB(255, 80, 255, 255);
    col_go = ARGB(255, 255, 196, 48);
    col_unit = ARGB(255, 130, 255, 90);
    col_txt = ARGB(255, 255, 230, 140);
    col_you = ARGB(255, 255, 255, 255);

    radar_s = 196.f;
    radar_r = (radar_s * 0.5f) - 10.f;
    radar_x = 14.f + (float)vp.X;
    radar_y = (float)vp.Y + (float)vp.Height - radar_s - 18.f;
    if (radar_y < 70.f)
        radar_y = 70.f;
    cx = radar_x + radar_s * 0.5f;
    cy = radar_y + radar_s * 0.5f;
    scale = radar_r / g_loot_esp_radius;
    sf = sinf(pf);
    cf = cosf(pf);

    BatchRect(radar_x, radar_y, radar_s, radar_s, col_frame);
    BatchCircle(cx, cy, radar_r, ARGB(180, 50, 160, 80));
    BatchCircle(cx, cy, radar_r * 0.5f, ARGB(90, 40, 120, 60));
    BatchLine(cx, cy - radar_r, cx, cy + radar_r, ARGB(80, 60, 140, 70));
    BatchLine(cx - radar_r, cy, cx + radar_r, cy, ARGB(80, 60, 140, 70));
    /* Player facing is always up on the radar. */
    BatchLine(cx, cy, cx, cy - 14.f, col_you);
    BatchLine(cx, cy - 14.f, cx - 5.f, cy - 4.f, col_you);
    BatchLine(cx, cy - 14.f, cx + 5.f, cy - 4.f, col_you);

    snprintf(line, sizeof(line), "LOOT MAP  n=%d  r=%.0f", n, (double)g_loot_esp_radius);
    BatchText(radar_x + 6.f, radar_y + 4.f, line, col_txt);

    if (!ObjMgrReady())
        BatchText(radar_x + 6.f, radar_y + 18.f, "OM WAIT", ARGB(255, 255, 80, 80));
    else if (n == 0)
        BatchText(radar_x + 6.f, radar_y + 18.f, "NO LOOT IN RANGE", ARGB(255, 180, 180, 180));

    for (i = 0; i < n; i++) {
        float dx = loot[i].x - px;
        float dy = loot[i].y - py;
        float right = dx * cf - dy * sf;
        float fwd = dx * sf + dy * cf;
        float sx = cx + right * scale;
        float sy = cy - fwd * scale;
        float ddot = sqrtf((sx - cx) * (sx - cx) + (sy - cy) * (sy - cy));
        DWORD col = (i == nearest) ? col_near : (loot[i].kind == 1 ? col_go : col_unit);
        if (ddot > radar_r && ddot > 0.1f) {
            float k = radar_r / ddot;
            sx = cx + (sx - cx) * k;
            sy = cy + (sy - cy) * k;
        }
        if (i == nearest)
            BatchLineThick(cx, cy, sx, sy, col_near);
        BatchCircle(sx, sy, (i == nearest) ? 5.f : 3.f, col);
        BatchLine(sx - 3.f, sy, sx + 3.f, sy, col);
        BatchLine(sx, sy - 3.f, sx, sy + 3.f, col);
    }

    if (nearest >= 0) {
        snprintf(line, sizeof(line), "AIM  %.0fy  %s",
                 (double)loot[nearest].dist, loot[nearest].name);
        BatchText(radar_x + 6.f, radar_y + radar_s - 16.f, line, col_near);
    }

    /* Side list — up to 8 nearest by walking sorted-ish (cache is dist-sorted). */
    {
        int shown = 0;
        float ly = radar_y;
        BatchText(radar_x + radar_s + 8.f, ly, "INTERACT", col_txt);
        ly += 14.f;
        for (i = 0; i < n && shown < 8; i++) {
            DWORD col = (i == nearest) ? col_near : (loot[i].kind == 1 ? col_go : col_unit);
            snprintf(line, sizeof(line), "%5.0f  %s", (double)loot[i].dist, loot[i].name);
            BatchText(radar_x + radar_s + 8.f, ly, line, col);
            ly += 12.f;
            shown++;
        }
    }

    /* World ESP: player -> nearest + boxes. Falls back to screen-center origin. */
    if (nearest >= 0) {
        float sx1, sy1, sx2, sy2;
        int a, b;
        a = WorldToScreenDev(dev, px, py, pz + 1.2f, &sx1, &sy1);
        b = WorldToScreenDev(dev, loot[nearest].x, loot[nearest].y,
                             loot[nearest].z + 1.0f, &sx2, &sy2);
        if (!a) {
            sx1 = (float)vp.X + (float)vp.Width * 0.5f;
            sy1 = (float)vp.Y + (float)vp.Height * 0.58f;
            a = 1;
        }
        if (a && b)
            BatchLineThick(sx1, sy1, sx2, sy2, col_near);
        else if (a && !b)
            BatchLineThick(sx1, sy1, sx1, sy1 - 40.f, col_near);
    }

    for (i = 0; i < n; i++) {
        float sx, sy;
        float half = (loot[i].kind == 1) ? 0.55f : 0.4f;
        float ht = (loot[i].kind == 1) ? 1.4f : 1.8f;
        DWORD col = (i == nearest) ? col_near : (loot[i].kind == 1 ? col_go : col_unit);
        BatchWorldBox(dev, loot[i].x, loot[i].y, loot[i].z, half, ht, col);
        if (WorldToScreenDev(dev, loot[i].x, loot[i].y, loot[i].z + ht, &sx, &sy)) {
            snprintf(line, sizeof(line), "%.0f %s", (double)loot[i].dist, loot[i].name);
            BatchText(sx + 6.f, sy - 2.f, line, col);
        }
        if (g_batch_n + 256 > OV_MAX_BATCH_VERTS)
            FlushBatch(dev);
    }

    if (!g_esp_logged) {
        char msg[96];
        snprintf(msg, sizeof(msg), "loot ESP live n=%d hooked=%d", n, Overlay_Ready());
        LogO(msg);
        g_esp_logged = 1;
    }
}
