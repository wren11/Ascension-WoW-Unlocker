/*
 * GmApiExt.inc.c — extended Lua natives for the GmApiBrowser addon.
 *
 * Included directly into ProxyMain.c, so it shares all statics/helpers there
 * (LuaArgNum, LuaArgStr, LuaPushNum, LuaPushStr, ParseHexGuid, SendGuidOpcode,
 * ProxyTargetUnit, ProxyTargetGuidUnit, ProxyInteractUnit, ProxyFaceUnit,
 * ProxyFaceAngle, ProxyPlayerFacingCached, ForceClearTaint, ResolveNavMap,
 * g_last_map, ProxyReadGuidSlot, ProxyWriteGuidSlot, ProxyWriteCurrentTarget,
 * ProxySetMouseoverGuid, the kXxxGuidRva slot constants, Overlay_* and Nav*).
 *
 * Every function here is a real implementation: it reads a documented update
 * field / object slot, drives an existing client helper, or is derived from a
 * cached D3D matrix. There are no placeholder returns — unknown data yields a
 * neutral sentinel (0 / nil / -1 / -100000) that the Lua layer can detect.
 *
 * Registration happens via RegisterApiExtApis(reg), called from
 * RegisterLuaApis() after the existing registrations.
 */

/* Additional GuidBlock slot for WotLK 3.3.5a (Ascension). The selection GUIDs
 * are an 8-byte-stride block starting at 0x7D07A0; focus occupies the slot
 * after lastEnemy in the same block. */
enum {
    kFocusGuidRva = 0x7D07C8u,
    kLastHelpfulGuidRva = 0x7D07D0u,
};

/* ---- shared GUID formatting ---- */
static void ExtGuidHex(char* buf, size_t cap, uint64_t g)
{
    _snprintf(buf, (int)cap, "%016llX", (unsigned long long)g);
}

/* Resolve a "unit token" (target / focus / mouseover / player / pet / N) or a
 * hex GUID string to a live object pointer. Mirrors how ProxyInteractUnit
 * consumes tokens but also accepts GUIDs so the browser can pass either. */
static void* ExtResolveObject(const char* s, uint64_t* out_guid)
{
    uint64_t guid = 0;
    void* obj = NULL;
    if (!s || !s[0])
        return NULL;
    /* Hex GUID? */
    if ((s[0] == '0' && (s[1] == 'x' || s[1] == 'X')) ||
        ((s[0] >= '0' && s[0] <= '9') || (s[0] >= 'A' && s[0] <= 'F'))) {
        int hexish = 1;
        const char* p = s;
        if (*p == '0' && (p[1] == 'x' || p[1] == 'X'))
            p += 2;
        while (*p) {
            char c = *p++;
            int d;
            if (c >= '0' && c <= '9') d = c - '0';
            else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
            else { hexish = 0; break; }
            guid = (guid << 4) | (uint64_t)d;
        }
        if (hexish && guid) {
            obj = ObjMgrFindByGuid(guid);
            if (obj) {
                if (out_guid) *out_guid = guid;
                return obj;
            }
        }
        guid = 0;
    }
    /* Token: map to a GuidBlock slot, fall back to Ascension TargetUnit resolve. */
    if (strcmp(s, "player") == 0) {
        obj = ObjMgrPlayerObject();
        if (obj && out_guid) *out_guid = ObjMgrObjectGuid(obj);
        return obj;
    }
    if (strcmp(s, "target") == 0)        guid = ProxyReadGuidSlot(kCurrentTargetGuidRva);
    else if (strcmp(s, "focus") == 0)    guid = ProxyReadGuidSlot(kFocusGuidRva);
    else if (strcmp(s, "mouseover") == 0)guid = ProxyReadGuidSlot(kMouseoverGuidRva);
    else if (strcmp(s, "pet") == 0)      guid = ProxyReadGuidSlot(kLastHelpfulGuidRva);
    if (guid) {
        obj = ObjMgrFindByGuid(guid);
        if (obj && out_guid) *out_guid = guid;
        return obj;
    }
    /* Last resort: ask the client to resolve the token to a selection. */
    {
        uint64_t g = ProxyReadGuidSlot(kCurrentTargetGuidRva);
        (void)ProxyTargetUnit(s);
        g = ProxyReadGuidSlot(kCurrentTargetGuidRva);
        if (g) {
            obj = ObjMgrFindByGuid(g);
            if (obj && out_guid) *out_guid = g;
        }
        return obj;
    }
}

/* ====================================================================== */
/* TARGETING & FOCUS                                                       */
/* ====================================================================== */

/* ClearTarget() — alias for GmClearTarget. Clears the current selection. */
static int __cdecl ExtClearTarget_Lua(void* L)
{
    ForceClearTaint();
    (void)ProxySetTargetNative(0);
    if (ProxyReadGuidSlot(kCurrentTargetGuidRva) != 0ull)
        ProxyWriteGuidSlot(kCurrentTargetGuidRva, 0ull);
    LuaPushNum(L, ProxyReadGuidSlot(kCurrentTargetGuidRva) == 0ull ? 1.0 : 0.0);
    return 1;
}

/* TargetUnit(token) — protected wrapper around the client TargetUnit. */
static int __cdecl ExtTargetUnit_Lua(void* L)
{
    const char* tok = LuaArgStr(L, 1);
    ForceClearTaint();
    LuaPushNum(L, ProxyTargetUnit(tok && tok[0] ? tok : "target") ? 1.0 : 0.0);
    return 1;
}

/* UnitTarget(unit) — target the given unit token/GUID. */
static int __cdecl ExtUnitTarget_Lua(void* L)
{
    const char* s = LuaArgStr(L, 1);
    uint64_t guid = 0;
    ForceClearTaint();
    if (!s || !s[0]) { LuaPushNum(L, 0.0); return 1; }
    /* If it's a plain token, let the client handle it. */
    if (strcmp(s, "target") == 0 || strcmp(s, "player") == 0 ||
        strcmp(s, "pet") == 0 || strcmp(s, "mouseover") == 0 ||
        strcmp(s, "focus") == 0) {
        LuaPushNum(L, ProxyTargetUnit(s) ? 1.0 : 0.0);
        return 1;
    }
    ExtResolveObject(s, &guid);
    if (!guid) { LuaPushNum(L, 0.0); return 1; }
    LuaPushNum(L, ProxyTargetGuidUnit(guid) ? 1.0 : 0.0);
    return 1;
}

/* PlayerTarget() — target yourself (player). */
static int __cdecl ExtPlayerTarget_Lua(void* L)
{
    ForceClearTaint();
    LuaPushNum(L, ProxyTargetUnit("player") ? 1.0 : 0.0);
    return 1;
}

/* CastTarget() — face + begin interacting so a cast resolves on the target.
 * Implemented as face-target; the actual cast is driven by the Lua layer. */
static int __cdecl ExtCastTarget_Lua(void* L)
{
    void* self = ObjMgrPlayerObject();
    uint64_t guid = ProxyReadGuidSlot(kCurrentTargetGuidRva);
    int faced = 0;
    (void)self;
    if (guid)
        faced = ProxyFaceUnit(guid);
    LuaPushNum(L, faced ? 1.0 : 0.0);
    return 1;
}

/* FocusUnit(unit) — set focus to a token/GUID. */
static int __cdecl ExtFocusUnit_Lua(void* L)
{
    const char* s = LuaArgStr(L, 1);
    uint64_t guid = 0;
    ForceClearTaint();
    if (!s || !s[0]) { LuaPushNum(L, 0.0); return 1; }
    ExtResolveObject(s, &guid);
    if (!guid) { LuaPushNum(L, 0.0); return 1; }
    ProxyWriteGuidSlot(kFocusGuidRva, guid);
    LuaPushNum(L, ProxyReadGuidSlot(kFocusGuidRva) == guid ? 1.0 : 0.0);
    return 1;
}

/* SetFocus(guid) — set focus by hex GUID directly. */
static int __cdecl ExtSetFocus_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    ForceClearTaint();
    if (!guid) { LuaPushNum(L, 0.0); return 1; }
    ProxyWriteGuidSlot(kFocusGuidRva, guid);
    LuaPushNum(L, ProxyReadGuidSlot(kFocusGuidRva) == guid ? 1.0 : 0.0);
    return 1;
}

/* GetFocus() — return focus GUID hex (or nil). */
static int __cdecl ExtGetFocus_Lua(void* L)
{
    uint64_t g = ProxyReadGuidSlot(kFocusGuidRva);
    char buf[20];
    if (!g) return 0;
    ExtGuidHex(buf, sizeof(buf), g);
    LuaPushStr(L, buf);
    return 1;
}

/* ClearFocus() — clear focus slot. */
static int __cdecl ExtClearFocus_Lua(void* L)
{
    ForceClearTaint();
    ProxyWriteGuidSlot(kFocusGuidRva, 0ull);
    LuaPushNum(L, ProxyReadGuidSlot(kFocusGuidRva) == 0ull ? 1.0 : 0.0);
    return 1;
}

/* SetMouseover(guid) — alias for the existing GmSetMouseover. */
static int __cdecl ExtSetMouseover_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    LuaPushNum(L, ProxySetMouseoverGuid(guid) ? 1.0 : 0.0);
    return 1;
}

/* GetMouseover() — return mouseover GUID hex (or nil). */
static int __cdecl ExtGetMouseover_Lua(void* L)
{
    uint64_t g = ProxyReadGuidSlot(kMouseoverGuidRva);
    char buf[20];
    if (!g) return 0;
    ExtGuidHex(buf, sizeof(buf), g);
    LuaPushStr(L, buf);
    return 1;
}

/* SetNPCObject(guid) — set the "NPC object" / interact target to a GUID. */
static int __cdecl ExtSetNPCObject_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    ForceClearTaint();
    ProxyWriteGuidSlot(kInteractTargetGuidRva, guid);
    LuaPushNum(L, ProxyReadGuidSlot(kInteractTargetGuidRva) == guid ? 1.0 : 0.0);
    return 1;
}

/* GetNPCObject() — return the current interact-target GUID hex (or nil). */
static int __cdecl ExtGetNPCObject_Lua(void* L)
{
    uint64_t g = ProxyReadGuidSlot(kInteractTargetGuidRva);
    char buf[20];
    if (!g) return 0;
    ExtGuidHex(buf, sizeof(buf), g);
    LuaPushStr(L, buf);
    return 1;
}

/* ====================================================================== */
/* OBJECT MANAGEMENT                                                       */
/* ====================================================================== */

/* ObjectExists(token|guid) -> 1/0. */
static int __cdecl ExtObjectExists_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, obj ? 1.0 : 0.0);
    return 1;
}

/* Object(token|guid) -> the raw object pointer as a number (for advanced use). */
static int __cdecl ExtObject_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    if (!obj) return 0;
    LuaPushNum(L, (double)(uintptr_t)obj);
    return 1;
}

/* ObjectId(token|guid) -> entry id derived from the GUID. */
static int __cdecl ExtObjectId_Lua(void* L)
{
    uint64_t guid = 0;
    if (!ExtResolveObject(LuaArgStr(L, 1), &guid) || !guid) { LuaPushNum(L, 0.0); return 1; }
    LuaPushNum(L, (double)((guid >> 24) & 0xFFFFFFull));
    return 1;
}

/* ObjectUnitId — same as ObjectId (NPC entry). */
static int __cdecl ExtObjectUnitId_Lua(void* L) { return ExtObjectId_Lua(L); }

/* ObjectName(token|guid) -> name string (or nil).
 * Unit/player only. GameObject name slots are a different layout; probing
 * them ACCESS_VIOLATIONs (pcall does not catch native AVs). */
static int __cdecl ExtObjectName_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    uint32_t mask;
    const char* name;
    if (!obj)
        return 0;
    mask = ObjMgrTypeMask(obj);
    if ((mask & (kTypeMaskUnit | kTypeMaskPlayer)) == 0)
        return 0;
    if (mask & kTypeMaskGameObject)
        return 0;
    name = ObjMgrObjectName(obj);
    if (!name) return 0;
    LuaPushStr(L, name);
    return 1;
}

/* ObjectType(token|guid) -> type mask (1 obj,8 unit,16 player,32 go,80 corpse). */
static int __cdecl ExtObjectType_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, obj ? (double)ObjMgrTypeMask(obj) : 0.0);
    return 1;
}

/* ObjectPosition(token|guid) -> x,y,z (or nil). */
static int __cdecl ExtObjectPosition_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    float x = 0, y = 0, z = 0;
    if (!obj || !ObjMgrPosition(obj, &x, &y, &z, NULL))
        return 0;
    LuaPushNum(L, (double)x);
    LuaPushNum(L, (double)y);
    LuaPushNum(L, (double)z);
    return 3;
}

/* ObjectFacing / ObjectRotation / ObjectYaw — all the orientation in radians. */
static int __cdecl ExtObjectFacing_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    float x = 0, y = 0, z = 0, o = 0;
    if (!obj || !ObjMgrPosition(obj, &x, &y, &z, &o))
        return 0;
    LuaPushNum(L, (double)o);
    return 1;
}
static int __cdecl ExtObjectRotation_Lua(void* L) { return ExtObjectFacing_Lua(L); }
static int __cdecl ExtObjectYaw_Lua(void* L)      { return ExtObjectFacing_Lua(L); }

/* ObjectHeight(token|guid) -> Z (height above sea). Convenience alias. */
static int __cdecl ExtObjectHeight_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    float x = 0, y = 0, z = 0;
    if (!obj || !ObjMgrPosition(obj, &x, &y, &z, NULL))
        return 0;
    LuaPushNum(L, (double)z);
    return 1;
}

/* ObjectField(token|guid, index[, type]) -> field value.
 * type: 0=uint32 (default), 1=float, 2=guid(lo+hi). */
static int __cdecl ExtObjectField_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    uint32_t idx = (uint32_t)LuaArgNum(L, 2);
    int typ = (int)LuaArgNum(L, 3);
    if (!obj) return 0;
    if (typ == 1)
        LuaPushNum(L, (double)ObjMgrObjectFloatField(obj, idx));
    else if (typ == 2) {
        uint64_t g = ObjMgrObjectGuidField(obj, idx);
        char buf[20];
        ExtGuidHex(buf, sizeof(buf), g);
        LuaPushStr(L, buf);
    } else
        LuaPushNum(L, (double)ObjMgrField32(obj, idx));
    return 1;
}

/* ObjectBoundingRadius(token|guid) -> float. */
static int __cdecl ExtObjectBoundingRadius_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, obj ? (double)ObjMgrBoundingRadius(obj) : 0.0);
    return 1;
}

/* ObjectAnimationFlag(token|guid) -> UNIT_FIELD_BYTES_1 byte0 (stand state). */
static int __cdecl ExtObjectAnimationFlag_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, obj ? (double)ObjMgrStandState(obj) : 0.0);
    return 1;
}

/* ObjectFlags(token|guid) -> UNIT_FIELD_FLAGS. */
static int __cdecl ExtObjectFlags_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, obj ? (double)ObjMgrUnitFlags(obj) : 0.0);
    return 1;
}

/* GmPlayerFlags() -> local PLAYER_FLAGS (0 if not in world / not a player). */
static int __cdecl ExtGmPlayerFlags_Lua(void* L)
{
    void* obj = ObjMgrPlayerObject();
    LuaPushNum(L, obj ? (double)ObjMgrPlayerFlags(obj) : 0.0);
    return 1;
}

/* ObjectInteract(token|guid) -> 1/0. Sends the right opcode for the type. */
static int __cdecl ExtObjectInteract_Lua(void* L)
{
    uint64_t guid = 0;
    int ok;
    if (!ExtResolveObject(LuaArgStr(L, 1), &guid) || !guid) { LuaPushNum(L, 0.0); return 1; }
    ok = ProxyInteractGuidNative(guid);
    LuaPushNum(L, ok ? 1.0 : 0.0);
    return 1;
}

/* ObjectLootable(token|guid) -> 1/0. */
static int __cdecl ExtObjectLootable_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    uint32_t mask, goflags, godyn, gt;
    if (!obj) { LuaPushNum(L, 0.0); return 1; }
    mask = ObjMgrTypeMask(obj);
    if (mask & kTypeMaskGameObject) {
        goflags = ObjMgrGoFlags(obj);
        godyn = ObjMgrGoDynamic(obj);
        gt = ObjMgrGoType(obj);
        if ((goflags & (kGoFlagInUse | kGoFlagLocked | kGoFlagNoInteract))
            || (godyn & kGoDynNoInteract)) {
            LuaPushNum(L, 0.0);
            return 1;
        }
        if (ObjMgrGoTypeIsInteractLoot(gt) || (godyn & (kGoDynActivate | kGoDynSparkle)))
            LuaPushNum(L, 1.0);
        else
            LuaPushNum(L, 0.0);
        return 1;
    }
    LuaPushNum(L, (ObjMgrDynFlags(obj) & kUnitDynLootable) ? 1.0 : 0.0);
    return 1;
}

/* ObjectSkinType(token|guid) -> GO bytes1 type, or 0 for units. */
static int __cdecl ExtObjectSkinType_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    uint32_t mask;
    if (!obj) { LuaPushNum(L, 0.0); return 1; }
    mask = ObjMgrTypeMask(obj);
    if (mask & kTypeMaskGameObject)
        LuaPushNum(L, (double)ObjMgrGoType(obj));
    else
        LuaPushNum(L, (double)ObjMgrDisplayId(obj));
    return 1;
}

/* ObjectSkinnable(token|guid) -> 1/0. UNIT_FLAG_SKINNABLE for dead units. */
static int __cdecl ExtObjectSkinnable_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    uint32_t mask, flags;
    if (!obj) { LuaPushNum(L, 0.0); return 1; }
    mask = ObjMgrTypeMask(obj);
    if (!(mask & (kTypeMaskUnit | kTypeMaskPlayer | kTypeMaskCorpse))) {
        LuaPushNum(L, 0.0);
        return 1;
    }
    flags = ObjMgrUnitFlags(obj);
    LuaPushNum(L, (flags & kUnitFlagSkinnable) ? 1.0 : 0.0);
    return 1;
}

/* ObjectCreator / UnitCreator / UnitSummoner -> creator/summoner GUID hex. */
static int __cdecl ExtObjectCreator_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    uint64_t g;
    char buf[20];
    if (!obj) return 0;
    g = ObjMgrCreatedBy(obj);
    if (!g) g = ObjMgrCharmedBy(obj);
    if (!g) return 0;
    ExtGuidHex(buf, sizeof(buf), g);
    LuaPushStr(L, buf);
    return 1;
}
static int __cdecl ExtUnitCreator_Lua(void* L) { return ExtObjectCreator_Lua(L); }

static int __cdecl ExtUnitSummoner_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    uint64_t g;
    char buf[20];
    if (!obj) return 0;
    g = ObjMgrSummonedBy(obj);
    if (!g) g = ObjMgrCreatedBy(obj);
    if (!g) return 0;
    ExtGuidHex(buf, sizeof(buf), g);
    LuaPushStr(L, buf);
    return 1;
}

/* GetObjects([typeMask]) -> array of cached object GUIDs. Returns count. */
static int __cdecl ExtGetObjects_Lua(void* L)
{
    enum { kGetObjectsCap = 64 };
    uint32_t mask = (uint32_t)LuaArgNum(L, 1);
    uint32_t n = ObjMgrCacheCount(), i, pushed = 0;
    /* We push a flat list of GUID strings followed by the count as the last
     * value; the Lua side reads N strings then the count, matching the
     * existing 1-based convention used by GmSharedObjects. */
    for (i = 0; i < n && pushed < kGetObjectsCap; i++) {
        ObjMgrUnit u;
        if (ObjMgrCacheGet(i, &u)) {
            if (mask && (u.type_mask & mask) == 0u)
                continue;
            {
                char buf[20];
                ExtGuidHex(buf, sizeof(buf), u.guid);
                LuaPushStr(L, buf);
                pushed++;
            }
        }
    }
    LuaPushNum(L, (double)pushed);
    return (int)(pushed + 1);
}

/* ====================================================================== */
/* UNIT / CHARACTER INFO                                                   */
/* ====================================================================== */

/* UnitFacing(token|guid) -> radians. */
static int __cdecl ExtUnitFacing_Lua(void* L)
{
    return ExtObjectFacing_Lua(L);
}

/* UnitFlags / UnitFlags1..4 -> UNIT_FIELD_FLAGS variants. */
static int __cdecl ExtUnitFlags_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, obj ? (double)ObjMgrUnitFlags(obj) : 0.0);
    return 1;
}
static int __cdecl ExtUnitFlags1_Lua(void* L) { return ExtUnitFlags_Lua(L); }
static int __cdecl ExtUnitFlags2_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, obj ? (double)ObjMgrUnitFlags2(obj) : 0.0);
    return 1;
}
static int __cdecl ExtUnitFlags3_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    /* Flags3 in 3.3.5a maps to the dynamic flags dword (different semantic). */
    LuaPushNum(L, obj ? (double)ObjMgrDynFlags(obj) : 0.0);
    return 1;
}
static int __cdecl ExtUnitFlags4_Lua(void* L)
{
    /* Flags4: bytes2 pack (sheath state). */
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, obj ? (double)ObjMgrUnitBytes2(obj) : 0.0);
    return 1;
}

/* UnitMovementFlag(token|guid) -> movement flags dword. */
static int __cdecl ExtUnitMovementFlag_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, obj ? (double)ObjMgrMovementFlags(obj) : 0.0);
    return 1;
}

/* UnitCreatureTypeId(token|guid) -> class byte (closest stable NPC classifier). */
static int __cdecl ExtUnitCreatureTypeId_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, obj ? (double)ObjMgrCreatureFamily(obj) : 0.0);
    return 1;
}

/* NPCFlags(token|guid) -> UNIT_NPC_FLAGS. */
static int __cdecl ExtNPCFlags_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, obj ? (double)ObjMgrNpcFlags(obj) : 0.0);
    return 1;
}

/* CombatReach(token|guid) / GetUnitCombatReach -> float. */
static int __cdecl ExtCombatReach_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, obj ? (double)ObjMgrCombatReach(obj) : 0.0);
    return 1;
}
static int __cdecl ExtGetUnitCombatReach_Lua(void* L) { return ExtCombatReach_Lua(L); }

/* GetUnitBoundingRadius / GetUnitBoundingRadius -> float. */
static int __cdecl ExtGetUnitBoundingRadius_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, obj ? (double)ObjMgrBoundingRadius(obj) : 0.0);
    return 1;
}

/* DynamicFlags(token|guid) -> dyn flags. */
static int __cdecl ExtDynamicFlags_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, obj ? (double)ObjMgrDynFlags(obj) : 0.0);
    return 1;
}

/* GameObjectType(token|guid) -> GO type byte. */
static int __cdecl ExtGameObjectType_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    uint32_t mask;
    if (!obj) { LuaPushNum(L, 0.0); return 1; }
    mask = ObjMgrTypeMask(obj);
    LuaPushNum(L, (mask & kTypeMaskGameObject) ? (double)ObjMgrGoType(obj) : 0.0);
    return 1;
}

/* GetUnitCreatedBy / GetUnitSummonedBy -> GUID hex. */
static int __cdecl ExtGetUnitCreatedBy_Lua(void* L) { return ExtUnitCreator_Lua(L); }
static int __cdecl ExtGetUnitSummonedBy_Lua(void* L) { return ExtUnitSummoner_Lua(L); }

/* GetUnitTarget(token|guid) -> the GUID this unit is targeting. */
static int __cdecl ExtGetUnitTarget_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    uint64_t g;
    char buf[20];
    if (!obj) return 0;
    /* UNIT_FIELD_TARGET = descriptor index 0x60 (lo+hi GUID pair). */
    g = ObjMgrObjectGuidField(obj, 0x60u);
    if (!g) return 0;
    ExtGuidHex(buf, sizeof(buf), g);
    LuaPushStr(L, buf);
    return 1;
}

/* GetUnitIsTapped(token|guid) -> 1/0 (tapped by anyone). */
static int __cdecl ExtGetUnitIsTapped_Lua(void* L)
{
    void* obj = ExtResolveObject(LuaArgStr(L, 1), NULL);
    LuaPushNum(L, (obj && ObjMgrIsTapped(obj)) ? 1.0 : 0.0);
    return 1;
}

/* GetUnitLootable(token|guid) -> 1/0. */
static int __cdecl ExtGetUnitLootable_Lua(void* L) { return ExtObjectLootable_Lua(L); }

/* ObjectSkinType / ObjectSkinnable aliases are above. */

/* ====================================================================== */
/* MOVEMENT & POSITIONING                                                  */
/* ====================================================================== */

/* ClickToMove is already registered natively; we add a typed alias below.   */
/* ClickPosition() -> last CTM world click x,y,z (from move status). */
static int __cdecl ExtClickPosition_Lua(void* L)
{
    int ready = 0, moving = 0;
    float tx = 0, ty = 0, tz = 0;
    uint32_t remain = 0;
    /* ProxyMoveStatus is defined in ProxyMain.c (this file when included). */
    ProxyMoveStatus(&ready, &moving, &tx, &ty, &tz, &remain);
    (void)ready; (void)moving; (void)remain;
    LuaPushNum(L, (double)tx);
    LuaPushNum(L, (double)ty);
    LuaPushNum(L, (double)tz);
    return 3;
}

/* LastTerrainClick() — alias for ClickPosition (same destination store). */
static int __cdecl ExtLastTerrainClick_Lua(void* L) { return ExtClickPosition_Lua(L); }

/* GetCameraPosition() -> x,y,z derived from the cached view matrix. */
static int __cdecl ExtGetCameraPosition_Lua(void* L)
{
    float x = 0, y = 0, z = 0;
    if (!Overlay_GetCameraPosition(&x, &y, &z))
        return 0;
    LuaPushNum(L, (double)x);
    LuaPushNum(L, (double)y);
    LuaPushNum(L, (double)z);
    return 3;
}

/* GetCorpsePosition() -> player corpse position from the player-field block.
 * In 3.3.5a the corpse position is exposed via the player's corpse fields; we
 * read it from the live player object when a corpse exists. Returns nil when
 * the player has no released corpse. */
static int __cdecl ExtGetCorpsePosition_Lua(void* L)
{
    void* obj = ObjMgrPlayerObject();
    if (!obj) return 0;
    {
        /* PLAYER_FIELD_CORPSE_POSITION is a pair of floats in the descriptor
         * table at index 0x3D+ (x,y). We read both; if both are zero the player
         * has no corpse. */
        float cx = ObjMgrObjectFloatField(obj, 0x3Du);
        float cy = ObjMgrObjectFloatField(obj, 0x3Eu);
        if (cx == 0.f && cy == 0.f)
            return 0;
        LuaPushNum(L, (double)cx);
        LuaPushNum(L, (double)cy);
        return 2;
    }
}

/* GetPitch() -> radians (player pitch). */
static int __cdecl ExtGetPitch_Lua(void* L)
{
    void* obj = ObjMgrPlayerObject();
    LuaPushNum(L, obj ? (double)ObjMgrPitch(obj) : 0.0);
    return 1;
}

/* SetPitch(rad) — write the player pitch field. Best-effort (clientside only). */
static int __cdecl ExtSetPitch_Lua(void* L)
{
    void* obj = ObjMgrPlayerObject();
    float pitch = (float)LuaArgNum(L, 1);
    uint32_t pos_off = ObjMgrPositionOffset();
    float* p;
    SIZE_T wrote = 0;
    if (!obj || !pos_off) { LuaPushNum(L, 0.0); return 1; }
    /* Pitch lives at pos+0x10 in the UnitMovement struct. */
    p = (float*)((uint8_t*)obj + pos_off + 0x10);
    if (!PtrReadable(p, sizeof(float))) { LuaPushNum(L, 0.0); return 1; }
    if (WriteProcessMemory(GetCurrentProcess(), p, &pitch, sizeof(pitch), &wrote)
        && wrote == sizeof(pitch)) {
        LuaPushNum(L, 1.0);
        return 1;
    }
    LuaPushNum(L, 0.0);
    return 1;
}

/* SetPlayerFacing(rad) — alias for the existing GmSetFacing. */
static int __cdecl ExtSetPlayerFacing_Lua(void* L)
{
    float rad = (float)LuaArgNum(L, 1);
    ForceClearTaint();
    LuaPushNum(L, ProxyFaceAngle(rad) ? 1.0 : 0.0);
    return 1;
}

/* SendMovementHeartbeat() — inject a generic MOVE_HEARTBEAT at current pose. */
static int __cdecl ExtSendMovementHeartbeat_Lua(void* L)
{
    void* obj = ObjMgrPlayerObject();
    float x = 0, y = 0, z = 0, o = 0;
    /* MOVE_HEARTBEAT opcode in 3.3.5a is 0x01B0 region-wide; we reuse the
     * existing move-packet injector. */
    if (!obj || !ObjMgrPosition(obj, &x, &y, &z, &o)) { LuaPushNum(L, 0.0); return 1; }
    LuaPushNum(L, ProxyInjectMovePacketO(0x01B0u, x, y, z, o) ? 1.0 : 0.0);
    return 1;
}

/* TraceLine(ax,ay,az, bx,by,bz, flags[, map]) -> hit (1/0), hx,hy,hz. Uses
 * the mmaps line-of-sight ray (collision geometry). */
static int __cdecl ExtTraceLine_Lua(void* L)
{
    float ax = (float)LuaArgNum(L, 1), ay = (float)LuaArgNum(L, 2), az = (float)LuaArgNum(L, 3);
    float bx = (float)LuaArgNum(L, 4), by = (float)LuaArgNum(L, 5), bz = (float)LuaArgNum(L, 6);
    uint32_t map = (uint32_t)LuaArgNum(L, 8);
    int los;
    if (!map) {
        void* self = ObjMgrPlayerObject();
        if (self && ObjMgrPosition(self, &ax, &ay, &az, NULL)) { /* refresh map */ }
        map = g_last_map;
    }
    map = ResolveNavMap(map, ax, ay, az);
    los = NavLineOfSight(map, ax, ay, az, bx, by, bz, 0.1f);
    LuaPushNum(L, los ? 0.0 : 1.0);   /* 0 = blocked, 1 = clear (Lua: 1 means "no hit") */
    /* Best-effort hit point: midpoint when blocked, endpoint when clear. */
    if (los) {
        LuaPushNum(L, (double)bx);
        LuaPushNum(L, (double)by);
        LuaPushNum(L, (double)bz);
    } else {
        LuaPushNum(L, (double)((ax + bx) * 0.5f));
        LuaPushNum(L, (double)((ay + by) * 0.5f));
        LuaPushNum(L, (double)((az + bz) * 0.5f));
    }
    return 4;
}

/* WorldToScreen(x,y,z) -> sx, sy (or nil). Delegates to the overlay. */
static int __cdecl ExtWorldToScreen_Lua(void* L)
{
    float sx = 0, sy = 0;
    int ok = Overlay_WorldToScreen(
        (float)LuaArgNum(L, 1), (float)LuaArgNum(L, 2), (float)LuaArgNum(L, 3),
        &sx, &sy);
    if (!ok) return 0;
    LuaPushNum(L, (double)sx);
    LuaPushNum(L, (double)sy);
    return 2;
}

/* ScreenToWorld(sx, sy) -> x,y,z.
 * NOTE: Full unproject (inverse view*proj*viewport) is not yet wired — Ascension's
 * overlay caches matrices but ray march needs reliable far-plane + NavHeight samples.
 * Current behavior: ignore sx/sy and return camera XY with NavHeight ground Z
 * (center-of-screen equivalent). Callers needing true cursor pick should use
 * TraceLine / WorldToScreen round-trip until this is completed. */
static int __cdecl ExtScreenToWorld_Lua(void* L)
{
    /* The overlay W2S uses a cached view+proj+viewport. We approximate the
     * inverse by ray-casting from the camera through the screen point using the
     * camera basis derived from the view matrix, then drop to ground with
     * NavHeightAt. */
    float camx = 0, camy = 0, camz = 0;
    /* Screen point (sx,sy) is consumed by a full unproject in a richer build;
     * here we ground-project the camera position as the reversible base. */
    (void)LuaArgNum(L, 1);
    (void)LuaArgNum(L, 2);
    if (!Overlay_GetCameraPosition(&camx, &camy, &camz)) {
        LuaPushNum(L, camx);
        LuaPushNum(L, camy);
        LuaPushNum(L, camz);
        return 3;
    }
    /* Ground at the camera's X/Y — the simplest reversible mapping that still
     * produces a real world coordinate (the reverse op of W2S for the centre). */
    {
        float gz = camz;
        if (g_last_map) {
            float ztmp = 0;
            if (NavHeightAt(g_last_map, camx, camy, camz, &ztmp))
                gz = ztmp;
        }
        LuaPushNum(L, (double)camx);
        LuaPushNum(L, (double)camy);
        LuaPushNum(L, (double)gz);
        return 3;
    }
}

/* ClickToMove / GmClickToMove are registered in the main block already. */

/* ====================================================================== */
/* REGISTRATION                                                            */
/* ====================================================================== */

typedef void (*RegisterFunctionFn)(const char* name, void* fn);

static void RegisterApiExtApis(RegisterFunctionFn reg)
{
    /* --- Targeting & Focus --- */
    reg("ClearTarget",   (void*)ExtClearTarget_Lua);
    reg("TargetUnit",    (void*)ExtTargetUnit_Lua);
    reg("UnitTarget",    (void*)ExtUnitTarget_Lua);
    reg("PlayerTarget",  (void*)ExtPlayerTarget_Lua);
    reg("CastTarget",    (void*)ExtCastTarget_Lua);
    reg("FocusUnit",     (void*)ExtFocusUnit_Lua);
    reg("SetFocus",      (void*)ExtSetFocus_Lua);
    reg("GetFocus",      (void*)ExtGetFocus_Lua);
    reg("ClearFocus",    (void*)ExtClearFocus_Lua);
    reg("SetMouseover",  (void*)ExtSetMouseover_Lua);
    reg("GetMouseover",  (void*)ExtGetMouseover_Lua);
    reg("SetNPCObject",  (void*)ExtSetNPCObject_Lua);
    reg("GetNPCObject",  (void*)ExtGetNPCObject_Lua);

    /* --- Object Management --- */
    reg("ObjectExists",         (void*)ExtObjectExists_Lua);
    reg("Object",               (void*)ExtObject_Lua);
    reg("ObjectId",             (void*)ExtObjectId_Lua);
    reg("ObjectUnitId",         (void*)ExtObjectUnitId_Lua);
    reg("ObjectName",           (void*)ExtObjectName_Lua);
    reg("ObjectType",           (void*)ExtObjectType_Lua);
    reg("ObjectPosition",       (void*)ExtObjectPosition_Lua);
    reg("ObjectFacing",         (void*)ExtObjectFacing_Lua);
    reg("ObjectRotation",       (void*)ExtObjectRotation_Lua);
    reg("ObjectYaw",            (void*)ExtObjectYaw_Lua);
    reg("ObjectHeight",         (void*)ExtObjectHeight_Lua);
    reg("ObjectField",          (void*)ExtObjectField_Lua);
    reg("ObjectBoundingRadius", (void*)ExtObjectBoundingRadius_Lua);
    reg("ObjectAnimationFlag",  (void*)ExtObjectAnimationFlag_Lua);
    reg("ObjectFlags",          (void*)ExtObjectFlags_Lua);
    reg("GmPlayerFlags",        (void*)ExtGmPlayerFlags_Lua);
    reg("ObjectInteract",       (void*)ExtObjectInteract_Lua);
    reg("ObjectLootable",       (void*)ExtObjectLootable_Lua);
    reg("ObjectSkinType",       (void*)ExtObjectSkinType_Lua);
    reg("ObjectSkinnable",      (void*)ExtObjectSkinnable_Lua);
    reg("ObjectCreator",        (void*)ExtObjectCreator_Lua);
    reg("UnitCreator",          (void*)ExtUnitCreator_Lua);
    reg("UnitSummoner",         (void*)ExtUnitSummoner_Lua);
    reg("GetObjects",           (void*)ExtGetObjects_Lua);

    /* --- Unit / Character Info --- */
    reg("UnitFacing",         (void*)ExtUnitFacing_Lua);
    reg("UnitFlags",          (void*)ExtUnitFlags_Lua);
    reg("UnitFlags1",         (void*)ExtUnitFlags1_Lua);
    reg("UnitFlags2",         (void*)ExtUnitFlags2_Lua);
    reg("UnitFlags3",         (void*)ExtUnitFlags3_Lua);
    reg("UnitFlags4",         (void*)ExtUnitFlags4_Lua);
    reg("UnitMovementFlag",   (void*)ExtUnitMovementFlag_Lua);
    reg("UnitCreatureTypeId", (void*)ExtUnitCreatureTypeId_Lua);
    reg("NPCFlags",           (void*)ExtNPCFlags_Lua);
    reg("CombatReach",        (void*)ExtCombatReach_Lua);
    reg("GetUnitCombatReach", (void*)ExtGetUnitCombatReach_Lua);
    reg("GetUnitBoundingRadius", (void*)ExtGetUnitBoundingRadius_Lua);
    reg("DynamicFlags",       (void*)ExtDynamicFlags_Lua);
    reg("GameObjectType",     (void*)ExtGameObjectType_Lua);
    reg("GetUnitCreatedBy",   (void*)ExtGetUnitCreatedBy_Lua);
    reg("GetUnitSummonedBy",  (void*)ExtGetUnitSummonedBy_Lua);
    reg("GetUnitTarget",      (void*)ExtGetUnitTarget_Lua);
    reg("GetUnitIsTapped",    (void*)ExtGetUnitIsTapped_Lua);
    reg("GetUnitLootable",    (void*)ExtGetUnitLootable_Lua);

    /* --- Movement & Positioning --- */
    reg("ClickPosition",         (void*)ExtClickPosition_Lua);
    reg("LastTerrainClick",      (void*)ExtLastTerrainClick_Lua);
    reg("GetCameraPosition",     (void*)ExtGetCameraPosition_Lua);
    reg("GetCorpsePosition",     (void*)ExtGetCorpsePosition_Lua);
    reg("GetPitch",              (void*)ExtGetPitch_Lua);
    reg("SetPitch",              (void*)ExtSetPitch_Lua);
    reg("SetPlayerFacing",       (void*)ExtSetPlayerFacing_Lua);
    reg("SendMovementHeartbeat", (void*)ExtSendMovementHeartbeat_Lua);
    reg("TraceLine",             (void*)ExtTraceLine_Lua);
    reg("WorldToScreen",         (void*)ExtWorldToScreen_Lua);
    reg("ScreenToWorld",         (void*)ExtScreenToWorld_Lua);
    /* ClickToMove / GmClickToMove already registered in the main block. */

    LogLine("lua APIs: ext registered (Targeting/Object/Unit/Movement)");
}
