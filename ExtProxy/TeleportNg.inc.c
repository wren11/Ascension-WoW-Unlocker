

enum {
    kNgEe = 0,
    kNgEeNoJump = 1,
    kNgEeE1 = 2,
    kNgEeBurst = 3,
    kNgClientOnly = 4,
    kNgEeAck = 5,
    kNgCrossTry = 6,
    kNgExplore = 7,
    kNgExploreLight = 8,
    kNgModeMax = 8
};

static char g_ng_detail[192] = "ng: idle";
static volatile LONG g_ng_last_ok = 0;
static volatile LONG g_ng_last_mode = -1;

#define NgSetDetail(...) \
    do { \
        _snprintf(g_ng_detail, sizeof(g_ng_detail), __VA_ARGS__); \
        g_ng_detail[sizeof(g_ng_detail) - 1] = '\0'; \
    } while (0)

static int NgInjectSetRaw(float x, float y, float z, float o)
{
    /* CMSG_MOVE_SET_RAW_POSITION 0xE1 is a cheat/GM opcode on this core.
     * Injecting it on a player account yields LANG_COMMAND_PERMISSIONS
     * and does not move the server vis. Heartbeat EE is the player path. */
    (void)x; (void)y; (void)z; (void)o;
    return 0;
}

static int NgInjectWorldportAck(void)
{
    uint8_t buf[8];
    uint32_t op = kOpcodeMoveWorldportAck;
    memset(buf, 0, sizeof(buf));
    memcpy(buf + 0, &op, 4);
    return InjectClientPacket(buf, 4) ? 1 : 0;
}

static int NgEe(float x, float y, float z, float o, uint32_t flags, uint32_t lock_ms)
{
    return ProxyTeleportSafeEx(x, y, z, o, flags | kTpSkipGround | kTpSkipJump, lock_ms) ? 1 : 0;
}

static int NgEeLight(float x, float y, float z, float o, uint32_t flags, uint32_t lock_ms)
{
    uint32_t pin_ms = (lock_ms > 0u) ? lock_ms : ProxyDefaultTpLockMs();
    uint32_t f = flags | kTpSkipGround | kTpSkipJump;
    if (!ProxyTeleportSafeEx(x, y, z, o, f, pin_ms))
        return 0;
    ProxyTeleportLoadKick(x, y, z, o);
    ProxyTpPulseEx(pin_ms);
    if (!ProxyTeleportSafeEx(x, y, z, o, f, pin_ms))
        return 0;
    ProxyTeleportLoadKick(x, y, z, o);
    ProxyTpLock(x, y, z, o, pin_ms, 28.f);
    {
        void* self = ObjMgrPlayerObject();
        if (self)
            ObjMgrSetPosition(self, x, y, z, o);
    }
    return 1;
}

static int NgClientPin(float x, float y, float z, float o, uint32_t lock_ms)
{
    void* self;
    uint32_t pin_ms = (lock_ms > 0u) ? lock_ms : ProxyDefaultTpLockMs();
    if (pin_ms > 100u)
        pin_ms = 100u;
    ProxyTpLock(x, y, z, o, pin_ms, 28.f);
    self = ObjMgrPlayerObject();
    if (self)
        ObjMgrSetPosition(self, x, y, z, o);
    g_last_player_x = x;
    g_last_player_y = y;
    g_last_player_z = z;
    g_player_facing = o;
    InterlockedExchange(&g_facing_valid, 1);
    InterlockedExchange(&g_last_player_valid, 1);

    return 1;
}

static int NgBurst(float x, float y, float z, float o, uint32_t flags, uint32_t lock_ms)
{
    uint32_t pin_ms = (lock_ms > 0u) ? lock_ms : ProxyDefaultTpLockMs();

    if (!NgEe(x, y, z, o, flags | kTpSkipOmInvalidate, pin_ms))
        return 0;
    ProxyTpPulseEx(pin_ms);
    if (!NgEe(x, y, z, o, flags | kTpSkipJump | kTpSkipOmInvalidate, pin_ms))
        return 0;
    ProxyTpLock(x, y, z, o, pin_ms, 28.f);
    return 1;
}

static int NgCrossTry(float x, float y, float z, float o, uint32_t map_id,
                      uint32_t flags, uint32_t lock_ms)
{
    uint32_t here;
    int e1, ack;

    RefreshLastMapFromPlayer();
    here = g_last_map;

    if (map_id != kMapIdUnknown && here == map_id) {
        int ok = NgEeLight(x, y, z, o, flags, lock_ms);
        NgSetDetail("ng-cross: already on map %u — EE light %s", map_id, ok ? "ok" : "fail");
        return ok;
    }

    e1 = NgInjectSetRaw(x, y, z, o);
    ack = NgInjectWorldportAck();
    RefreshLastMapFromPlayer();
    here = g_last_map;

    if (map_id != kMapIdUnknown && here == map_id) {
        int ok = NgEeLight(x, y, z, o, flags, lock_ms);
        NgSetDetail("ng-cross: map became %u after E1=%d ACK=%d — EE light %s",
            map_id, e1, ack, ok ? "ok" : "fail");
        return ok;
    }

    NgSetDetail(
        "ng-cross: blocked map here=%u want=%u (E1=%d ACK=%d) — no GM worldport; "
        "stay put (use hearth/portal or be on dest continent)",
        here, map_id, e1, ack);
    return 0;
}

int ProxyTeleportNg(float x, float y, float z, float o, uint32_t map_id,
                    uint32_t mode, uint32_t flags, uint32_t lock_ms)
{
    uint32_t pin_ms;
    uint32_t here;
    int ok = 0;

    if (!TpFinite(x) || !TpFinite(y) || !TpFinite(z)) {
        NgSetDetail("ng: bad xyz");
        InterlockedExchange(&g_ng_last_ok, 0);
        return 0;
    }
    if (!TpFinite(o) || fabsf(o) > 100.f || (o != 0.f && fabsf(o) < 1.0e-20f))
        o = 0.f;
    if (mode > kNgModeMax)
        mode = kNgExplore;

    pin_ms = (lock_ms > 0u) ? lock_ms : ProxyDefaultTpLockMs();
    RefreshLastMapFromPlayer();
    here = g_last_map;
    InterlockedExchange(&g_ng_last_mode, (LONG)mode);

    if (mode == kNgExplore) {
        if (map_id != kMapIdUnknown && here != kMapIdUnknown && map_id != here)
            mode = kNgCrossTry;
        else
            mode = kNgExploreLight;
    }

    switch (mode) {
    case kNgExploreLight:
        ok = NgEeLight(x, y, z, o, flags, pin_ms);
        NgSetDetail("ng-light: %s map=%u", ok ? "ok" : "fail", here);
        break;
    case kNgEeNoJump:
        ok = NgEe(x, y, z, o, flags | kTpSkipJump | kTpSkipOmInvalidate, pin_ms);
        NgSetDetail("ng-ee-nojump: %s map=%u→%u", ok ? "ok" : "fail", here, map_id);
        break;
    case kNgEeE1:
        NgInjectSetRaw(x, y, z, o);
        ok = NgEe(x, y, z, o, flags | kTpSkipOmInvalidate, pin_ms);
        NgSetDetail("ng-e1+ee: %s", ok ? "ok" : "fail");
        break;
    case kNgEeBurst:
        ok = NgBurst(x, y, z, o, flags, pin_ms);
        NgSetDetail("ng-burst: %s map=%u", ok ? "ok" : "fail", here);
        break;
    case kNgClientOnly:
        ok = NgClientPin(x, y, z, o, pin_ms);
        NgSetDetail("ng-client: om+lock");
        break;
    case kNgEeAck:
        ok = NgEe(x, y, z, o, flags | kTpSkipOmInvalidate, pin_ms);
        NgInjectWorldportAck();
        NgSetDetail("ng-ee+ack: %s", ok ? "ok" : "fail");
        break;
    case kNgCrossTry:
        ok = NgCrossTry(x, y, z, o, map_id, flags, pin_ms);
        break;
    case kNgEe:
    default:
        ok = NgEe(x, y, z, o, flags | kTpSkipOmInvalidate, pin_ms);
        NgSetDetail("ng-ee: %s map=%u", ok ? "ok" : "fail", here);
        break;
    }

    if (ok && map_id != kMapIdUnknown)
        InterlockedExchange((volatile LONG*)&g_last_map, (LONG)map_id);

    InterlockedExchange(&g_ng_last_ok, ok ? 1 : 0);
    {
        static volatile LONG s_ng_log = 0;
        if ((InterlockedIncrement(&s_ng_log) % 16) == 1)
            LogLine(g_ng_detail);
    }
    return ok;
}

static int __cdecl GmTeleportNg_Lua(void* L)
{
    float x = (float)LuaArgNum(L, 1);
    float y = (float)LuaArgNum(L, 2);
    float z = (float)LuaArgNum(L, 3);
    float o = (float)LuaArgNum(L, 4);
    double map_arg = LuaArgNum(L, 5);
    uint32_t mode = (uint32_t)LuaArgNum(L, 6);
    uint32_t flags = (uint32_t)LuaArgNum(L, 7);
    uint32_t lock_ms = (uint32_t)LuaArgNum(L, 8);
    uint32_t map_id = kMapIdUnknown;
    int ok;
    if (map_arg >= 0.0 && map_arg < 2147483647.0)
        map_id = (uint32_t)map_arg;
    ForceClearTaint();

    ok = ProxyTeleportNg(x, y, z, o, map_id, mode, flags, lock_ms);
    LuaPushNum(L, ok ? 1.0 : 0.0);
    LuaPushStr(L, g_ng_detail);
    return 2;
}

static int __cdecl GmTeleportNgExplore_Lua(void* L)
{
    float x = (float)LuaArgNum(L, 1);
    float y = (float)LuaArgNum(L, 2);
    float z = (float)LuaArgNum(L, 3);
    float o = (float)LuaArgNum(L, 4);
    double map_arg = LuaArgNum(L, 5);
    uint32_t flags = (uint32_t)LuaArgNum(L, 6);
    uint32_t lock_ms = (uint32_t)LuaArgNum(L, 7);
    uint32_t map_id = kMapIdUnknown;
    int ok;
    if (map_arg >= 0.0 && map_arg < 2147483647.0)
        map_id = (uint32_t)map_arg;
    ForceClearTaint();
    ok = ProxyTeleportNg(x, y, z, o, map_id, kNgExplore, flags, lock_ms);
    LuaPushNum(L, ok ? 1.0 : 0.0);
    LuaPushStr(L, g_ng_detail);
    return 2;
}

static int __cdecl GmTeleportNgEe_Lua(void* L)
{
    float x = (float)LuaArgNum(L, 1);
    float y = (float)LuaArgNum(L, 2);
    float z = (float)LuaArgNum(L, 3);
    float o = (float)LuaArgNum(L, 4);
    uint32_t flags = (uint32_t)LuaArgNum(L, 5);
    uint32_t lock_ms = (uint32_t)LuaArgNum(L, 6);
    int ok;
    ForceClearTaint();

    ok = ProxyTeleportNg(x, y, z, o, kMapIdUnknown, kNgExploreLight, flags, lock_ms);
    LuaPushNum(L, ok ? 1.0 : 0.0);
    LuaPushStr(L, g_ng_detail);
    return 2;
}

static int __cdecl GmTeleportNgCross_Lua(void* L)
{
    float x = (float)LuaArgNum(L, 1);
    float y = (float)LuaArgNum(L, 2);
    float z = (float)LuaArgNum(L, 3);
    float o = (float)LuaArgNum(L, 4);
    double map_arg = LuaArgNum(L, 5);
    uint32_t flags = (uint32_t)LuaArgNum(L, 6);
    uint32_t lock_ms = (uint32_t)LuaArgNum(L, 7);
    uint32_t map_id = kMapIdUnknown;
    int ok;
    if (map_arg >= 0.0 && map_arg < 2147483647.0)
        map_id = (uint32_t)map_arg;
    ForceClearTaint();
    ok = ProxyTeleportNg(x, y, z, o, map_id, kNgCrossTry, flags, lock_ms);
    LuaPushNum(L, ok ? 1.0 : 0.0);
    LuaPushStr(L, g_ng_detail);
    return 2;
}

static int __cdecl GmNgLastDetail_Lua(void* L)
{
    LuaPushStr(L, g_ng_detail);
    LuaPushNum(L, (double)InterlockedCompareExchange(&g_ng_last_ok, 0, 0));
    LuaPushNum(L, (double)InterlockedCompareExchange(&g_ng_last_mode, 0, 0));
    return 3;
}

static int __cdecl GmNgHelp_Lua(void* L)
{
    static const char* help =
        "GmTeleportNg(x,y,z[,o[,map[,mode[,flags[,lockMs]]]]]) -> ok, detail  NON-GM only\n"
        "GmTeleportNgExplore(...)  auto same=EE-light / cross=E1+ACK (no EE off-map)\n"
        "GmTeleportNgEe(...)       EE light (same-map tour path)\n"
        "GmTeleportNgCross(...)    cross-map experiment (no 0x08/0xE0)\n"
        "modes: 0=EE 1=EE-nojump 2=E1+EE 3=burst 4=client 5=EE+ACK 6=cross 7=explore 8=light\n"
        "NEVER uses WORLD_TELEPORT/CHARM_PORT. Existing GmTeleport* unchanged.";
    LuaPushStr(L, help);
    return 1;
}

static void RegisterTeleportNgApis(RegisterFunctionFn reg)
{
    reg("GmTeleportNg", (void*)GmTeleportNg_Lua);
    reg("GmTeleportNgExplore", (void*)GmTeleportNgExplore_Lua);
    reg("GmTeleportNgEe", (void*)GmTeleportNgEe_Lua);
    reg("GmTeleportNgCross", (void*)GmTeleportNgCross_Lua);
    reg("GmNgLastDetail", (void*)GmNgLastDetail_Lua);
    reg("GmNgHelp", (void*)GmNgHelp_Lua);
}
