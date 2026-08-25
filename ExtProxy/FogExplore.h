#pragma once

#include <stdint.h>

enum {
    kFieldPlayerExploredZones1 = 0x37Du,
    kPlayerExploredZonesWords = 0x80u,


    kAreaTableMaxIdRva = 0x006D3140u,
    kAreaTableMinIdRva = 0x006D3144u,
    kAreaTableRowsRva = 0x006D3154u,

    kAreaTableAreaBitOff = 0x0Cu,
    kAreaTableExplLevelOff = 0x28u,
};

void FogInit(uint8_t* ascension_base);

int FogIsExploredBit(uint32_t area_bit);

int FogAreaBit(uint32_t area_id, uint32_t* out_bit);

int FogIsAreaExplored(uint32_t area_id);

uint32_t FogExploredWord(uint32_t word_index);
