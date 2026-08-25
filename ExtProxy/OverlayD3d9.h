#pragma once

#ifdef __cplusplus
extern "C" {
#endif

enum {
    kDrawMaxPrims = 2048,
    kDrawLine = 1,
    kDrawRect = 2,
    kDrawCircle = 3,
    kDrawText = 4,
    kDrawWorldLine = 5,
    kDrawWorldBox = 6
};

void Overlay_Init(void);
void Overlay_Shutdown(void);
void Overlay_Clear(void);
int Overlay_Ready(void);

/* Double-buffer frame API — build at ≤30 Hz, EndScene draws every game frame. */
void Overlay_BeginFrame(void);   /* clear staging buffer */
void Overlay_EndFrame(void);     /* publish staging → display (atomic) */
void Overlay_SetUpdateHz(int hz); /* 0 = every EndScene may republish; default 30 */
int Overlay_GetUpdateHz(void);
unsigned Overlay_FrameCount(void);
unsigned Overlay_DrawCount(void); /* primitives in published frame */

int Overlay_AddLine(float x1, float y1, float x2, float y2,
                    unsigned char r, unsigned char g, unsigned char b, unsigned char a);
int Overlay_AddRect(float x, float y, float w, float h,
                    unsigned char r, unsigned char g, unsigned char b, unsigned char a);
int Overlay_AddCircle(float x, float y, float radius,
                      unsigned char r, unsigned char g, unsigned char b, unsigned char a);
int Overlay_AddText(float x, float y, const char* text,
                    unsigned char r, unsigned char g, unsigned char b, unsigned char a);
int Overlay_AddWorldLine(float x1, float y1, float z1, float x2, float y2, float z2,
                         unsigned char r, unsigned char g, unsigned char b, unsigned char a);
int Overlay_AddWorldBox(float x, float y, float z, float half_w, float height,
                        unsigned char r, unsigned char g, unsigned char b, unsigned char a);

int Overlay_WorldToScreen(float x, float y, float z, float* out_sx, float* out_sy);

/* Derives camera world position from the cached view matrix. */
int Overlay_GetCameraPosition(float* out_x, float* out_y, float* out_z);

/* Native loot ESP (object-manager radar + world line). Default ON. */
void Overlay_SetLootEsp(int on);
int Overlay_GetLootEsp(void);
void Overlay_SetLootEspRadius(float yards);
float Overlay_GetLootEspRadius(void);
unsigned Overlay_LootEspCount(void);

int Overlay_ParseDrawCommand(const char* cmd);

/* Map Lua color args: 0..1 floats → bytes, or 0..255 bytes. */
unsigned char Overlay_ColorByte(double v);

#ifdef __cplusplus
}
#endif
