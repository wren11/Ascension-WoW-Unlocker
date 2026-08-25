#pragma once
#include <stdint.h>

/* Cross-instance name directory + Lua RPC bus (shared MMF, all ExtProxy clients). */
#define INST_BUS_NAME "Local\\AscensionExtProxyInstBusV2"
#define INST_BUS_MAGIC 0x31425349u /* "IBS1" */
#define INST_BUS_VERSION 2u
#define INST_BUS_MAX_INST 64u
#define INST_BUS_MAX_RPC 32u
#define INST_BUS_NAME_LEN 48u
#define INST_BUS_FN_LEN 64u
#define INST_BUS_STR_LEN 96u
#define INST_BUS_MAX_ARGS 8u
#define INST_BUS_MAX_RETS 8u
#define INST_BUS_FLAG_ONLINE 1u
#define INST_BUS_FLAG_OCCUPIED 2u

enum InstBusArgKind {
    kInstArgNil = 0,
    kInstArgNumber = 1,
    kInstArgString = 2,
};

enum InstBusRpcState {
    kInstRpcEmpty = 0,
    kInstRpcPending = 1,
    kInstRpcRunning = 2,
    kInstRpcDone = 3,
    kInstRpcError = 4,
};

#pragma pack(push, 1)
typedef struct InstBusArg {
    uint32_t kind;
    double num;
    char str[INST_BUS_STR_LEN];
} InstBusArg;

typedef struct InstBusDir {
    uint32_t instance_id;
    uint32_t pid;
    uint32_t tick_ms;
    uint32_t flags; /* bit0 = online, bit1 = login occupied */
    char name[INST_BUS_NAME_LEN];
} InstBusDir;

typedef struct InstBusRpc {
    volatile uint32_t seq;
    volatile uint32_t state;
    uint32_t target_instance;
    uint32_t source_instance;
    uint32_t source_pid;
    char fn[INST_BUS_FN_LEN];
    uint32_t argc;
    InstBusArg args[INST_BUS_MAX_ARGS];
    uint32_t retc;
    InstBusArg rets[INST_BUS_MAX_RETS];
    char err[INST_BUS_STR_LEN];
} InstBusRpc;

typedef struct InstBusHeader {
    uint32_t magic;
    uint32_t version;
    InstBusDir dir[INST_BUS_MAX_INST];
    InstBusRpc rpc[INST_BUS_MAX_RPC];
    volatile uint32_t rpc_seq_gen;
} InstBusHeader;
#pragma pack(pop)

void InstBusStart(void);
void InstBusStop(void);

/* Publish this client's character name into the directory. */
void InstBusPublishName(const char* name);

/* Reserve a world seat for this process. Returns 1 if under max_inst. */
int InstBusTryOccupy(uint32_t max_inst);

/* Resolve by player name (case-insensitive) or numeric id string. Returns 1 on hit. */
int InstBusResolve(const char* nameOrId, uint32_t* out_instance, uint32_t* out_pid, char* out_name, int name_cap);

/* Directory snapshot for Lua listing. */
int InstBusCopyDir(InstBusDir* out, int max_n);

/*
 * Post RPC and wait for reply (peer UI thread executes).
 * Returns 1 on success, 0 on timeout/error. Fills rets/retc/err.
 */
int InstBusRemoteCall(uint32_t target_instance, const char* fn,
                      const InstBusArg* args, uint32_t argc,
                      InstBusArg* rets, uint32_t* retc, char* err, int err_cap,
                      uint32_t timeout_ms);

/* UI-thread: claim pending RPCs for this instance and queue Lua scripts. */
void InstBusDrainPending(void);

/* Called from Lua GmRpcCapture / GmRpcFail while a claimed RPC is executing. */
void InstBusCaptureReturns(const InstBusArg* rets, uint32_t retc);
void InstBusCaptureError(const char* msg);

uint32_t InstBusCurrentExecSeq(void);
