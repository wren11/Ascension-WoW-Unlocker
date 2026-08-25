#pragma once
/*
 * Runtime offset validate + masked AOB remap for Ascension / Extensions.
 * Defaults = stock RVAs from MovementConfig.h / known ProxyMain enums.
 * Never invents addresses: unique AOB hit required to remap.
 */
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef void (*OffLogFn)(const char* msg);

typedef struct OffsetTable {
    uint32_t net_client_send;
    uint32_t packet_queue;
    uint32_t frame_script_execute;
    uint32_t register_function;
    uint32_t lua_to_number;
    uint32_t lua_push_number;
    uint32_t lua_push_string;
    uint32_t lua_to_lstring;
    uint32_t glue_login;
    uint32_t game_ui_set_target;
    uint32_t ext_process_incoming;
    uint32_t ext_opcode_to_name;
    uint32_t ext_send;
    uint32_t ext_create_packet;
    uint32_t resolved_flags; /* bit per site if remapped */
    uint32_t failed_flags;
} OffsetTable;

extern OffsetTable g_off;

/* Call once after g_ascension / Extensions base known. */
int OffsetResolve_Init(uint8_t* ascension, uint8_t* extensions, OffLogFn log);

#ifdef __cplusplus
}
#endif
