#include "OffsetResolve.h"
#include "MovementConfig.h"

#include <string.h>
#include <stdio.h>
#include <windows.h>

OffsetTable g_off;

enum {
    kBitNetSend = 1u << 0,
    kBitQueue = 1u << 1,
    kBitFsExec = 1u << 2,
    kBitRegFn = 1u << 3,
    kBitLuaNum = 1u << 4,
    kBitLuaPushN = 1u << 5,
    kBitLuaPushS = 1u << 6,
    kBitLuaLStr = 1u << 7,
    kBitGlue = 1u << 8,
    kBitSetTarget = 1u << 9,
    kBitIn = 1u << 10,
    kBitOpName = 1u << 11,
    kBitExtSend = 1u << 12,
    kBitExtCreate = 1u << 13,
};

typedef struct {
    const char* name;
    int is_ext;          /* 0=Ascension, 1=Extensions */
    uint32_t stock;
    uint32_t* slot;
    uint32_t bit;
    const uint8_t* pat;
    const uint8_t* mask; /* 0x00 = wildcard */
    uint32_t len;
} OffSite;

/* Masks: 0xFF keep, 0x00 wild. Absolute VAs / SEH handlers masked. */

/* NetClient::Send — relative [esi+0x534], ASLR-safe, UNIQUE */
static const uint8_t kPatSend[] = {
    0x55, 0x8B, 0xEC, 0x56, 0x8B, 0xF1, 0x83, 0xBE, 0x34, 0x05, 0x00, 0x00
};
static const uint8_t kMaskSend[] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
};

/* Packet queue — abs mov eax,[queue_global] masked */
static const uint8_t kPatQueue[] = {
    0x55, 0x8B, 0xEC, 0xA1, 0x00, 0x00, 0x00, 0x00, 0x85, 0xC0, 0x75, 0x34
};
static const uint8_t kMaskQueue[] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF
};

/* FrameScript_Execute — mask abs globals */
static const uint8_t kPatFs[] = {
    0x55, 0x8B, 0xEC, 0x51, 0x83, 0x05, 0x00, 0x00, 0x00, 0x00, 0x01, 0xA1
};
static const uint8_t kMaskFs[] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF
};

static const uint8_t kPatReg[] = {
    0x55, 0x8B, 0xEC, 0x8B, 0x45, 0x0C, 0x56, 0x8B, 0x35, 0x00, 0x00, 0x00, 0x00
};
static const uint8_t kMaskReg[] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00
};

static const uint8_t kPatLuaToN[] = {
    0x55, 0x8B, 0xEC, 0x8B, 0x45, 0x0C, 0x8B, 0x4D, 0x08, 0x83, 0xEC, 0x10, 0xE8,
    0x00, 0x00, 0x00, 0x00
};
static const uint8_t kMaskLuaToN[] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    0x00, 0x00, 0x00, 0x00
};

static const uint8_t kPatLuaPushN[] = {
    0x55, 0x8B, 0xEC, 0x8B, 0x4D, 0x08, 0xDD, 0x45, 0x0C, 0x8B, 0x41, 0x0C
};
static const uint8_t kMaskLuaPushN[] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
};

static const uint8_t kPatLuaPushS[] = {
    0x55, 0x8B, 0xEC, 0x8B, 0x55, 0x0C, 0x85, 0xD2, 0x75, 0x1C, 0x8B, 0x45
};
static const uint8_t kMaskLuaPushS[] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
};

static const uint8_t kPatLuaLStr[] = {
    0x55, 0x8B, 0xEC, 0x56, 0x8B, 0x75, 0x08, 0x57, 0x8B, 0x7D, 0x0C, 0x8B, 0xC7, 0x8B, 0xCE, 0xE8
};
static const uint8_t kMaskLuaLStr[] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
};

/* GlueLogin — mask abs glue-ready byte address */
static const uint8_t kPatGlue[] = {
    0x55, 0x8B, 0xEC, 0x80, 0x3D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x84
};
static const uint8_t kMaskGlue[] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF
};

static const uint8_t kPatSetTarget[] = {
    0x55, 0x8B, 0xEC, 0x81, 0xEC, 0xAC, 0x02, 0x00, 0x00, 0x53, 0x8B, 0x5D, 0x08, 0x56, 0x8B, 0x75
};
static const uint8_t kMaskSetTarget[] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
};

/*
 * ExtProcessIncoming — SEH prologue + sub esp,0x84 + cookie xor (UNIQUE).
 * Wild: SEH handler VA (+6..+9), security-cookie abs (+24..+27).
 */
static const uint8_t kPatIn[] = {
    0x55, 0x8B, 0xEC, 0x6A, 0xFF, 0x68, 0x00, 0x00, 0x00, 0x00, 0x64, 0xA1,
    0x00, 0x00, 0x00, 0x00, 0x50, 0x81, 0xEC, 0x84, 0x00, 0x00, 0x00, 0xA1,
    0x00, 0x00, 0x00, 0x00, 0x33, 0xC5, 0x89, 0x45, 0xF0, 0x53, 0x56, 0x57,
    0x50, 0x8D, 0x45, 0xF4, 0x64, 0xA3, 0x00, 0x00, 0x00, 0x00, 0x89, 0x55
};
static const uint8_t kMaskIn[] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
};

static const uint8_t kPatOpName[] = {
    0x8B, 0x44, 0x24, 0x04, 0x3D, 0xD4, 0x09, 0x00, 0x00, 0x0F, 0x87, 0x49
};
static const uint8_t kMaskOpName[] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
};

static const uint8_t kPatExtSend[] = {
    0xA1, 0x00, 0x00, 0x00, 0x00, 0x56, 0x6A, 0x01, 0xFF, 0x74, 0x24, 0x0C, 0xFF, 0xD0, 0x83, 0xC4
};
static const uint8_t kMaskExtSend[] = {
    0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
};

/*
 * ExtCreatePacket — SEH + sub esp,8 + push ebx/esi/edi + cookie + arg load (UNIQUE).
 * Wild: SEH handler VA (+6..+9), security-cookie abs (+24..+27).
 */
static const uint8_t kPatExtCreate[] = {
    0x55, 0x8B, 0xEC, 0x6A, 0xFF, 0x68, 0x00, 0x00, 0x00, 0x00, 0x64, 0xA1,
    0x00, 0x00, 0x00, 0x00, 0x50, 0x83, 0xEC, 0x08, 0x53, 0x56, 0x57, 0xA1,
    0x00, 0x00, 0x00, 0x00, 0x33, 0xC5, 0x50, 0x8D, 0x45, 0xF4, 0x64, 0xA3,
    0x00, 0x00, 0x00, 0x00, 0x8B, 0x7D, 0x08, 0xB8, 0xB0, 0xFA, 0x84, 0x00
};
static const uint8_t kMaskExtCreate[] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
};

static int MatchAt(const uint8_t* hay, const uint8_t* pat, const uint8_t* mask, uint32_t len)
{
    uint32_t i;
    for (i = 0; i < len; i++) {
        if ((hay[i] & mask[i]) != (pat[i] & mask[i]))
            return 0;
    }
    return 1;
}

static uint32_t ScanUnique(const uint8_t* base, uint32_t img_size,
                           const uint8_t* pat, const uint8_t* mask, uint32_t len,
                           uint32_t prefer_rva)
{
    uint32_t i;
    uint32_t hits[32];
    uint32_t n = 0;
    uint32_t lo, hi;
    const uint32_t window = 0x20000u;

    if (!base || img_size < len)
        return 0;

    /* Near-stock window first — avoids early-image SEH flood before real site. */
    if (prefer_rva) {
        lo = prefer_rva > window ? prefer_rva - window : 0;
        hi = prefer_rva + window;
        if (hi + len > img_size)
            hi = img_size > len ? img_size - len : 0;
        for (i = lo; i <= hi && n < (uint32_t)(sizeof(hits) / sizeof(hits[0])); i++) {
            if (MatchAt(base + i, pat, mask, len))
                hits[n++] = i;
        }
        if (n == 1)
            return hits[0];
        if (n > 1) {
            uint32_t best = 0;
            uint32_t best_d = 0xFFFFFFFFu;
            for (i = 0; i < n; i++) {
                uint32_t d = hits[i] > prefer_rva ? hits[i] - prefer_rva : prefer_rva - hits[i];
                if (d < best_d) {
                    best_d = d;
                    best = hits[i];
                }
            }
            if (best_d <= 0x10000u)
                return best;
        }
    }

    n = 0;
    for (i = 0; i + len <= img_size && n < (uint32_t)(sizeof(hits) / sizeof(hits[0])); i++) {
        if (MatchAt(base + i, pat, mask, len))
            hits[n++] = i;
    }
    if (n == 1)
        return hits[0];
    if (n > 1 && prefer_rva) {
        uint32_t best = 0;
        uint32_t best_d = 0xFFFFFFFFu;
        for (i = 0; i < n; i++) {
            uint32_t d = hits[i] > prefer_rva ? hits[i] - prefer_rva : prefer_rva - hits[i];
            if (d < best_d && d <= 0x10000u) {
                best_d = d;
                best = hits[i];
            }
        }
        if (best_d <= 0x10000u)
            return best;
    }
    return 0;
}

static uint32_t ModuleImageSize(uint8_t* mod)
{
    IMAGE_DOS_HEADER* dos;
    IMAGE_NT_HEADERS32* nt;
    if (!mod)
        return 0;
    dos = (IMAGE_DOS_HEADER*)mod;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
        return 0;
    nt = (IMAGE_NT_HEADERS32*)(mod + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
        return 0;
    return nt->OptionalHeader.SizeOfImage;
}

static void ResolveOne(OffSite* s, uint8_t* asc, uint32_t asc_sz,
                       uint8_t* ext, uint32_t ext_sz, OffLogFn log)
{
    uint8_t* base = s->is_ext ? ext : asc;
    uint32_t sz = s->is_ext ? ext_sz : asc_sz;
    char msg[160];
    uint32_t found;

    if (!base || !sz || !s->slot)
        return;

    /* 1) Stock OK (exact prologue), OR already an E9 hook at stock (another layer). */
    if (s->stock + s->len <= sz) {
        if (MatchAt(base + s->stock, s->pat, s->mask, s->len)) {
            *s->slot = s->stock;
            return;
        }
        if (s->stock + 5u <= sz && base[s->stock] == 0xE9) {
            *s->slot = s->stock;
            if (log) {
                _snprintf(msg, sizeof(msg),
                    "OffsetResolve KEEP %s stock=0x%X (already E9-hooked)",
                    s->name, (unsigned)s->stock);
                log(msg);
            }
            return;
        }
    }

    /* 2) Unique (or near-stock) AOB */
    found = ScanUnique(base, sz, s->pat, s->mask, s->len, s->stock);
    if (found) {
        *s->slot = found;
        g_off.resolved_flags |= s->bit;
        if (log) {
            _snprintf(msg, sizeof(msg),
                "OffsetResolve RESOLVED %s stock=0x%X -> 0x%X",
                s->name, (unsigned)s->stock, (unsigned)found);
            log(msg);
        }
        return;
    }

    /* 3) Keep stock but mark failed — Install* will still refuse bad prologues */
    *s->slot = s->stock;
    g_off.failed_flags |= s->bit;
    if (log) {
        _snprintf(msg, sizeof(msg),
            "OffsetResolve FAIL %s stock=0x%X (kept stock; hook may refuse)",
            s->name, (unsigned)s->stock);
        log(msg);
    }
}

int OffsetResolve_Init(uint8_t* ascension, uint8_t* extensions, OffLogFn log)
{
    uint32_t asc_sz = ModuleImageSize(ascension);
    uint32_t ext_sz = ModuleImageSize(extensions);
    OffSite sites[14];
    int n = 0;
    int i;

    memset(&g_off, 0, sizeof(g_off));
    g_off.net_client_send = kNetClientSendRva;
    g_off.packet_queue = kPacketQueueRva;
    g_off.frame_script_execute = kFrameScriptExecuteRva;
    g_off.register_function = kRegisterFunctionRva;
    g_off.lua_to_number = kLuaToNumberRva;
    g_off.lua_push_number = kLuaPushNumberRva;
    g_off.lua_push_string = kLuaPushStringRva;
    g_off.lua_to_lstring = kLuaToLStringRva;
    g_off.glue_login = 0x000D8A30u;
    g_off.game_ui_set_target = 0x00124BF0u;
    g_off.ext_process_incoming = kExtProcessIncomingRva;
    g_off.ext_opcode_to_name = kExtOpcodeToNameRva;
    g_off.ext_send = kExtSendRva;
    g_off.ext_create_packet = kExtCreatePacketRva;

    sites[n].name = "NetClientSend"; sites[n].is_ext = 0; sites[n].stock = kNetClientSendRva;
    sites[n].slot = &g_off.net_client_send; sites[n].bit = kBitNetSend;
    sites[n].pat = kPatSend; sites[n].mask = kMaskSend; sites[n].len = (uint32_t)sizeof(kPatSend); n++;
    sites[n].name = "PacketQueue"; sites[n].is_ext = 0; sites[n].stock = kPacketQueueRva;
    sites[n].slot = &g_off.packet_queue; sites[n].bit = kBitQueue;
    sites[n].pat = kPatQueue; sites[n].mask = kMaskQueue; sites[n].len = (uint32_t)sizeof(kPatQueue); n++;
    sites[n].name = "FrameScriptExecute"; sites[n].is_ext = 0; sites[n].stock = kFrameScriptExecuteRva;
    sites[n].slot = &g_off.frame_script_execute; sites[n].bit = kBitFsExec;
    sites[n].pat = kPatFs; sites[n].mask = kMaskFs; sites[n].len = (uint32_t)sizeof(kPatFs); n++;
    sites[n].name = "RegisterFunction"; sites[n].is_ext = 0; sites[n].stock = kRegisterFunctionRva;
    sites[n].slot = &g_off.register_function; sites[n].bit = kBitRegFn;
    sites[n].pat = kPatReg; sites[n].mask = kMaskReg; sites[n].len = (uint32_t)sizeof(kPatReg); n++;
    sites[n].name = "LuaToNumber"; sites[n].is_ext = 0; sites[n].stock = kLuaToNumberRva;
    sites[n].slot = &g_off.lua_to_number; sites[n].bit = kBitLuaNum;
    sites[n].pat = kPatLuaToN; sites[n].mask = kMaskLuaToN; sites[n].len = (uint32_t)sizeof(kPatLuaToN); n++;
    sites[n].name = "LuaPushNumber"; sites[n].is_ext = 0; sites[n].stock = kLuaPushNumberRva;
    sites[n].slot = &g_off.lua_push_number; sites[n].bit = kBitLuaPushN;
    sites[n].pat = kPatLuaPushN; sites[n].mask = kMaskLuaPushN; sites[n].len = (uint32_t)sizeof(kPatLuaPushN); n++;
    sites[n].name = "LuaPushString"; sites[n].is_ext = 0; sites[n].stock = kLuaPushStringRva;
    sites[n].slot = &g_off.lua_push_string; sites[n].bit = kBitLuaPushS;
    sites[n].pat = kPatLuaPushS; sites[n].mask = kMaskLuaPushS; sites[n].len = (uint32_t)sizeof(kPatLuaPushS); n++;
    sites[n].name = "LuaToLString"; sites[n].is_ext = 0; sites[n].stock = kLuaToLStringRva;
    sites[n].slot = &g_off.lua_to_lstring; sites[n].bit = kBitLuaLStr;
    sites[n].pat = kPatLuaLStr; sites[n].mask = kMaskLuaLStr; sites[n].len = (uint32_t)sizeof(kPatLuaLStr); n++;
    sites[n].name = "GlueLogin"; sites[n].is_ext = 0; sites[n].stock = 0x000D8A30u;
    sites[n].slot = &g_off.glue_login; sites[n].bit = kBitGlue;
    sites[n].pat = kPatGlue; sites[n].mask = kMaskGlue; sites[n].len = (uint32_t)sizeof(kPatGlue); n++;
    sites[n].name = "GameUiSetTarget"; sites[n].is_ext = 0; sites[n].stock = 0x00124BF0u;
    sites[n].slot = &g_off.game_ui_set_target; sites[n].bit = kBitSetTarget;
    sites[n].pat = kPatSetTarget; sites[n].mask = kMaskSetTarget; sites[n].len = (uint32_t)sizeof(kPatSetTarget); n++;
    sites[n].name = "ExtProcessIncoming"; sites[n].is_ext = 1; sites[n].stock = kExtProcessIncomingRva;
    sites[n].slot = &g_off.ext_process_incoming; sites[n].bit = kBitIn;
    sites[n].pat = kPatIn; sites[n].mask = kMaskIn; sites[n].len = (uint32_t)sizeof(kPatIn); n++;
    sites[n].name = "ExtOpcodeToName"; sites[n].is_ext = 1; sites[n].stock = kExtOpcodeToNameRva;
    sites[n].slot = &g_off.ext_opcode_to_name; sites[n].bit = kBitOpName;
    sites[n].pat = kPatOpName; sites[n].mask = kMaskOpName; sites[n].len = (uint32_t)sizeof(kPatOpName); n++;
    sites[n].name = "ExtSend"; sites[n].is_ext = 1; sites[n].stock = kExtSendRva;
    sites[n].slot = &g_off.ext_send; sites[n].bit = kBitExtSend;
    sites[n].pat = kPatExtSend; sites[n].mask = kMaskExtSend; sites[n].len = (uint32_t)sizeof(kPatExtSend); n++;
    sites[n].name = "ExtCreatePacket"; sites[n].is_ext = 1; sites[n].stock = kExtCreatePacketRva;
    sites[n].slot = &g_off.ext_create_packet; sites[n].bit = kBitExtCreate;
    sites[n].pat = kPatExtCreate; sites[n].mask = kMaskExtCreate; sites[n].len = (uint32_t)sizeof(kPatExtCreate); n++;

    for (i = 0; i < n; i++)
        ResolveOne(&sites[i], ascension, asc_sz, extensions, ext_sz, log);

    if (log) {
        char msg[128];
        _snprintf(msg, sizeof(msg),
            "OffsetResolve done remapped=0x%X failed=0x%X asc_sz=0x%X ext_sz=0x%X",
            (unsigned)g_off.resolved_flags, (unsigned)g_off.failed_flags,
            (unsigned)asc_sz, (unsigned)ext_sz);
        log(msg);
    }
    return (g_off.failed_flags == 0) ? 1 : 0;
}
