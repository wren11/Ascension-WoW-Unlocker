#pragma once

#include <stddef.h>
#include <stdint.h>
#include "MovementConfig.h"

#define PKT_IPC_MAGIC 0x504B5431u
#define PKT_CMD_MAGIC 0x444D4341u

#define PKT_RING_NAME_BASE "Local\\AscensionExtProxyRingV5"
#define PKT_PIPE_NAME_BASE "\\\\.\\pipe\\AscensionExtProxyV5"
#define PKT_PID_FILE_NAME "ExtProxy64.pid"
#define PKT_RING_SLOTS 2048u
#define PKT_RING_MAX 2048u
#define PKT_REPLAY_MAX 2048u
#define PKT_BOOKMARK_SLOTS 16u

#define PKT_CMD_PAYLOAD_MAX 24576u

enum PktDir {
    kPktDirOut = 0,
    kPktDirIn = 1,
    kPktDirReplay = 2,
};

enum PktCmd {
    kCmdPing = 1,
    kCmdGetConfig = 2,
    kCmdSetConfig = 3,
    kCmdSetSniff = 4,
    kCmdReplay = 5,
    kCmdGetStatus = 6,
    kCmdRunLua = 12,
    kCmdSelfTest = 13,
    kCmdOpcodeName = 17,
    kCmdExtNetInfo = 18,
    kCmdSetSpeed = 19,
    kCmdMapObjects = 30,
    kCmdNavHeight = 31,
    kCmdLineOfSight = 32,

    kCmdTeleport = 33,
    kCmdTarget   = 34,
    kCmdLoot     = 35,
    kCmdFace     = 36,
    kCmdFindPath = 37,
    kCmdSetMove  = 38,
    kCmdClickToMove = 39,
    kCmdMoveStatus = 40,
    kCmdSetHacks = 41,
    kCmdFindOpcode = 42,
    kCmdSetAntiAfk = 43,
    kCmdFaceUnit = 44,
    kCmdFacingInfo = 45,
    kCmdLootAll = 46,
    kCmdInjectRecv = 47,
    kCmdBookmarkSet = 48,
    kCmdBookmarkClear = 49,
    kCmdBookmarkFire = 50,
    kCmdBookmarkLoop = 51,
    kCmdBookmarkBurst = 52,

    /* Opcode ignore filter + lightweight chat capture (sniff ring). */
    kCmdSetOpcodeIgnore = 53,   /* replace ignore bitset from u16[] list */
    kCmdGetOpcodeIgnore = 54,   /* returns count + ignored opcodes (capped) */
    kCmdSetChatCapture = 55,    /* uint32 on/off — capture chat even when sniff off */

    /* Multi-instance shared world model (host pushes aggregated view DOWN to client). */
    kCmdSubscribeShared = 60,
    kCmdSharedQuery     = 61,   /* client -> host: ask for fresh shared view (poll) */

    /* SoftRealm Core gate: allowed lowercase names + instance cap. */
    kCmdSetEntitlements = 72,
};

/* Shared-world view wire format (host → client via kCmdSubscribeShared).
 * A compact object list with source-instance tags so each client can read what
 * every other connected instance sees. Mirrored on the C# host (SharedStateManager). */
#define SHARED_VIEW_MAGIC 0x53485631u   /* "1VHS" */
#define SHARED_VIEW_MAX_OBJECTS 256
#pragma pack(push, 1)
typedef struct SharedViewObject {
    uint64_t guid;
    uint32_t entry;
    uint32_t type_mask;
    int32_t  health;
    int32_t  max_health;
    uint32_t level;
    int32_t  faction;
    float    x, y, z, facing;
    uint32_t src_instance;     /* which GMToolBox instance published this */
} SharedViewObject;
typedef struct SharedViewHeader {
    uint32_t magic;
    uint32_t this_instance;    /* the instance id the host assigned to THIS client */
    uint32_t total_instances;
    uint32_t owner_pid;        /* this client's own pid (echo) */
    uint32_t count;            /* number of SharedViewObject records following */
} SharedViewHeader;
#pragma pack(pop)

#pragma pack(push, 1)
typedef struct PktRingHeader {
    uint32_t magic;
    uint32_t slot_count;
    uint32_t slot_bytes;
    volatile uint32_t write_seq;
    volatile uint32_t sniff_enabled;
    volatile uint32_t drop_count;
    uint32_t owner_pid;
    /* Host publishes its drain cursor so the writer can count overwrites. */
    volatile uint32_t read_seq;
    uint32_t reserved[6];
} PktRingHeader;

typedef struct PktRingSlot {
    uint32_t seq;
    uint32_t tick;
    uint8_t dir;
    uint8_t pad0;
    uint16_t size;
    uint32_t opcode;
    uint8_t data[PKT_RING_MAX];
} PktRingSlot;

typedef struct PktCmdHeader {
    uint32_t magic;
    uint32_t cmd;
    uint32_t len;
} PktCmdHeader;
#pragma pack(pop)

void PktIpcStart(void);
void PktIpcStop(void);
/* Returns 1 if a ring slot was written. Gates sniff/chat/ignore internally. */
int PktIpcSniff(uint8_t dir, uint32_t opcode, const uint8_t* data, uint32_t size);
/* True when full sniff or chat-capture wants ring traffic. */
int PktIpcWantCapture(void);
int PktIpcTakeReplay(uint8_t* out, uint32_t* inout_size);
int PktIpcSniffEnabled(void);
int PktIpcReplayPending(void);
void PktIpcMarkReplayOk(void);
void PktIpcMarkReplayFail(void);
uint32_t PktIpcOwnerPid(void);
int PktIpcTakeInjectIn(uint8_t* out, uint32_t* inout_size);
int PktIpcInjectInPending(void);
int PktIpcQueueReplay(const uint8_t* data, uint32_t size);

int ProxyQueueInjectIncoming(const uint8_t* data, uint32_t size);
int ProxyBookmarkSet(uint32_t slot, uint32_t dir, const uint8_t* data, uint32_t size);
int ProxyBookmarkClear(uint32_t slot);
int ProxyBookmarkFire(uint32_t slot);
void ProxyBookmarkLoopSet(uint32_t on);
uint32_t ProxyBookmarkLoopGet(void);
int ProxyBookmarkBurst(void);
void ProxyBookmarkLoopPulse(void);
void ProxyDrainInjectIncoming(void);

void ProxyLogLine(const char* msg);
int ProxyRequestRunLua(const char* script, uint32_t len);
void ProxyWakeUiForInject(void);
void ProxyWakeUiForInjectSync(void);
int ProxyRunSelfTest(char* out, uint32_t out_cap);
int ProxyOpcodeName(uint32_t opcode, char* out, uint32_t out_cap);
uint32_t ProxyGetMapObjects(char* out, uint32_t out_cap);
int ProxyNavHeight(uint32_t map, float x, float y, float z_hint, float* out_z);
int ProxyLineOfSightGuid(uint64_t target_guid, uint32_t map);

int ProxyTeleportSafe(float x, float y, float z, float o, uint32_t flags);
int ProxyTeleportSafeEx(float x, float y, float z, float o, uint32_t flags, uint32_t lock_ms);
int ProxyTeleportLoadEx(float x, float y, float z, float o, uint32_t flags, uint32_t lock_ms);
int ProxyTeleportAnywhere(float x, float y, float z, float o, uint32_t map_id,
                          uint32_t flags, uint32_t lock_ms);
int ProxyTeleportNg(float x, float y, float z, float o, uint32_t map_id,
                    uint32_t mode, uint32_t flags, uint32_t lock_ms);
int ProxyValidateTeleportDest(float x, float y, float z, float o,
                              float* out_ground, char* err, size_t errn);
void ProxyTpLock(float x, float y, float z, float o, uint32_t duration_ms, float radius_yd);
void ProxyTpUnlock(void);
int ProxyTpLockActive(void);
int ProxyFreeMove(void);
int ProxyTpPulse(void);
int ProxyTpPulseEx(uint32_t lock_ms);
uint32_t ProxyDefaultTpLockMs(void);
int ProxyTargetGuid(uint64_t guid);
int ProxyTargetUnit(const char* token);
int ProxyInteractUnit(const char* token);
int ProxyTargetGuidUnit(uint64_t guid);
int ProxyLookAt(float tx, float ty, float tz);
int ProxyRightClickGuid(uint64_t guid);
int ProxyInteractGuidNative(uint64_t guid);
uint64_t ProxyTargetNearestNative(int mode, int reverse);
/* allow_tagged: non-zero = do not reject dyn tapped-by-other (HuntingBot ignoreTaggedMobs). */
uint64_t ProxyTargetNearestValid(float radius, int mode, int allow_tagged);
uint32_t ProxyLootNearestNative(float radius);
uint32_t ProxyCollectLootable(float radius, uint64_t* out, uint32_t max);
int ProxyLootSessionByGuid(uint64_t guid);
int ProxyLootOpenGuid(uint64_t guid);
int ProxyLootTakeOpen(uint64_t guid);
int ProxyIsSkinnable(uint64_t guid);
uint32_t ProxyCollectSkinnable(float radius, uint64_t* out, uint32_t max);
int ProxySkinStartGuid(uint64_t guid);
uint32_t ProxySkinNearestNative(float radius);

int ProxyLootGuid(uint64_t guid, uint32_t mode);
/* Teleport to the object's live OM XYZ (skip-ground), compute Z from OM
 * (nav floor only if OM Z is insane), then face it. Returns 1 only when
 * live 3D distance is inside interact (~5.5 yd). */
int ProxyApproachGuid(uint64_t guid);
int ProxyFacePoint(float tx, float ty);
int ProxyFaceAngle(float radians);
int ProxyFaceUnit(uint64_t guid);
int ProxyFaceTarget(void);
uint32_t ProxyFindPath(float sx, float sy, float sz, float ex, float ey, float ez, uint32_t map,
                       float* out_xyz, uint32_t max_points);
int ProxySetMove(uint32_t op, float duration_s);

int ProxyClickToMove(float x, float y, float z);
int ProxyClickToMoveStop(void);

int ProxyMoveStatus(int* ready, int* moving, float* tx, float* ty, float* tz, uint32_t* remain_ms);

void ProxyGetConfig(MovementConfig* out);
int ProxySetConfig(const MovementConfig* in);
float ProxySetSpeed(float scale, uint32_t speed_cheat);
uint32_t ProxySetHacks(uint32_t hacks, uint32_t flyhack);
int ProxyFindOpcode(const char* name, uint32_t* out_op);

#pragma pack(push, 1)
typedef struct AntiAfkStatus {
    uint32_t enabled;
    uint32_t interval_ms;
    uint32_t pulse_count;
    uint32_t last_pulse_ms;
    uint32_t patched;
    uint32_t have_lha;
} AntiAfkStatus;
#pragma pack(pop)
void ProxyGetAntiAfk(AntiAfkStatus* out);
void ProxySetAntiAfk(uint32_t enabled, uint32_t interval_ms);

/* Multi-instance shared world model — host pushes a SharedView blob down to this
 * client (kCmdSubscribeShared); the Lua natives GmSharedObjects/GmSharedPlayers/
 * GmGetInstance/GmSharedNearby read it from this cache. */
void PktIpcSetSharedView(const uint8_t* data, uint32_t size);
const SharedViewHeader* PktIpcSharedView(uint32_t* out_count);
const SharedViewObject* PktIpcSharedObjects(uint32_t* out_count);
uint32_t PktIpcThisInstance(void);
uint32_t PktIpcTotalInstances(void);
/* Optional bootstrap from ExtProxy.cfg instance_id=N (used until host SubscribeShared). */
void PktIpcSetCfgInstanceId(uint32_t id);

/* Account/Core name gate — remember CHAR_ENUM names. Never drop PLAYER_LOGIN
 * (that freezes the client on the load screen). Addons gate after world entry. */
#define ENT_FLAG_HAS_ACCOUNT 1u
#define ENT_FLAG_GATE_ON     2u
#define ENT_MAX_NAMES 16
#define ENT_NAME_LEN 25
void EntitlementSet(uint32_t flags, uint32_t max_instances, const char names[][ENT_NAME_LEN], uint32_t count);
void EntitlementOnPacket(uint8_t dir, uint32_t opcode, const uint8_t* data, uint32_t size);
int EntitlementShouldDropSend(uint32_t opcode, const uint8_t* data, uint32_t size);
int EntitlementIsGateOpcode(uint32_t opcode);
int EntitlementIsPlayerLogin(uint32_t opcode);
