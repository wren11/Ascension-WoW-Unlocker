#pragma once

#include <stdint.h>

int NavFindPath(uint32_t map,
                float sx, float sy, float sz,
                float ex, float ey, float ez,
                float* out, int max_pts);
