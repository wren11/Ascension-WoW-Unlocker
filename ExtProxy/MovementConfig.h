#pragma once

#include <stdint.h>

#define MOVE_CONFIG_MAGIC 0x4D4F5645u

#pragma pack(push, 1)
typedef struct MovementConfig {
    uint32_t magic;
    uint32_t enabled;
    float map_x;
    float map_y;
    float world_x;
    float world_y;
    float world_z;
    float facing;
    uint32_t opcode;
    uint32_t flags;
    uint32_t flags2;
    uint32_t sequence;
    uint32_t flyhack;
    uint32_t no_zclip;


    uint32_t map_id;

    uint32_t hacks;

    uint32_t packets_only;


    uint32_t inject_mode;

    float speed_scale;

    uint32_t speed_cheat;

    uint32_t allow_undermap;
} MovementConfig;
#pragma pack(pop)

enum {
    kMapIdEasternKingdoms = 0u,
    kMapIdKalimdor = 1u,
    kMapIdOutland = 530u,
    kMapIdNorthrend = 571u,
    kMapIdUnknown = 0xFFFFFFFFu,
};

enum {
    kHackWaterwalk = 0x00000001u,
    kHackHover = 0x00000002u,
    kHackNoFall = 0x00000004u,
    kHackSuperJump = 0x00000008u,
    kHackAntiRoot = 0x00000010u,
    kHackNoclip = 0x00000020u,
};

enum {
    kMoveFlagForward = 0x00000001u,
    kMoveFlagBackward = 0x00000002u,
    kMoveFlagStrafeLeft = 0x00000004u,
    kMoveFlagStrafeRight = 0x00000008u,
    kMoveFlagDisableGravity = 0x00000400u,
    kMoveFlagRoot = 0x00000800u,
    kMoveFlagFalling = 0x00001000u,
    kMoveFlagFallingFar = 0x00002000u,
    kMoveFlagAscending = 0x00200000u,
    kMoveFlagDescending = 0x00400000u,
    kMoveFlagCanFly = 0x01000000u,
    kMoveFlagFlying = 0x02000000u,
    kMoveFlagWaterwalking = 0x10000000u,
    kMoveFlagHover = 0x40000000u,

    kMoveFlyPassive = kMoveFlagDisableGravity | kMoveFlagCanFly,
    kMoveFlyhackFlags = kMoveFlyPassive | kMoveFlagFlying,
    kMoveFallingMask = kMoveFlagFalling | kMoveFlagFallingFar,

    kMoveMaskMoving =
        kMoveFlagForward | kMoveFlagBackward | kMoveFlagStrafeLeft | kMoveFlagStrafeRight |
        kMoveFlagFalling | kMoveFlagFallingFar | kMoveFlagAscending | kMoveFlagDescending,
};

enum {

    /* Stock RVAs — Ascension Extensions.dll 2026-08 fingerprint (AOB-validated). */
    kExtSendRva = 0x00312990u,
    kExtCreatePacketRva = 0x00312220u,
    kExtRegisterPacketRva = 0x00312910u,
    kExtClearPacketRva = 0x00312150u,
    kExtProcessIncomingRva = 0x002C66C0u,
    kExtOpcodeToNameRva = 0x002C76A0u,
    kExtSetOpcodeLoggingRva = 0x002D6D90u,
    /* .data globals shifted +0x4C000 on 2026-08 Extensions (old .data@0xB80000 → 0xBCC000). */
    kExtLuaToUserdataPtrRva = 0x00BCD72Cu,
    kExtResetFnPtrRva = 0x00BCC0FCu,
    kExtQueueFnPtrRva = 0x00BCC1E8u,


    kCDataStoreCtorRva = 0x00001050u,
    kCDataStoreResetReadRva = 0x00001130u,
    kCDataStorePutU32Rva = 0x0007B0A0u,
    kCDataStorePutU16Rva = 0x0007B040u,
    kCDataStorePutF32Rva = 0x0007B160u,
    kCDataStoreDtorRva = 0x00003880u,
    kPacketQueueRva = 0x00006F40u,
    kNetClientSendRva = 0x00232B50u,
    kNetClientStateOffset = 0x534u,
    kQueueObjectGlobalRva = 0x0072FE50u,

    kDefaultMoveOpcode = 0xEEu,

    kOpcodeWorldTeleport = 0x08u,
    kOpcodeTeleportToUnit = 0x09u,
    kOpcodeNewWorld = 0x3Eu,
    kOpcodeMoveTeleport = 0xC5u,
    kOpcodeMoveTeleportCheat = 0xC6u,
    kOpcodeMoveTeleportAck = 0xC7u,
    kOpcodeMoveWorldportAck = 0xDCu,

    kOpcodeForceRunSpeedChange = 0xE2u,
    kOpcodeForceRunSpeedChangeAck = 0xE3u,
    kOpcodeForceRunBackSpeedChange = 0xE4u,
    kOpcodeForceRunBackSpeedChangeAck = 0xE5u,
    kOpcodeForceSwimSpeedChange = 0xE6u,
    kOpcodeForceSwimSpeedChangeAck = 0xE7u,
    kOpcodeLoginVerifyWorld = 0x236u,
    kOpcodeZoneUpdate = 0x1F4u,
    kOpcodeMoveJump = 0xBBu,
    kOpcodeMoveSetFacing = 0xDAu,
    kOpcodeMoveFallLand = 0xC9u,
    kOpcodeMoveStartAscend = 0xBFu,
    kOpcodeMoveCharmPortCheat = 0xE0u,
    kOpcodeMoveSetRawPosition = 0xE1u,
    kOpcodeMoveSetRunSpeedCheat = 0xCCu,
    kOpcodeMoveSetRunSpeed = 0xCDu,
    kOpcodeMoveSetAllSpeedCheat = 0xD6u,
    kOpcodeCastSpell = 0x12Eu,
    kOpcodeUseItem = 0xABu,

    kSpellNameLookupRva = 0x0013F5E0u,
    kCastSpellByNameLuaRva = 0x00140310u,
    kCastSpellByIdLuaRva = 0x0013E060u,
    kFrameScriptExecuteRva = 0x00419210u,
    kRegisterFunctionRva = 0x00417F90u,
    kLuaToNumberRva = 0x0044E030u,
    kLuaPushNumberRva = 0x0044E2A0u,
    kLuaPushStringRva = 0x0044E350u,
    kLuaToLStringRva = 0x0044E0E0u,

    kTaintPtrRva = 0x0094139Cu,
    kTaintDepthRva = 0x009413A0u,
    kTaintLockRva = 0x009413A4u,

    kProtectedActionGateRva = 0x001191D2u,
    kCastTaintGateRva = 0x0040319Cu,
    kInjectIntervalMs = 400u,
    kTeleportInjectIntervalMs = 120u,
};

#define kBaseRunSpeed 7.0f
#define kBaseRunBackSpeed 4.5f
#define kBaseSwimSpeed 4.7222223f
#define kSpeedScaleMin 0.1f
#define kSpeedScaleMax 50.0f
