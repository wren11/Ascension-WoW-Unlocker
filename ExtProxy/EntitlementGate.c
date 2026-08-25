/* Discord-linked Core: GMToolBox pushes max_instances (up to INST_BUS_MAX_INST / 64).
 * Name allow-list is unused while gateOn is false (account Core unlocks all toons). */
#include "PktIpc.h"
#include "InstanceBus.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>

void ProxyLogLine(const char* msg);
int ProxyOpcodeName(uint32_t opcode, char* out, uint32_t out_cap);

#define kMaxOp 0x9D4u

static CRITICAL_SECTION g_cs;
static int g_cs_ready;
static uint32_t g_flags;
static uint32_t g_max_instances = 1;
static uint32_t g_name_count;
static char g_names[ENT_MAX_NAMES][ENT_NAME_LEN];
static uint32_t g_enum_count;
static uint64_t g_enum_guid[ENT_MAX_NAMES];
static char g_enum_name[ENT_MAX_NAMES][ENT_NAME_LEN];

static void EnsureCs(void)
{
    if (g_cs_ready) return;
    InitializeCriticalSection(&g_cs);
    g_cs_ready = 1;
}

static void ToLowerCopy(char* dst, int cap, const char* src)
{
    int i = 0;
    if (!dst || cap < 2) return;
    dst[0] = 0;
    if (!src) return;
    while (src[i] && i < cap - 1) {
        dst[i] = (char)tolower((unsigned char)src[i]);
        i++;
    }
    dst[i] = 0;
}

static int IsLetterName(const char* s)
{
    int n = 0;
    if (!s) return 0;
    while (s[n]) {
        unsigned char c = (unsigned char)s[n];
        if (c < 'a' || c > 'z') return 0;
        n++;
    }
    return n >= 2 && n <= 24;
}

static int NameAllowed(const char* name)
{
    uint32_t i;
    if (!name || !name[0]) return 0;
    for (i = 0; i < g_name_count; i++) {
        if (_stricmp(g_names[i], name) == 0)
            return 1;
    }
    return 0;
}

static void RememberGuid(uint64_t guid, const char* name)
{
    uint32_t i;
    char low[ENT_NAME_LEN];
    if (!guid || !IsLetterName(name)) return;
    ToLowerCopy(low, ENT_NAME_LEN, name);
    for (i = 0; i < g_enum_count; i++) {
        if (g_enum_guid[i] == guid) {
            memcpy(g_enum_name[i], low, ENT_NAME_LEN);
            return;
        }
    }
    if (g_enum_count >= ENT_MAX_NAMES) return;
    g_enum_guid[g_enum_count] = guid;
    memcpy(g_enum_name[g_enum_count], low, ENT_NAME_LEN);
    g_enum_count++;
}

static const char* NameForGuid(uint64_t guid)
{
    uint32_t i;
    if (!guid) return NULL;
    for (i = 0; i < g_enum_count; i++) {
        if (g_enum_guid[i] == guid)
            return g_enum_name[i];
    }
    return NULL;
}

static uint32_t ReadOp(const uint8_t* data, uint32_t size, uint32_t* body_off)
{
    uint32_t op = 0;
    uint16_t op16 = 0;
    if (!data || size < 2) {
        if (body_off) *body_off = 0;
        return 0;
    }
    memcpy(&op16, data, 2);
    if (size >= 4) {
        memcpy(&op, data, 4);
        if (op <= kMaxOp) {
            if (body_off) *body_off = 4;
            return op;
        }
    }
    if (body_off) *body_off = 2;
    return op16;
}

static int OpLooksLike(uint32_t opcode, const char* needle)
{
    char name[64];
    if (opcode == 0x003Bu || opcode == 0x075Eu || opcode == 0x076Fu)
        return needle && _stricmp(needle, "CHAR_ENUM") == 0;
    if (opcode == 0x003Du || opcode == 0x03C2u)
        return needle && _stricmp(needle, "PLAYER_LOGIN") == 0;
    if (!ProxyOpcodeName(opcode, name, sizeof(name)))
        return 0;
    return needle && name[0] && strstr(name, needle) != NULL;
}

int EntitlementIsPlayerLogin(uint32_t opcode)
{
    return OpLooksLike(opcode, "PLAYER_LOGIN");
}

int EntitlementIsGateOpcode(uint32_t opcode)
{
    char name[64];
    if (opcode == 0x0037u || opcode == 0x003Bu || opcode == 0x003Du
        || opcode == 0x03C2u || opcode == 0x0041u || opcode == 0x075Eu)
        return 1;
    if (!ProxyOpcodeName(opcode, name, sizeof(name)))
        return 0;
    return strstr(name, "CHAR_ENUM") || strstr(name, "PLAYER_LOGIN")
        || strstr(name, "CHARACTER_LIST") || strstr(name, "CHAR_CREATE");
}

static void ParseCharEnumBody(const uint8_t* body, uint32_t n)
{
    uint32_t count, off, i;
    if (!body || n < 10) return;
    count = body[0];
    off = 1;
    if (count == 0 || count > ENT_MAX_NAMES) {
        /* Remapped / extra header — scan guid + cstring pairs. */
        off = 0;
        while (off + 10 <= n && g_enum_count < ENT_MAX_NAMES) {
            uint64_t guid = 0;
            char name[ENT_NAME_LEN];
            uint32_t k = 0;
            memcpy(&guid, body + off, 8);
            off += 8;
            while (off < n && body[off] && k < 24)
                name[k++] = (char)body[off++];
            name[k] = 0;
            if (off < n && body[off] == 0) off++;
            ToLowerCopy(name, ENT_NAME_LEN, name);
            if (guid && IsLetterName(name))
                RememberGuid(guid, name);
            else
                off++;
        }
        return;
    }
    for (i = 0; i < count && off + 9 <= n; i++) {
        uint64_t guid = 0;
        char name[ENT_NAME_LEN];
        uint32_t k = 0;
        uint32_t skip;
        memcpy(&guid, body + off, 8);
        off += 8;
        while (off < n && body[off] && k < 24)
            name[k++] = (char)body[off++];
        name[k] = 0;
        if (off < n && body[off] == 0) off++;
        ToLowerCopy(name, ENT_NAME_LEN, name);
        if (guid && IsLetterName(name))
            RememberGuid(guid, name);
        /* Stock WotLK remainder after name. */
        skip = 1u + 1u + 1u + 5u + 1u + 4u + 4u + 12u + 4u + 4u + 4u + 1u + 12u + (23u * 9u);
        if (off + skip <= n)
            off += skip;
        else
            break;
    }
}

void EntitlementSet(uint32_t flags, uint32_t max_instances, const char names[][ENT_NAME_LEN], uint32_t count)
{
    uint32_t i;
    char msg[160];
    EnsureCs();
    EnterCriticalSection(&g_cs);
    g_flags = flags;
    g_max_instances = max_instances ? max_instances : 1;
    if (g_max_instances > INST_BUS_MAX_INST) g_max_instances = INST_BUS_MAX_INST;
    g_name_count = 0;
    if (names && count) {
        if (count > ENT_MAX_NAMES) count = ENT_MAX_NAMES;
        for (i = 0; i < count; i++) {
            ToLowerCopy(g_names[g_name_count], ENT_NAME_LEN, names[i]);
            if (IsLetterName(g_names[g_name_count]))
                g_name_count++;
        }
    }
    LeaveCriticalSection(&g_cs);
    _snprintf(msg, sizeof(msg), "entitlement: gate=%u account=%u names=%u maxInst=%u",
        (flags & ENT_FLAG_GATE_ON) ? 1u : 0u,
        (flags & ENT_FLAG_HAS_ACCOUNT) ? 1u : 0u,
        g_name_count, g_max_instances);
    ProxyLogLine(msg);
}

void EntitlementOnPacket(uint8_t dir, uint32_t opcode, const uint8_t* data, uint32_t size)
{
    uint32_t body_off = 0;
    if (!data || size < 3) return;
    ReadOp(data, size, &body_off);
    if (dir == kPktDirIn && OpLooksLike(opcode, "CHAR_ENUM")) {
        EnsureCs();
        EnterCriticalSection(&g_cs);
        ParseCharEnumBody(data + body_off, size > body_off ? size - body_off : 0);
        LeaveCriticalSection(&g_cs);
    }
}

int EntitlementShouldDropSend(uint32_t opcode, const uint8_t* data, uint32_t size)
{
    (void)opcode;
    (void)data;
    (void)size;
    return 0;
}
