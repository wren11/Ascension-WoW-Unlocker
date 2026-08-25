/*
 * QueryCache.inc.c — CMSG item/GO/creature/name query + SMSG name cache.
 *
 * WotLK 3.3.5a (Ascension). Injected via NetClient::Send (same path as loot).
 * SMSG_*_QUERY_RESPONSE is template data (name/type) — NOT world XYZ.
 * World XYZ is object-manager only (create/update). Hunt stamps pins from OM
 * after these names confirm the object is real.
 *
 * Included from ProxyMain.c (LuaArg*, InjectClientPacket, ParseHexGuid).
 */

enum {
    kCmsgNameQuery = 0x0050u,
    kSmsgNameQueryResponse = 0x0051u,
    kCmsgItemQuerySingle = 0x0056u,
    kSmsgItemQuerySingleResponse = 0x0058u,
    kCmsgGameobjectQuery = 0x005Eu,
    kSmsgGameobjectQueryResponse = 0x005Fu,
    kCmsgCreatureQuery = 0x0060u,
    kSmsgCreatureQueryResponse = 0x0061u,
    kQKindItem = 1u,
    kQKindGo = 2u,
    kQKindCreature = 3u,
    kQKindName = 4u,
    kQCacheMax = 384u,
    kQNameCap = 96u
};

typedef struct QueryCacheEnt {
    uint32_t kind;
    uint32_t id;
    uint32_t found;
    uint32_t extra; /* GO type / unused */
    DWORD at;
    char name[kQNameCap];
} QueryCacheEnt;

static QueryCacheEnt g_qcache[kQCacheMax];
static uint32_t g_qcache_n = 0;
static CRITICAL_SECTION g_qcache_cs;
static int g_qcache_cs_ready = 0;

static void QueryCacheInit(void)
{
    if (g_qcache_cs_ready)
        return;
    InitializeCriticalSection(&g_qcache_cs);
    g_qcache_cs_ready = 1;
}

static int QueryReadCString(const uint8_t* p, uint32_t n, uint32_t* off,
                            char* out, uint32_t cap)
{
    uint32_t i, o;
    if (!p || !off || !out || cap < 2u)
        return 0;
    i = *off;
    o = 0;
    if (i >= n) {
        out[0] = 0;
        return 0;
    }
    while (i < n && p[i] && o + 1u < cap)
        out[o++] = (char)p[i++];
    out[o] = 0;
    if (i < n && p[i] == 0)
        i++;
    *off = i;
    return o > 0 ? 1 : 0;
}

static QueryCacheEnt* QueryCacheFind(uint32_t kind, uint32_t id)
{
    uint32_t i;
    for (i = 0; i < g_qcache_n; i++) {
        if (g_qcache[i].kind == kind && g_qcache[i].id == id)
            return &g_qcache[i];
    }
    return NULL;
}

static QueryCacheEnt* QueryCacheSlot(uint32_t kind, uint32_t id)
{
    QueryCacheEnt* e = QueryCacheFind(kind, id);
    if (e)
        return e;
    if (g_qcache_n >= kQCacheMax) {
        memmove(&g_qcache[0], &g_qcache[kQCacheMax / 4u],
                (size_t)(kQCacheMax - kQCacheMax / 4u) * sizeof(g_qcache[0]));
        g_qcache_n = kQCacheMax - kQCacheMax / 4u;
    }
    e = &g_qcache[g_qcache_n++];
    memset(e, 0, sizeof(*e));
    e->kind = kind;
    e->id = id;
    return e;
}

static void QueryCachePut(uint32_t kind, uint32_t id, int found,
                          const char* name, uint32_t extra)
{
    QueryCacheEnt* e;
    QueryCacheInit();
    EnterCriticalSection(&g_qcache_cs);
    e = QueryCacheSlot(kind, id);
    e->found = found ? 1u : 0u;
    e->extra = extra;
    e->at = GetTickCount();
    if (name && name[0]) {
        strncpy(e->name, name, kQNameCap - 1u);
        e->name[kQNameCap - 1u] = 0;
    } else if (!found) {
        e->name[0] = 0;
    }
    LeaveCriticalSection(&g_qcache_cs);
}

static void QueryCacheNoteBody(uint32_t opcode, const uint8_t* body, uint32_t n)
{
    uint32_t id = 0, extra = 0, off;
    char name[kQNameCap];
    int found = 1;
    uint32_t kind;

    if (!body || n < 4u)
        return;
    memcpy(&id, body, 4);
    name[0] = 0;

    if (opcode == kSmsgGameobjectQueryResponse) {
        kind = kQKindGo;
        if (id & 0x80000000u) {
            found = 0;
            id &= 0x7FFFFFFFu;
        } else {
            if (n >= 8u)
                memcpy(&extra, body + 4, 4); /* GO type */
            off = 12u; /* entry + type + displayId */
            QueryReadCString(body, n, &off, name, kQNameCap);
        }
        QueryCachePut(kind, id, found, name, extra);
        return;
    }
    if (opcode == kSmsgItemQuerySingleResponse) {
        kind = kQKindItem;
        if (id & 0x80000000u) {
            found = 0;
            id &= 0x7FFFFFFFu;
        } else {
            off = 16u; /* id + class + subclass + soundOverride */
            QueryReadCString(body, n, &off, name, kQNameCap);
        }
        QueryCachePut(kind, id, found, name, 0);
        return;
    }
    if (opcode == kSmsgCreatureQueryResponse) {
        kind = kQKindCreature;
        if (id & 0x80000000u) {
            found = 0;
            id &= 0x7FFFFFFFu;
        } else {
            off = 4u;
            QueryReadCString(body, n, &off, name, kQNameCap);
        }
        QueryCachePut(kind, id, found, name, 0);
        return;
    }
}

void QueryCacheNotePacket(uint8_t dir, uint32_t opcode, const uint8_t* p, uint32_t len)
{
    const uint8_t* body;
    uint32_t n;
    if (dir != kPktDirIn || !p || len < 8u)
        return;
    if (opcode != kSmsgItemQuerySingleResponse
        && opcode != kSmsgGameobjectQueryResponse
        && opcode != kSmsgCreatureQueryResponse)
        return;
    body = p + 4;
    n = len - 4u;
    QueryCacheNoteBody(opcode, body, n);
}

static int QueryFreshEnough(const QueryCacheEnt* e, DWORD now)
{
    if (!e || !e->at)
        return 0;
    return (now - e->at) < 60000u;
}

static int QuerySendEntryGuid(uint32_t opcode, uint32_t entry, uint64_t guid)
{
    uint8_t buf[16];
    memcpy(buf + 0, &opcode, 4);
    memcpy(buf + 4, &entry, 4);
    memcpy(buf + 8, &guid, 8);
    return InjectClientPacket(buf, 16) ? 1 : 0;
}

static int QuerySendU32(uint32_t opcode, uint32_t id)
{
    uint8_t buf[8];
    memcpy(buf + 0, &opcode, 4);
    memcpy(buf + 4, &id, 4);
    return InjectClientPacket(buf, 8) ? 1 : 0;
}

static int QuerySendGuid(uint32_t opcode, uint64_t guid)
{
    uint8_t buf[12];
    memcpy(buf + 0, &opcode, 4);
    memcpy(buf + 4, &guid, 8);
    return InjectClientPacket(buf, 12) ? 1 : 0;
}

static int QueryMaybeSend(uint32_t kind, uint32_t id, uint64_t guid)
{
    QueryCacheEnt* e;
    DWORD now = GetTickCount();
    QueryCacheInit();
    EnterCriticalSection(&g_qcache_cs);
    e = QueryCacheFind(kind, id);
    if (e && QueryFreshEnough(e, now) && e->found) {
        LeaveCriticalSection(&g_qcache_cs);
        return 1;
    }
    LeaveCriticalSection(&g_qcache_cs);

    ForceClearTaint();
    if (kind == kQKindGo)
        return QuerySendEntryGuid(kCmsgGameobjectQuery, id, guid);
    if (kind == kQKindCreature)
        return QuerySendEntryGuid(kCmsgCreatureQuery, id, guid);
    if (kind == kQKindItem)
        return QuerySendU32(kCmsgItemQuerySingle, id);
    if (kind == kQKindName)
        return QuerySendGuid(kCmsgNameQuery, guid);
    return 0;
}

static int __cdecl GmQueryGo_Lua(void* L)
{
    uint32_t entry = (uint32_t)LuaArgNum(L, 1);
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 2));
    LuaPushNum(L, QueryMaybeSend(kQKindGo, entry, guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmQueryItem_Lua(void* L)
{
    uint32_t id = (uint32_t)LuaArgNum(L, 1);
    LuaPushNum(L, QueryMaybeSend(kQKindItem, id, 0) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmQueryCreature_Lua(void* L)
{
    uint32_t entry = (uint32_t)LuaArgNum(L, 1);
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 2));
    LuaPushNum(L, QueryMaybeSend(kQKindCreature, entry, guid) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmQueryName_Lua(void* L)
{
    uint64_t guid = ParseHexGuid(LuaArgStr(L, 1));
    LuaPushNum(L, QueryMaybeSend(kQKindName, (uint32_t)(guid & 0xFFFFFFFFu), guid) ? 1.0 : 0.0);
    return 1;
}

/* found, name, extra, ageMs = GmQueryPeek(kind, id)
 * kind: "go"|"item"|"creature"|"name"  (or 1..4) */
static int __cdecl GmQueryPeek_Lua(void* L)
{
    const char* ks = LuaArgStr(L, 1);
    uint32_t id = (uint32_t)LuaArgNum(L, 2);
    uint32_t kind = 0;
    QueryCacheEnt* e;
    DWORD now, age;
    if (ks) {
        if (ks[0] == 'g' || ks[0] == 'G') kind = kQKindGo;
        else if (ks[0] == 'i' || ks[0] == 'I') kind = kQKindItem;
        else if (ks[0] == 'c' || ks[0] == 'C') kind = kQKindCreature;
        else if (ks[0] == 'n' || ks[0] == 'N') kind = kQKindName;
    }
    if (!kind)
        kind = (uint32_t)LuaArgNum(L, 1);
    QueryCacheInit();
    EnterCriticalSection(&g_qcache_cs);
    e = QueryCacheFind(kind, id);
    if (!e) {
        LeaveCriticalSection(&g_qcache_cs);
        LuaPushNum(L, 0.0);
        LuaPushStr(L, "");
        LuaPushNum(L, 0.0);
        LuaPushNum(L, -1.0);
        return 4;
    }
    now = GetTickCount();
    age = e->at ? (now - e->at) : 0xFFFFFFFFu;
    LuaPushNum(L, e->found ? 1.0 : 0.0);
    LuaPushStr(L, e->name);
    LuaPushNum(L, (double)e->extra);
    LuaPushNum(L, (double)age);
    LeaveCriticalSection(&g_qcache_cs);
    return 4;
}

static void RegisterQueryCacheApis(RegisterFunctionFn reg)
{
    QueryCacheInit();
    reg("GmQueryGo", (void*)GmQueryGo_Lua);
    reg("GmQueryItem", (void*)GmQueryItem_Lua);
    reg("GmQueryCreature", (void*)GmQueryCreature_Lua);
    reg("GmQueryName", (void*)GmQueryName_Lua);
    reg("GmQueryPeek", (void*)GmQueryPeek_Lua);
}
