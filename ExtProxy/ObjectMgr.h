#pragma once

#include <stdint.h>

enum {

    kCurMgrTlsIndexRva = 0x009439BCu,


    kObjMgrLinkOffsetOff = 0xA4u,
    kObjMgrFirstObjOff = 0xACu,


    kObjDescriptorsOff = 0x08u,
    kObjGuidOff = 0x30u,
};

enum {
    kTypeMaskObject = 0x01u,
    kTypeMaskItem = 0x02u,
    kTypeMaskContainer = 0x04u,
    kTypeMaskUnit = 0x08u,
    kTypeMaskPlayer = 0x10u,
    kTypeMaskGameObject = 0x20u,
    kTypeMaskDynamicObject = 0x40u,
    kTypeMaskCorpse = 0x80u,
};

#pragma pack(push, 1)
typedef struct ObjMgrUnit {
    uint64_t guid;
    uint64_t target_guid;
    uint32_t entry;
    uint32_t type_mask;
    uint32_t health;
    uint32_t max_health;
    uint32_t level;
    uint32_t faction;
    uint32_t unit_flags;
    uint32_t dyn_flags;
    float x, y, z, facing;
    float dist;
} ObjMgrUnit;

#define OBJ_SNAPSHOT_MAGIC 0x334A424Fu /* v3: +owner_pid */

enum {
    kUnitDynLootable          = 0x01u,
    kUnitDynTrackUnit         = 0x02u,
    kUnitDynTapped            = 0x04u,
    kUnitDynTappedByPlayer    = 0x08u,
    kUnitDynSpecialInfo       = 0x10u,
    kUnitDynDead              = 0x20u,
    kUnitDynTappedByAllThreat = 0x80u,

    /* WotLK UNIT_FIELD_FLAGS — used by combat-target filters. */
    kUnitFlagNonAttackable    = 0x00000002u,
    kUnitFlagNotAttackable1   = 0x00000080u,
    kUnitFlagImmuneToPc       = 0x00000100u,
    kUnitFlagNotSelectable    = 0x02000000u,
    kUnitFlagSkinnable        = 0x04000000u,
    kGoFlagInUse              = 0x01u,
    kGoFlagLocked             = 0x02u,
    kGoFlagNoInteract         = 0x10u,
    /* GAMEOBJECT_DYNAMIC low word (WotLK 3.3.5a). */
    kGoDynActivate            = 0x01u,
    kGoDynAnimate             = 0x02u,
    kGoDynNoInteract          = 0x04u,
    kGoDynSparkle             = 0x08u,
};

typedef struct ObjMgrSnapshotHeader {
    uint32_t magic;
    uint64_t player_guid;
    float player_x, player_y, player_z;
    uint32_t pos_off;
    uint32_t count;
    uint32_t owner_pid; /* process id of the publishing ExtProxy instance */
} ObjMgrSnapshotHeader;
#pragma pack(pop)

/* Live player row (full OM walk, not the mixed unit/GO snapshot). */
enum { kObjMgrNameMax = 47 };
typedef struct ObjMgrNamed {
    ObjMgrUnit unit;
    char name[kObjMgrNameMax + 1];
} ObjMgrNamed;

void ObjMgrInit(uint8_t* ascension_base);

int ObjMgrReady(void);

void* ObjMgrCurrent(void);

uint64_t ObjMgrPlayerGuid(void);
void* ObjMgrPlayerObject(void);

uint32_t ObjMgrField32(void* obj, uint32_t index);
uint64_t ObjMgrField64(void* obj, uint32_t index);

uint32_t ObjMgrUnitFlags(void* obj);

uint32_t ObjMgrDynFlags(void* obj);

uint64_t ObjMgrObjectGuid(void* obj);
uint32_t ObjMgrTypeMask(void* obj);

void* ObjMgrFindByGuid(uint64_t guid);

/* Live object-list walk (not the 200ms snapshot). XYZ/dist are the
 * current CGObject position vs the live player. Returns 0 if the GUID
 * is not in the object manager right now. */
int ObjMgrLiveByGuid(uint64_t guid, ObjMgrUnit* out);

int ObjMgrPosition(void* obj, float* x, float* y, float* z, float* facing);

uint32_t ObjMgrCalibrate(float known_x, float known_y, float known_z);
uint32_t ObjMgrPositionOffset(void);

int ObjMgrSetFacing(void* obj, float facing);

uint32_t ObjMgrCalibrateFacing(float known_facing);

uint32_t ObjMgrFacingOffset(void);

int ObjMgrFacingOffsetResolved(void);

int ObjMgrSetPosition(void* obj, float x, float y, float z, float facing);

float ObjMgrReadFloatAt(void* obj, int byte_off);

int ObjMgrReadClientFacing(void* obj, float* out_facing);
/* Calibrated offset only — no VirtualQuery scan. Safe on Present. */
int ObjMgrPeekFacing(void* obj, float* out_facing);

uint32_t ObjMgrCacheCount(void);
int ObjMgrCacheGet(uint32_t index, ObjMgrUnit* out);
int ObjMgrCacheFind(uint64_t guid, ObjMgrUnit* out);

uint32_t ObjMgrCollectUnits(ObjMgrUnit* out, uint32_t max, float radius);

/* Walk the live object list for players (type-mask and/or player GUID high).
 * max_dist in yards (0 → 200). Copies display names when the probe hits.
 * Does not use the mixed unit/GO snapshot. */
uint32_t ObjMgrCollectPlayers(ObjMgrNamed* out, uint32_t max, float max_dist);

void ObjMgrPump(void);

void ObjMgrInvalidate(void);

uint32_t ObjMgrCacheAgeMs(void);

/* Monotonic generation — increments on each successful ObjMgrPump publish.
 * Lua uses this to detect rehydrate vs reuse of the same snapshot. */
uint32_t ObjMgrCacheGen(void);

uint32_t ObjMgrSnapshot(void* out, uint32_t out_cap);

/* ---- Extended field accessors (GmApiExt) ----
 * Field indices are the WotLK 3.3.5a (build 12340) CGUnit/CGObject update-field
 * table offsets. They are returned as descriptor indices and read through the
 * existing ObjMgrField32/Field64 path so the same SafeRead/IsValid guards apply.
 */
enum {
    /* CGUnit descriptor indices. */
    kFieldUnitCreatedBy       = 0xC8u,  /* UNIT_FIELD_CREATEDBY (guid, lo+hi) */
    kFieldUnitSummonedBy      = 0xCAu,  /* UNIT_FIELD_SUMMONEDBY (guid, lo+hi) */
    kFieldUnitCharmedBy       = 0xC6u,  /* UNIT_FIELD_CHARMEDBY (guid, lo+hi) */
    kFieldUnitChannelObject   = 0xCCu,  /* UNIT_FIELD_CHANNEL_OBJECT (guid) */
    kFieldUnitBoundingRadius  = 0xC2u,  /* UNIT_FIELD_BOUNDINGRADIUS (float) */
    kFieldUnitCombatReach     = 0xC3u,  /* UNIT_FIELD_COMBATREACH (float) */
    kFieldUnitFlags2          = 0xC4u,  /* UNIT_FIELD_FLAGS_2 */
    kFieldUnitNpcFlags        = 0x66u,  /* UNIT_NPC_FLAGS */
    kFieldUnitBytes0          = 0x6Au,  /* race|class|gender|powerType */
    kFieldUnitBytes1          = 0xB4u,  /* standState|petLoyalty|petTraining|shapeshiftForm */
    kFieldUnitBytes2          = 0xB6u,  /* sheath|misc|petFlags|petSpecialization */
    kFieldUnitBaseAttackTime  = 0xB8u,
    kFieldUnitMaxHealth       = 0x18u,  /* fallback descriptor index */
    kFieldUnitHealth          = 0x1Au,  /* fallback descriptor index */
    kFieldUnitLevelDesc       = 0x36u,  /* UNIT_FIELD_LEVEL (descriptor) */
    kFieldUnitFactionDesc     = 0x3Cu,  /* UNIT_FIELD_FACTIONTEMPLATE */

    /* CGPlayer: MaNGOS/CMaNGOS 3.3.5 — UNIT_END=0x94, PLAYER_FLAGS=UNIT_END+2. */
    kFieldPlayerFlags         = 0x96u,
    kPlayerFlagGm             = 0x00000008u,
    kPlayerFlagDeveloper      = 0x00008000u,
    /* Bits actually used by WotLK PLAYER_FLAGS. Anything outside is a bad read. */
    kPlayerFlagsKnownMask     = 0x07FFFFFFu,

    /* CGPlayer creature-of fields (mirrored on units via descriptor). */
    kFieldUnitDisplayId       = 0x70u,  /* UNIT_FIELD_DISPLAYID */
    kFieldUnitNativeDisplayId = 0x72u,  /* UNIT_FIELD_NATIVEDISPLAYID */
    kFieldUnitMountDisplayId  = 0x74u,  /* UNIT_FIELD_MOUNTDISPLAYID */

    /* CGGameObject — Ascension 3.3.5a OBJECT_END=6 PLUS OBJECT_FIELD_CREATED_BY
     * (size 2) before the stock GO table. GAMEOBJECT_END=0x12.
     * BYTES_1 @ 0x11: state | type<<8 | artKit<<16 | anim<<24
     * Verified s_objectTypeEnd[GAMEOBJECT]=0x12, GetPosition RVA 0x306500. */
    kFieldGoCreatedBy         = 0x06u,  /* OBJECT_FIELD_CREATED_BY (guid) */
    kFieldGoDisplayId         = 0x08u,  /* GAMEOBJECT_DISPLAYID */
    kFieldGoFlags             = 0x09u,  /* GAMEOBJECT_FLAGS */
    kFieldGoDynamic           = 0x0Eu,  /* GAMEOBJECT_DYNAMIC */
    kFieldGoFaction           = 0x0Fu,  /* GAMEOBJECT_FACTION */
    kFieldGoLevel             = 0x10u,  /* GAMEOBJECT_LEVEL */
    kFieldGoBytes1            = 0x11u,  /* GAMEOBJECT_BYTES_1 */

    /* CGUnit non-descriptor (struct) offsets. */
    kUnitMovementFlagsOff     = 0xD8u,  /* UnitMovementField flags dword (after pos block) */
    kUnitAnimOff              = 0xECu,  /* animation stand state cache */
};

/* Returns a heap-stable pointer to a C string name for the object, or NULL.
 * The string is only valid for the duration of the call (do not free). */
const char* ObjMgrObjectName(void* obj);

/* Reads a 32-bit descriptor field by index with the standard guards. */
float ObjMgrObjectFloatField(void* obj, uint32_t index);

/* Returns the GUID (lo+hi pair) stored at descriptor index `index` (the lo). */
uint64_t ObjMgrObjectGuidField(void* obj, uint32_t index);

/* Convenience typed accessors. */
uint32_t ObjMgrPlayerFlags(void* obj);
int      ObjMgrPlayerFlagsLookSane(uint32_t flags);
int      ObjMgrPlayerIsGmStaff(uint32_t flags);

uint32_t ObjMgrNpcFlags(void* obj);
uint32_t ObjMgrUnitFlags2(void* obj);
float    ObjMgrBoundingRadius(void* obj);
float    ObjMgrCombatReach(void* obj);
uint64_t ObjMgrCreatedBy(void* obj);
uint64_t ObjMgrSummonedBy(void* obj);
uint64_t ObjMgrCharmedBy(void* obj);
uint32_t ObjMgrUnitBytes0(void* obj);
uint32_t ObjMgrUnitBytes1(void* obj);
uint32_t ObjMgrUnitBytes2(void* obj);
uint32_t ObjMgrCreatureFamily(void* obj);   /* race/class extraction fallback */
uint32_t ObjMgrDisplayId(void* obj);
uint32_t ObjMgrGoType(void* obj);            /* GAMEOBJECT_BYTES_1 >> 8 & 0xFF */
uint32_t ObjMgrGoState(void* obj);           /* GAMEOBJECT_BYTES_1 & 0xFF */
uint32_t ObjMgrGoAnimProgress(void* obj);
uint32_t ObjMgrGoFlags(void* obj);           /* GAMEOBJECT_FLAGS */
uint32_t ObjMgrGoDynamic(void* obj);         /* GAMEOBJECT_DYNAMIC */
int      ObjMgrGoTypeIsInteractLoot(uint32_t go_type);
uint32_t ObjMgrGoPosOffset(void);            /* 0 until a GO XYZ probe locks */

/* Movement flags: combines the descriptor-based Spline flags and the live
 * UnitMovement struct flags (the latter is what the server actually drives). */
uint32_t ObjMgrMovementFlags(void* obj);

/* Animation/stand state derived from UNIT_FIELD_BYTES_1 byte0. */
uint32_t ObjMgrStandState(void* obj);

/* Is the unit currently tapped-by-me / tapped-by-any (dyn flags). */
int ObjMgrIsTappedByMe(void* obj);
int ObjMgrIsTapped(void* obj);

/* Pitch is not a descriptor; read from the player movement struct relative to
 * the position block. Returns 0.0f when unknown. */
float ObjMgrPitch(void* obj);

/* Walk the live object list (NOT the cache) and return up to max objects as
 * raw object pointers. Returns count filled. Used by GetObjects() style APIs
 * that need fields not present in the cached snapshot. */
uint32_t ObjMgrListObjects(void** out, uint32_t max);
