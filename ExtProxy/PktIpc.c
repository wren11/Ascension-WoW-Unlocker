#include "PktIpc.h"
#include "MovementConfig.h"
#include "ObjectMgr.h"
#include "OffsetResolve.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <math.h>

float ProxyPlayerFacingCached(void);

static HANDLE g_map = NULL;
static PktRingHeader* g_hdr = NULL;
static PktRingSlot* g_slots = NULL;
static HANDLE g_pipe_thread = NULL;
static volatile LONG g_ipc_stop = 0;
static char g_pipe_name[128];
static char g_ring_name[128];
static uint32_t g_owner_pid = 0;

static CRITICAL_SECTION g_replay_cs;
static uint8_t g_replay_buf[PKT_REPLAY_MAX];
static uint32_t g_replay_size = 0;
static volatile LONG g_replay_pending = 0;
static volatile LONG g_replay_ok = 0;
static volatile LONG g_replay_fail = 0;
static volatile LONG g_sniff_in = 0;
static volatile LONG g_sniff_out = 0;
static volatile LONG g_sniff_dropped = 0;
static volatile LONG g_chat_capture = 1;

/* Opcode ignore bitset — checked before ring write (game-thread cheap). */
enum { kOpIgnoreBits = 16384, kOpIgnoreWords = kOpIgnoreBits / 32 };
static uint32_t g_op_ignore[kOpIgnoreWords];
static CRITICAL_SECTION g_op_ignore_cs;
static int g_op_ignore_cs_ready = 0;

static void OpIgnoreInit(void)
{
    if (g_op_ignore_cs_ready)
        return;
    InitializeCriticalSection(&g_op_ignore_cs);
    memset(g_op_ignore, 0, sizeof(g_op_ignore));
    g_op_ignore_cs_ready = 1;
}

static int OpIsIgnored(uint32_t opcode)
{
    uint32_t w, bit;
    if (opcode >= kOpIgnoreBits)
        return 0;
    w = opcode >> 5;
    bit = opcode & 31u;
    return (g_op_ignore[w] >> bit) & 1u;
}

static void OpIgnoreReplace(const uint16_t* ops, uint32_t count)
{
    uint32_t i;
    OpIgnoreInit();
    EnterCriticalSection(&g_op_ignore_cs);
    memset(g_op_ignore, 0, sizeof(g_op_ignore));
    for (i = 0; i < count; i++) {
        uint32_t op = ops[i];
        if (op < kOpIgnoreBits)
            g_op_ignore[op >> 5] |= (1u << (op & 31u));
    }
    LeaveCriticalSection(&g_op_ignore_cs);
}

static int IsChatCaptureOpcode(uint32_t opcode)
{
    switch (opcode) {
    case 0x0096u: /* SMSG_MESSAGECHAT */
    case 0x03B3u: /* SMSG_GM_MESSAGECHAT */
    case 0x01CBu: /* SMSG_NOTIFICATION */
    case 0x0291u: /* SMSG_SERVER_MESSAGE */
    case 0x033Du: /* SMSG_MOTD */
    case 0x0051u: /* SMSG_NAME_QUERY_RESPONSE — GUID→name dossier */
        return 1;
    default:
        return 0;
    }
}

static CRITICAL_SECTION g_inj_in_cs;
static uint8_t g_inj_in_buf[PKT_REPLAY_MAX];
static uint32_t g_inj_in_size = 0;
static volatile LONG g_inj_in_pending = 0;
static int g_inj_in_cs_ready = 0;

/* Multi-instance shared world model: the host pushes an aggregated SharedView
 * (objects from every connected instance, tagged with src_instance) down to this
 * client. Cached here so the GmShared* Lua natives read it without a round-trip. */
static CRITICAL_SECTION g_shared_cs;
static int g_shared_cs_ready = 0;
static SharedViewHeader g_shared_hdr;          /* last header received from host */
static SharedViewObject g_shared_objs[SHARED_VIEW_MAX_OBJECTS];
static uint32_t g_shared_count = 0;
static uint32_t g_cfg_instance_id = 0;          /* from ExtProxy.cfg until host pushes */

static void LogIpc(const char* msg)
{
    /* Rotate via ProxyLogLine — never unbounded append. */
    char line[256];
    _snprintf(line, sizeof(line), "ipc %s", msg ? msg : "");
    ProxyLogLine(line);
}

int PktIpcSniffEnabled(void)
{
    return g_hdr && g_hdr->sniff_enabled;
}

int PktIpcWantCapture(void)
{
    if (!g_hdr)
        return 0;
    if (g_hdr->sniff_enabled)
        return 1;
    return g_chat_capture ? 1 : 0;
}

int PktIpcSniff(uint8_t dir, uint32_t opcode, const uint8_t* data, uint32_t size)
{
    uint32_t seq;
    uint32_t idx;
    uint32_t read_seq;
    PktRingSlot* s;
    int sniff_on;
    int want_chat;
    if (!g_hdr || !g_slots || !data || !size)
        return 0;

    sniff_on = g_hdr->sniff_enabled ? 1 : 0;
    want_chat = (g_chat_capture && dir == kPktDirIn && IsChatCaptureOpcode(opcode)) ? 1 : 0;
    if (!sniff_on && !want_chat && !EntitlementIsGateOpcode(opcode))
        return 0;

    /* Ignore filter applies to full sniff; chat opcodes always pass when chat capture on. */
    if (sniff_on && !want_chat && OpIsIgnored(opcode)) {
        InterlockedIncrement(&g_sniff_dropped);
        return 0;
    }

    if (size > PKT_RING_MAX)
        size = PKT_RING_MAX;

    /* Count overwrites only — ignored opcodes use g_sniff_dropped separately. */
    read_seq = g_hdr->read_seq;
    seq = g_hdr->write_seq;
    if (seq > read_seq && (seq - read_seq) >= PKT_RING_SLOTS)
        InterlockedIncrement((volatile LONG*)&g_hdr->drop_count);

    seq = (uint32_t)InterlockedIncrement((volatile LONG*)&g_hdr->write_seq);
    idx = (seq - 1u) % PKT_RING_SLOTS;
    s = &g_slots[idx];
    /* Invalidate slot, write payload, then publish seq (avoid torn reads). */
    InterlockedExchange((volatile LONG*)&s->seq, 0);
    s->tick = GetTickCount();
    s->dir = dir;
    s->pad0 = 0;
    s->size = (uint16_t)size;
    s->opcode = opcode;
    memcpy(s->data, data, size);
    /* No full-slot memset — reader uses size; zeroing 2KB/pkt froze cities. */
    InterlockedExchange((volatile LONG*)&s->seq, (LONG)seq);
    if (dir == kPktDirIn)
        InterlockedIncrement(&g_sniff_in);
    else
        InterlockedIncrement(&g_sniff_out);
    return 1;
}

int PktIpcTakeReplay(uint8_t* out, uint32_t* inout_size)
{
    uint32_t n;
    if (!out || !inout_size)
        return 0;
    if (InterlockedCompareExchange(&g_replay_pending, 0, 1) != 1)
        return 0;
    EnterCriticalSection(&g_replay_cs);
    n = g_replay_size;
    if (n > *inout_size)
        n = *inout_size;
    if (n)
        memcpy(out, g_replay_buf, n);
    g_replay_size = 0;
    LeaveCriticalSection(&g_replay_cs);
    *inout_size = n;
    return n > 0;
}

static int QueueReplay(const uint8_t* data, uint32_t size)
{
    if (!data || !size || size > PKT_REPLAY_MAX)
        return 0;
    EnterCriticalSection(&g_replay_cs);
    memcpy(g_replay_buf, data, size);
    g_replay_size = size;
    LeaveCriticalSection(&g_replay_cs);
    InterlockedExchange(&g_replay_pending, 1);
    return 1;
}

static int QueueInjectIn(const uint8_t* data, uint32_t size)
{
    if (!data || !size || size > PKT_REPLAY_MAX)
        return 0;
    if (!g_inj_in_cs_ready)
        return 0;
    EnterCriticalSection(&g_inj_in_cs);
    memcpy(g_inj_in_buf, data, size);
    g_inj_in_size = size;
    LeaveCriticalSection(&g_inj_in_cs);
    InterlockedExchange(&g_inj_in_pending, 1);
    return 1;
}

int PktIpcInjectInPending(void)
{
    return InterlockedCompareExchange(&g_inj_in_pending, 0, 0) == 1;
}

int PktIpcTakeInjectIn(uint8_t* out, uint32_t* inout_size)
{
    uint32_t n;
    if (!out || !inout_size || !g_inj_in_cs_ready)
        return 0;
    if (InterlockedCompareExchange(&g_inj_in_pending, 0, 1) != 1)
        return 0;
    EnterCriticalSection(&g_inj_in_cs);
    n = g_inj_in_size;
    if (n > *inout_size)
        n = *inout_size;
    if (n)
        memcpy(out, g_inj_in_buf, n);
    g_inj_in_size = 0;
    LeaveCriticalSection(&g_inj_in_cs);
    *inout_size = n;
    return n > 0;
}

int ProxyQueueInjectIncoming(const uint8_t* data, uint32_t size)
{
    return QueueInjectIn(data, size);
}

int PktIpcQueueReplay(const uint8_t* data, uint32_t size)
{
    return QueueReplay(data, size);
}

static int WriteAll(HANDLE h, const void* buf, DWORD len)
{
    const uint8_t* p = (const uint8_t*)buf;
    DWORD done = 0;
    while (done < len) {
        DWORD n = 0;
        if (!WriteFile(h, p + done, len - done, &n, NULL) || !n)
            return 0;
        done += n;
    }
    return 1;
}

static int ReadExact(HANDLE h, void* buf, DWORD len)
{
    uint8_t* p = (uint8_t*)buf;
    DWORD done = 0;
    while (done < len) {
        DWORD n = 0;
        if (!ReadFile(h, p + done, len - done, &n, NULL) || !n)
            return 0;
        done += n;
    }
    return 1;
}

static void SendReply(HANDLE h, uint32_t cmd, const void* payload, uint32_t len)
{
    PktCmdHeader hdr;
    hdr.magic = PKT_CMD_MAGIC;
    hdr.cmd = cmd;
    hdr.len = len;
    if (!WriteAll(h, &hdr, sizeof(hdr)))
        return;
    if (len && payload)
        WriteAll(h, payload, len);
}

static void HandleClient(HANDLE pipe)
{
    for (;;) {
        PktCmdHeader hdr;
        uint8_t payload[PKT_CMD_PAYLOAD_MAX + 16];
        if (!ReadExact(pipe, &hdr, sizeof(hdr)))
            break;
        if (hdr.magic != PKT_CMD_MAGIC)
            break;

        if (hdr.len > sizeof(payload)) {
            uint8_t drain[512];
            uint32_t left = hdr.len;
            while (left) {
                uint32_t chunk = left < sizeof(drain) ? left : (uint32_t)sizeof(drain);
                if (!ReadExact(pipe, drain, chunk))
                    return;
                left -= chunk;
            }
            {
                uint32_t err = 0;
                SendReply(pipe, hdr.cmd, &err, sizeof(err));
            }
            continue;
        }
        if (hdr.len && !ReadExact(pipe, payload, hdr.len))
            break;

        switch (hdr.cmd) {
        case kCmdPing: {
            uint32_t v = 1;
            SendReply(pipe, kCmdPing, &v, sizeof(v));
            break;
        }
        case kCmdGetConfig: {
            MovementConfig cfg;
            ProxyGetConfig(&cfg);
            SendReply(pipe, kCmdGetConfig, &cfg, sizeof(cfg));
            break;
        }
        case kCmdSetConfig:
            if (hdr.len >= 64) {
                MovementConfig cfg;
                memset(&cfg, 0, sizeof(cfg));
                memcpy(&cfg, payload, hdr.len < sizeof(cfg) ? hdr.len : sizeof(cfg));
                ProxySetConfig(&cfg);
            }
            {
                MovementConfig out;
                ProxyGetConfig(&out);
                SendReply(pipe, kCmdSetConfig, &out, sizeof(out));
            }
            break;
        case kCmdSetSpeed:
            if (hdr.len >= 8) {
                float scale = 1.f;
                uint32_t cheat = 0;
                float applied;
                memcpy(&scale, payload, 4);
                memcpy(&cheat, payload + 4, 4);
                applied = ProxySetSpeed(scale, cheat);
                SendReply(pipe, kCmdSetSpeed, &applied, sizeof(applied));
            }
            break;
        case kCmdSetHacks:
            if (hdr.len >= 8) {
                uint32_t hacks = 0, fly = 0, applied;
                memcpy(&hacks, payload, 4);
                memcpy(&fly, payload + 4, 4);
                applied = ProxySetHacks(hacks, fly);
                SendReply(pipe, kCmdSetHacks, &applied, sizeof(applied));
            }
            break;
        case kCmdSetAntiAfk: {
            AntiAfkStatus st;
            if (hdr.len >= 4) {
                uint32_t en = 0, iv = 0;
                memcpy(&en, payload, 4);
                if (hdr.len >= 8)
                    memcpy(&iv, payload + 4, 4);
                ProxySetAntiAfk(en, iv);
            }
            ProxyGetAntiAfk(&st);
            SendReply(pipe, kCmdSetAntiAfk, &st, sizeof(st));
            break;
        }
        case kCmdFindOpcode:
            if (hdr.len >= 1) {
                char name[96];
                uint32_t op = 0;
                uint32_t n = hdr.len < sizeof(name) - 1u ? hdr.len : (uint32_t)(sizeof(name) - 1u);
                memcpy(name, payload, n);
                name[n] = 0;
                if (!ProxyFindOpcode(name, &op))
                    op = 0;
                SendReply(pipe, kCmdFindOpcode, &op, sizeof(op));
            }
            break;
        case kCmdExtNetInfo: {
            char info[384];
            MovementConfig cfg;
            ProxyGetConfig(&cfg);
            _snprintf(info, sizeof(info),
                "ExtProxy hacks fly=%u bits=0x%X scale=%.2f cheat=%u "
                "RVAs: Send=0x%X CreatePkt=0x%X In=0x%X OpName=0x%X",
                cfg.flyhack, cfg.hacks, cfg.speed_scale, cfg.speed_cheat,
                g_off.ext_send, g_off.ext_create_packet,
                g_off.ext_process_incoming, g_off.ext_opcode_to_name);
            SendReply(pipe, kCmdExtNetInfo, info, (uint32_t)(strlen(info) + 1));
            break;
        }
        case kCmdSetSniff:
            if (g_hdr && hdr.len >= 4) {
                uint32_t on = 0;
                memcpy(&on, payload, 4);
                g_hdr->sniff_enabled = on ? 1u : 0u;
            }
            {
                uint32_t on = g_hdr ? g_hdr->sniff_enabled : 0;
                SendReply(pipe, kCmdSetSniff, &on, sizeof(on));
            }
            break;
        case kCmdSetOpcodeIgnore:
            /* payload: uint32 count + count * uint16 opcodes (replace list). */
            OpIgnoreInit();
            if (hdr.len >= 4) {
                uint32_t count = 0;
                memcpy(&count, payload, 4);
                if (count > 4096u)
                    count = 4096u;
                if (hdr.len >= 4u + count * 2u) {
                    OpIgnoreReplace((const uint16_t*)(payload + 4), count);
                } else if (count == 0) {
                    OpIgnoreReplace(NULL, 0);
                }
            }
            {
                uint32_t dropped = (uint32_t)g_sniff_dropped;
                SendReply(pipe, kCmdSetOpcodeIgnore, &dropped, sizeof(dropped));
            }
            break;
        case kCmdGetOpcodeIgnore:
            OpIgnoreInit();
            {
                uint16_t out[512];
                uint32_t n = 0, op;
                EnterCriticalSection(&g_op_ignore_cs);
                for (op = 0; op < kOpIgnoreBits && n < 512u; op++) {
                    if ((g_op_ignore[op >> 5] >> (op & 31u)) & 1u)
                        out[n++] = (uint16_t)op;
                }
                LeaveCriticalSection(&g_op_ignore_cs);
                {
                    uint8_t buf[4 + 512 * 2];
                    memcpy(buf, &n, 4);
                    if (n)
                        memcpy(buf + 4, out, n * 2u);
                    SendReply(pipe, kCmdGetOpcodeIgnore, buf, 4u + n * 2u);
                }
            }
            break;
        case kCmdSetChatCapture:
            if (hdr.len >= 4) {
                uint32_t on = 0;
                memcpy(&on, payload, 4);
                InterlockedExchange(&g_chat_capture, on ? 1 : 0);
            }
            {
                uint32_t on = (uint32_t)g_chat_capture;
                SendReply(pipe, kCmdSetChatCapture, &on, sizeof(on));
            }
            break;
        case kCmdReplay:
            if (hdr.len >= 4) {
                uint32_t n = 0;
                memcpy(&n, payload, 4);
                if (n && n <= PKT_REPLAY_MAX && hdr.len >= 4 + n && QueueReplay(payload + 4, n)) {
                    uint32_t ok = 1;
                    SendReply(pipe, kCmdReplay, &ok, sizeof(ok));
                } else {
                    uint32_t ok = 0;
                    InterlockedIncrement(&g_replay_fail);
                    SendReply(pipe, kCmdReplay, &ok, sizeof(ok));
                }
            }
            break;
        case kCmdInjectRecv:
            if (hdr.len >= 4) {
                uint32_t n = 0;
                uint32_t ok = 0;
                memcpy(&n, payload, 4);
                if (n && n <= PKT_REPLAY_MAX && hdr.len >= 4 + n
                    && QueueInjectIn(payload + 4, n))
                    ok = 1;
                SendReply(pipe, kCmdInjectRecv, &ok, sizeof(ok));
            }
            break;
        case kCmdBookmarkSet:
            if (hdr.len >= 12) {
                uint32_t slot = 0, dir = 0, n = 0, ok = 0;
                memcpy(&slot, payload, 4);
                memcpy(&dir, payload + 4, 4);
                memcpy(&n, payload + 8, 4);
                if (n && n <= PKT_REPLAY_MAX && hdr.len >= 12 + n
                    && ProxyBookmarkSet(slot, dir, payload + 12, n))
                    ok = 1;
                SendReply(pipe, kCmdBookmarkSet, &ok, sizeof(ok));
            }
            break;
        case kCmdBookmarkClear: {
            uint32_t slot = 0, ok = 0;
            if (hdr.len >= 4)
                memcpy(&slot, payload, 4);
            ok = ProxyBookmarkClear(slot) ? 1u : 0u;
            SendReply(pipe, kCmdBookmarkClear, &ok, sizeof(ok));
            break;
        }
        case kCmdBookmarkFire: {
            uint32_t slot = 0, ok = 0;
            if (hdr.len >= 4)
                memcpy(&slot, payload, 4);
            ok = ProxyBookmarkFire(slot) ? 1u : 0u;
            SendReply(pipe, kCmdBookmarkFire, &ok, sizeof(ok));
            break;
        }
        case kCmdBookmarkLoop: {
            uint32_t on = 0;
            if (hdr.len >= 4)
                memcpy(&on, payload, 4);
            ProxyBookmarkLoopSet(on ? 1u : 0u);
            on = ProxyBookmarkLoopGet();
            SendReply(pipe, kCmdBookmarkLoop, &on, sizeof(on));
            break;
        }
        case kCmdBookmarkBurst: {
            uint32_t n = (uint32_t)ProxyBookmarkBurst();
            SendReply(pipe, kCmdBookmarkBurst, &n, sizeof(n));
            break;
        }
        case kCmdOpcodeName:
            if (hdr.len >= 4) {
                uint32_t op = 0;
                char name[96];
                memcpy(&op, payload, 4);
                if (!ProxyOpcodeName(op, name, sizeof(name)))
                    _snprintf(name, sizeof(name), "0x%X", op);
                SendReply(pipe, kCmdOpcodeName, name, (uint32_t)(strlen(name) + 1));
            } else {
                const char* unk = "?";
                SendReply(pipe, kCmdOpcodeName, unk, 2);
            }
            break;
        case kCmdMapObjects: {
            char objects[8192];
            uint32_t n = ProxyGetMapObjects(objects, sizeof(objects));
            SendReply(pipe, kCmdMapObjects, objects, n);
            break;
        }
        case kCmdNavHeight: {
            uint8_t reply[8];
            uint32_t ok = 0;
            float z = 0.f;
            if (hdr.len >= 16) {
                uint32_t map;
                float x, y, zh;
                memcpy(&map, payload, 4);
                memcpy(&x, payload + 4, 4);
                memcpy(&y, payload + 8, 4);
                memcpy(&zh, payload + 12, 4);
                ok = ProxyNavHeight(map, x, y, zh, &z) ? 1u : 0u;
            }
            memcpy(reply, &ok, 4);
            memcpy(reply + 4, &z, 4);
            SendReply(pipe, kCmdNavHeight, reply, sizeof(reply));
            break;
        }
        case kCmdLineOfSight: {
            int32_t r = -1;
            if (hdr.len >= 12) {
                uint32_t map;
                uint64_t guid;
                memcpy(&map, payload, 4);
                memcpy(&guid, payload + 4, 8);
                r = ProxyLineOfSightGuid(guid, map);
            }
            SendReply(pipe, kCmdLineOfSight, &r, sizeof(r));
            break;
        }
        case kCmdTeleport: {

            uint32_t ok = 0;
            if (hdr.len >= 20) {
                float x, y, z, o;
                uint32_t flags;
                memcpy(&x, payload + 0, 4);
                memcpy(&y, payload + 4, 4);
                memcpy(&z, payload + 8, 4);
                memcpy(&o, payload + 12, 4);
                memcpy(&flags, payload + 16, 4);
                flags |= 0x2u | 0x4u;
                ok = ProxyTeleportSafeEx(x, y, z, o, flags, 100u) ? 1u : 0u;
            }
            SendReply(pipe, kCmdTeleport, &ok, sizeof(ok));
            break;
        }
        case kCmdTarget: {

            uint32_t ok = 0;
            if (hdr.len >= 8) {
                uint64_t guid;
                memcpy(&guid, payload, 8);
                ok = ProxyTargetGuid(guid) ? 1u : 0u;
            }
            SendReply(pipe, kCmdTarget, &ok, sizeof(ok));
            break;
        }
        case kCmdLoot: {

            uint32_t ok = 0;
            if (hdr.len >= 9) {
                uint64_t guid;
                uint8_t mode;
                memcpy(&guid, payload, 8);
                memcpy(&mode, payload + 8, 1);
                ok = ProxyLootGuid(guid, mode) ? 1u : 0u;
            }
            SendReply(pipe, kCmdLoot, &ok, sizeof(ok));
            break;
        }
        case kCmdFace: {

            uint32_t ok = 0;
            if (hdr.len >= 8) {
                float tx, ty;
                memcpy(&tx, payload + 0, 4);
                memcpy(&ty, payload + 4, 4);
                ok = ProxyFacePoint(tx, ty) ? 1u : 0u;
            }
            SendReply(pipe, kCmdFace, &ok, sizeof(ok));
            break;
        }
        case kCmdFaceUnit: {

            uint32_t ok = 0;
            if (hdr.len >= 8) {
                uint64_t guid;
                memcpy(&guid, payload, 8);
                ok = ProxyFaceUnit(guid) ? 1u : 0u;
            }
            SendReply(pipe, kCmdFaceUnit, &ok, sizeof(ok));
            break;
        }
        case kCmdLootAll: {

            uint32_t ok = 0;
            if (hdr.len >= 8) {
                uint64_t guid;
                memcpy(&guid, payload, 8);
                if (ProxyApproachGuid(guid))
                    ok = ProxyRightClickGuid(guid) ? 1u : 0u;
            }
            SendReply(pipe, kCmdLootAll, &ok, sizeof(ok));
            break;
        }
        case kCmdFacingInfo: {

            uint8_t reply[16];
            uint32_t po = ObjMgrPositionOffset();
            uint32_t fo = ObjMgrFacingOffset();
            uint32_t rz = ObjMgrFacingOffsetResolved() ? 1u : 0u;
            float facing = ProxyPlayerFacingCached();
            memcpy(reply + 0, &po, 4);
            memcpy(reply + 4, &fo, 4);
            memcpy(reply + 8, &rz, 4);
            memcpy(reply + 12, &facing, 4);
            SendReply(pipe, kCmdFacingInfo, reply, sizeof(reply));
            break;
        }
        case kCmdFindPath: {

            uint8_t reply[PKT_CMD_PAYLOAD_MAX];
            uint32_t n = 0;
            if (hdr.len >= 28) {
                uint32_t map;
                float sx, sy, sz, ex, ey, ez;
                uint32_t max_pts;
                memcpy(&map, payload + 0, 4);
                memcpy(&sx, payload + 4, 4);
                memcpy(&sy, payload + 8, 4);
                memcpy(&sz, payload + 12, 4);
                memcpy(&ex, payload + 16, 4);
                memcpy(&ey, payload + 20, 4);
                memcpy(&ez, payload + 24, 4);

                max_pts = (sizeof(reply) - 4u) / 12u;
                n = ProxyFindPath(sx, sy, sz, ex, ey, ez, map,
                                  (float*)(reply + 4), max_pts);
            }
            memcpy(reply + 0, &n, 4);
            SendReply(pipe, kCmdFindPath, reply, 4u + n * 12u);
            break;
        }
        case kCmdSetMove: {

            uint32_t ok = 0;
            if (hdr.len >= 5) {
                uint8_t op;
                float dur;
                memcpy(&op, payload + 0, 1);
                memcpy(&dur, payload + 1, 4);
                ok = ProxySetMove((uint32_t)op, dur) ? 1u : 0u;
            }
            SendReply(pipe, kCmdSetMove, &ok, sizeof(ok));
            break;
        }
        case kCmdClickToMove: {

            uint32_t ok = 0;
            if (hdr.len >= 12) {
                float x, y, z;
                memcpy(&x, payload + 0, 4);
                memcpy(&y, payload + 4, 4);
                memcpy(&z, payload + 8, 4);
                ok = ProxyClickToMove(x, y, z) ? 1u : 0u;
            }
            SendReply(pipe, kCmdClickToMove, &ok, sizeof(ok));
            break;
        }
        case kCmdMoveStatus: {

            uint8_t reply[24];
            int ready = 0, moving = 0;
            float tx = 0, ty = 0, tz = 0;
            uint32_t rem = 0, r32, m32;
            ProxyMoveStatus(&ready, &moving, &tx, &ty, &tz, &rem);
            r32 = (uint32_t)ready;
            m32 = (uint32_t)moving;
            memcpy(reply + 0, &r32, 4);
            memcpy(reply + 4, &m32, 4);
            memcpy(reply + 8, &tx, 4);
            memcpy(reply + 12, &ty, 4);
            memcpy(reply + 16, &tz, 4);
            memcpy(reply + 20, &rem, 4);
            SendReply(pipe, kCmdMoveStatus, reply, sizeof(reply));
            break;
        }
        case kCmdRunLua: {
            uint32_t ok = ProxyRequestRunLua((const char*)payload, hdr.len) ? 1u : 0u;
            SendReply(pipe, kCmdRunLua, &ok, sizeof(ok));
            break;
        }
        case kCmdSetEntitlements: {
            uint32_t flags = 0, max_inst = 1, count = 0, i;
            char names[ENT_MAX_NAMES][ENT_NAME_LEN];
            memset(names, 0, sizeof(names));
            if (hdr.len >= 12) {
                memcpy(&flags, payload, 4);
                memcpy(&max_inst, payload + 4, 4);
                memcpy(&count, payload + 8, 4);
                if (count > ENT_MAX_NAMES) count = ENT_MAX_NAMES;
                if (hdr.len >= 12u + count * (uint32_t)ENT_NAME_LEN) {
                    for (i = 0; i < count; i++) {
                        memcpy(names[i], payload + 12 + i * ENT_NAME_LEN, ENT_NAME_LEN);
                        names[i][ENT_NAME_LEN - 1] = 0;
                    }
                } else {
                    count = 0;
                }
            }
            EntitlementSet(flags, max_inst, names, count);
            {
                uint32_t ok = 1;
                SendReply(pipe, kCmdSetEntitlements, &ok, sizeof(ok));
            }
            break;
        }
        case kCmdSelfTest: {
            char diag[320];
            if (ProxyRunSelfTest(diag, sizeof(diag)))
                SendReply(pipe, kCmdSelfTest, diag, (uint32_t)strlen(diag));
            else
                SendReply(pipe, kCmdSelfTest, "self-test failed", 16);
            break;
        }
        case kCmdSubscribeShared: {
            /* Host → client: store the aggregated shared-world blob in our cache. */
            PktIpcSetSharedView(payload, hdr.len);
            uint32_t ok = 1;
            SendReply(pipe, kCmdSubscribeShared, &ok, sizeof(ok));
            break;
        }
        case kCmdSharedQuery: {
            /* Client → host poll: reply with a CS-protected snapshot of the cache. */
            uint32_t n = 0;
            SharedViewHeader localHdr;
            SharedViewObject localObjs[SHARED_VIEW_MAX_OBJECTS];
            uint32_t bytes;
            uint8_t* buf;
            memset(&localHdr, 0, sizeof(localHdr));
            if (g_shared_cs_ready) {
                EnterCriticalSection(&g_shared_cs);
                localHdr = g_shared_hdr;
                n = g_shared_count;
                if (n > SHARED_VIEW_MAX_OBJECTS) n = SHARED_VIEW_MAX_OBJECTS;
                if (n)
                    memcpy(localObjs, g_shared_objs, n * sizeof(SharedViewObject));
                LeaveCriticalSection(&g_shared_cs);
            }
            if (localHdr.magic != SHARED_VIEW_MAGIC) {
                localHdr.magic = SHARED_VIEW_MAGIC;
                localHdr.count = 0;
                localHdr.owner_pid = g_owner_pid;
                n = 0;
            }
            bytes = (uint32_t)sizeof(SharedViewHeader) + n * (uint32_t)sizeof(SharedViewObject);
            if (bytes > PKT_CMD_PAYLOAD_MAX) {
                n = 0;
                bytes = (uint32_t)sizeof(SharedViewHeader);
            }
            buf = (uint8_t*)LocalAlloc(LMEM_FIXED, bytes);
            if (buf) {
                memcpy(buf, &localHdr, sizeof(SharedViewHeader));
                if (n && bytes > sizeof(SharedViewHeader))
                    memcpy(buf + sizeof(SharedViewHeader), localObjs, n * sizeof(SharedViewObject));
                SendReply(pipe, kCmdSharedQuery, buf, bytes);
                LocalFree(buf);
            } else {
                SendReply(pipe, kCmdSharedQuery, &(uint32_t){0}, 4);
            }
            break;
        }
        default:
            break;
        }
    }
}

static SECURITY_ATTRIBUTES* EveryoneSa(SECURITY_ATTRIBUTES* sa, SECURITY_DESCRIPTOR* sd)
{
    InitializeSecurityDescriptor(sd, SECURITY_DESCRIPTOR_REVISION);
    SetSecurityDescriptorDacl(sd, TRUE, NULL, FALSE);
    sa->nLength = sizeof(*sa);
    sa->lpSecurityDescriptor = sd;
    sa->bInheritHandle = FALSE;
    return sa;
}

static DWORD WINAPI ClientThread(LPVOID param)
{
    HANDLE pipe = (HANDLE)param;
    /* Ephemeral PingOnce / reconnect storms used to spam connect/disconnect every tick.
     * Log at most once per 15s so login/attach stays readable. */
    {
        static DWORD s_last_conn_log;
        DWORD now = GetTickCount();
        if (!s_last_conn_log || (now - s_last_conn_log) >= 15000u) {
            s_last_conn_log = now;
            LogIpc("client connected");
        }
    }
    HandleClient(pipe);
    DisconnectNamedPipe(pipe);
    CloseHandle(pipe);
    return 0;
}

static void WritePidFile(uint32_t pid)
{
    char path[MAX_PATH];
    FILE* f;
    char* slash;
    HMODULE self = NULL;
    DWORD n;
    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        (LPCSTR)&WritePidFile, &self);
    n = GetModuleFileNameA(self, path, MAX_PATH);
    if (!n || n >= MAX_PATH)
        return;
    slash = strrchr(path, '\\');
    if (!slash)
        return;
    lstrcpyA(slash + 1, PKT_PID_FILE_NAME);
    f = fopen(path, "w");
    if (!f)
        return;
    fprintf(f, "%u\n", (unsigned)pid);
    fclose(f);
}

static DWORD WINAPI PipeThread(LPVOID param)
{
    SECURITY_ATTRIBUTES sa;
    SECURITY_DESCRIPTOR sd;
    char msg[160];
    (void)param;
    _snprintf(msg, sizeof(msg), "pipe thread start name=%s", g_pipe_name);
    LogIpc(msg);
    while (!g_ipc_stop) {
        HANDLE pipe = CreateNamedPipeA(
            g_pipe_name,
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            4,
            262144,
            262144,
            0,
            EveryoneSa(&sa, &sd));
        if (pipe == INVALID_HANDLE_VALUE) {
            Sleep(200);
            continue;
        }
        if (ConnectNamedPipe(pipe, NULL) || GetLastError() == ERROR_PIPE_CONNECTED) {
            HANDLE t = CreateThread(NULL, 0, ClientThread, (LPVOID)pipe, 0, NULL);
            if (t)
                CloseHandle(t);
            else {
                HandleClient(pipe);
                DisconnectNamedPipe(pipe);
                CloseHandle(pipe);
            }
        } else {
            CloseHandle(pipe);
        }
    }
    return 0;
}

void PktIpcStart(void)
{
    SIZE_T bytes;
    SECURITY_ATTRIBUTES sa;
    SECURITY_DESCRIPTOR sd;
    char msg[192];
    OpIgnoreInit();
    InitializeCriticalSection(&g_replay_cs);
    InitializeCriticalSection(&g_inj_in_cs);
    g_inj_in_cs_ready = 1;
    InitializeCriticalSection(&g_shared_cs);
    g_shared_cs_ready = 1;
    memset(&g_shared_hdr, 0, sizeof(g_shared_hdr));
    g_shared_hdr.magic = SHARED_VIEW_MAGIC;
    if (g_cfg_instance_id != 0)
        g_shared_hdr.this_instance = g_cfg_instance_id;
    g_shared_count = 0;
    g_owner_pid = GetCurrentProcessId();
    _snprintf(g_pipe_name, sizeof(g_pipe_name), "%s_%u", PKT_PIPE_NAME_BASE, (unsigned)g_owner_pid);
    _snprintf(g_ring_name, sizeof(g_ring_name), "%s_%u", PKT_RING_NAME_BASE, (unsigned)g_owner_pid);
    WritePidFile(g_owner_pid);
    _snprintf(msg, sizeof(msg), "ipc owner pid=%u pipe=%s", (unsigned)g_owner_pid, g_pipe_name);
    LogIpc(msg);

    bytes = sizeof(PktRingHeader) + (SIZE_T)PKT_RING_SLOTS * sizeof(PktRingSlot);
    g_map = CreateFileMappingA(INVALID_HANDLE_VALUE, EveryoneSa(&sa, &sd), PAGE_READWRITE, 0, (DWORD)bytes, g_ring_name);
    if (!g_map) {
        LogIpc("CreateFileMapping failed");
    } else {
        g_hdr = (PktRingHeader*)MapViewOfFile(g_map, FILE_MAP_ALL_ACCESS, 0, 0, bytes);
        if (!g_hdr) {
            LogIpc("MapViewOfFile failed — pipe-only mode");
            CloseHandle(g_map);
            g_map = NULL;
        } else {
            memset(g_hdr, 0, bytes);
            g_hdr->magic = PKT_IPC_MAGIC;
            g_hdr->slot_count = PKT_RING_SLOTS;
            g_hdr->slot_bytes = (uint32_t)sizeof(PktRingSlot);
            g_hdr->write_seq = 0;
            g_hdr->read_seq = 0;
            g_hdr->drop_count = 0;

            g_hdr->sniff_enabled = 0;
            g_hdr->owner_pid = g_owner_pid;
            g_slots = (PktRingSlot*)(g_hdr + 1);
            LogIpc("ring ready");
        }
    }
    g_pipe_thread = CreateThread(NULL, 0, PipeThread, NULL, 0, NULL);
    LogIpc(g_hdr ? "ring+pipe ready" : "pipe ready (no ring)");
}

uint32_t PktIpcOwnerPid(void)
{
    return g_owner_pid;
}

/* Host pushes an aggregated SharedView blob down to this client. We copy it into
 * the local cache (header + up to SHARED_VIEW_MAX_OBJECTS records) so the GmShared*
 * Lua natives can read it without a pipe round-trip. */
void PktIpcSetSharedView(const uint8_t* data, uint32_t size)
{
    if (!g_shared_cs_ready) return;
    if (!data || size < sizeof(SharedViewHeader)) return;
    SharedViewHeader h;
    memcpy(&h, data, sizeof(h));
    if (h.magic != SHARED_VIEW_MAGIC) return;
    uint32_t avail = (size - sizeof(SharedViewHeader)) / sizeof(SharedViewObject);
    uint32_t n = h.count;
    if (n > avail) n = avail;
    if (n > SHARED_VIEW_MAX_OBJECTS) n = SHARED_VIEW_MAX_OBJECTS;
    EnterCriticalSection(&g_shared_cs);
    g_shared_hdr = h;
    g_shared_count = n;
    if (n) memcpy(g_shared_objs, data + sizeof(SharedViewHeader), n * sizeof(SharedViewObject));
    LeaveCriticalSection(&g_shared_cs);
}

const SharedViewHeader* PktIpcSharedView(uint32_t* out_count)
{
    if (!g_shared_cs_ready) { if (out_count) *out_count = 0; return NULL; }
    EnterCriticalSection(&g_shared_cs);
    if (out_count) *out_count = g_shared_count;
    const SharedViewHeader* h = (g_shared_hdr.magic == SHARED_VIEW_MAGIC) ? &g_shared_hdr : NULL;
    LeaveCriticalSection(&g_shared_cs);
    return h;
}

const SharedViewObject* PktIpcSharedObjects(uint32_t* out_count)
{
    if (!g_shared_cs_ready) { if (out_count) *out_count = 0; return NULL; }
    EnterCriticalSection(&g_shared_cs);
    if (out_count) *out_count = g_shared_count;
    const SharedViewObject* arr = (g_shared_count > 0) ? g_shared_objs : NULL;
    LeaveCriticalSection(&g_shared_cs);
    return arr;
}

void PktIpcSetCfgInstanceId(uint32_t id)
{
    g_cfg_instance_id = id;
    if (g_shared_cs_ready && id != 0) {
        EnterCriticalSection(&g_shared_cs);
        if (g_shared_hdr.this_instance == 0)
            g_shared_hdr.this_instance = id;
        LeaveCriticalSection(&g_shared_cs);
    }
}

uint32_t PktIpcThisInstance(void)
{
    if (!g_shared_cs_ready) return g_cfg_instance_id;
    EnterCriticalSection(&g_shared_cs);
    uint32_t id = g_shared_hdr.this_instance;
    LeaveCriticalSection(&g_shared_cs);
    return id ? id : g_cfg_instance_id;
}

uint32_t PktIpcTotalInstances(void)
{
    if (!g_shared_cs_ready) return 1;
    EnterCriticalSection(&g_shared_cs);
    uint32_t n = g_shared_hdr.total_instances;
    LeaveCriticalSection(&g_shared_cs);
    return n ? n : 1;
}

void PktIpcStop(void)
{
    InterlockedExchange(&g_ipc_stop, 1);
    {
        HANDLE h = CreateFileA(g_pipe_name[0] ? g_pipe_name : PKT_PIPE_NAME_BASE,
            GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (h != INVALID_HANDLE_VALUE)
            CloseHandle(h);
    }
    if (g_pipe_thread) {
        WaitForSingleObject(g_pipe_thread, 2000);
        CloseHandle(g_pipe_thread);
        g_pipe_thread = NULL;
    }
    if (g_hdr) {
        UnmapViewOfFile(g_hdr);
        g_hdr = NULL;
        g_slots = NULL;
    }
    if (g_map) {
        CloseHandle(g_map);
        g_map = NULL;
    }
    DeleteCriticalSection(&g_replay_cs);
    if (g_inj_in_cs_ready) {
        g_inj_in_cs_ready = 0;
        DeleteCriticalSection(&g_inj_in_cs);
    }
}

int PktIpcReplayPending(void)
{
    return g_replay_pending != 0;
}

void PktIpcMarkReplayOk(void)
{
    InterlockedIncrement(&g_replay_ok);
}

void PktIpcMarkReplayFail(void)
{
    InterlockedIncrement(&g_replay_fail);
}
