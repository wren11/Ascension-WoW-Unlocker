#pragma once

#include <stdint.h>

/* Tile root: directory containing %04u%02u%02u.mmtile files (e.g. ...\mmaps\mmtiles). */
void NavHeightSetRoot(const char* utf8_dir);
char* NavHeightRoot(void);

/* Maps root: directory containing %04u.mmap mesh headers (e.g. ...\mmaps\maps). */
void NavMapsSetRoot(const char* utf8_dir);
char* NavMapsRoot(void);

/* True if <mapsRoot>\<mapId:04u>.mmap exists (or maps root unset → assume ok). */
int NavMapMeshExists(uint32_t map);

int NavHeightAt(uint32_t map, float x, float y, float z_hint, float* out_z);

uint32_t NavGuessMap(float x, float y, float z_hint);

uint32_t NavGuessMapInclusive(float x, float y, float z_hint);

int NavIsContinentMap(uint32_t map);

int NavLineOfSight(uint32_t map,
                   float ax, float ay, float az,
                   float bx, float by, float bz,
                   float tolerance);
