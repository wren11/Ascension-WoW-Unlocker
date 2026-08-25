#pragma once
#include <stdint.h>

/* Per-process Lua→host chat/player report ring (mirrors packet ring naming). */
#define CHAT_REPORT_NAME_BASE "Local\\AscensionExtProxyChatV1"
#define CHAT_REPORT_MAGIC 0x43525631u /* "1VRC" */
#define CHAT_REPORT_SLOTS 256u
#define CHAT_REPORT_SENDER 48
#define CHAT_REPORT_CHANNEL 48
#define CHAT_REPORT_MSG 320
#define CHAT_REPORT_EXTRA 96

enum {
    kChatRepChat = 1,
    kChatRepPlayer = 2,
    kChatRepWho = 3,
};

#pragma pack(push, 1)
typedef struct ChatReportSlot {
    uint32_t seq;
    uint32_t tick_ms;
    uint32_t kind;       /* kChatRep* */
    uint32_t instance_id;
    uint64_t guid;
    int32_t level;
    int32_t class_id;
    int32_t race;
    int32_t gender;
    char sender[CHAT_REPORT_SENDER];
    char channel[CHAT_REPORT_CHANNEL];
    char message[CHAT_REPORT_MSG];
    char extra[CHAT_REPORT_EXTRA];
} ChatReportSlot;

typedef struct ChatReportHeader {
    uint32_t magic;
    uint32_t slot_count;
    uint32_t write_seq;
    uint32_t owner_pid;
    uint32_t drop_count;
    uint32_t r0, r1, r2;
} ChatReportHeader;
#pragma pack(pop)

void ChatReportStart(uint32_t owner_pid);
void ChatReportStop(void);
int ChatReportPush(uint32_t kind, uint64_t guid, const char* sender, const char* channel,
                   const char* message, const char* extra,
                   int level, int class_id, int race, int gender);
const char* ChatReportMapName(void);
