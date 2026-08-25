#pragma once
#include <stdint.h>

/* Cross-instance teleport + combat-target mirror (shared MMF, all ExtProxy clients). */
#define TELE_MIRROR_NAME "Local\\AscensionExtProxyTeleMirrorV6"
#define TELE_MIRROR_MAGIC 0x54454C32u /* "2LET" */

#pragma pack(push, 1)
typedef struct TeleMirrorSlot {
    uint32_t magic;
    volatile uint32_t seq;
    uint32_t map;
    float x, y, z, o;
    uint32_t leader_pid;
    uint32_t flags;
    uint32_t tick_ms;
    uint32_t publisher_instance;
    uint64_t combat_guid;   /* leader's current combat target (0 = none) */
} TeleMirrorSlot;
#pragma pack(pop)

void TeleMirrorStart(void);
void TeleMirrorStop(void);
/* Publish pose (+ optional combat target) for followers. Returns new seq. */
uint32_t TeleMirrorPublish(uint32_t map, float x, float y, float z, float o, uint32_t flags);
uint32_t TeleMirrorPublishEx(uint32_t map, float x, float y, float z, float o, uint32_t flags,
                             uint64_t combat_guid, uint32_t publisher_instance);
/* Read latest slot. Returns 1 if magic ok. */
int TeleMirrorPeek(TeleMirrorSlot* out);
uint32_t TeleMirrorSeq(void);
