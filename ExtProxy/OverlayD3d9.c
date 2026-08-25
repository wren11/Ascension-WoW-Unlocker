/*
 * Ascension 3.3.5a — D3D9 overlay (vtable-only, no inline code patches).
 *
 * Hook chain:
 *   1) IAT Direct3DCreate9 → CreateDevice → EndScene / Present / SetTransform / Reset
 *   2) Late inject: dummy device patches the SHARED d3d9 device vtable (hooks live device)
 *
 * Draw model:
 *   Staging buffer (Lua/API) published via Overlay_EndFrame at ≤30 Hz.
 *   Present draws the published front buffer + native loot ESP on the backbuffer
 *   (after WoW UI). EndScene is fallback only until Present has fired once.
 *   Native ESP does not need Lua — empty front buffer still draws the loot map.
 *   Batch DrawPrimitiveUP — FVF XYZRHW with VS/PS unbound.
 */

#include "OverlayD3d9.h"
#include "ObjectMgr.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <math.h>
#include <stdlib.h>

#ifndef D3D_SDK_VERSION
#define D3D_SDK_VERSION 32
#endif
#define D3DDEVTYPE_HAL 1
#define D3DADAPTER_DEFAULT 0
#define D3DCREATE_SOFTWARE_VERTEXPROCESSING 0x20
#define D3DCREATE_MIXED_VERTEXPROCESSING 0x80
#define D3DSWAPEFFECT_DISCARD 1
#define D3DFMT_UNKNOWN 0
#define D3DFMT_D24S8 75
#define D3DPRESENT_INTERVAL_DEFAULT 0
#define D3DPT_LINELIST 2
#define D3DFVF_XYZRHW 0x004
#define D3DFVF_DIFFUSE 0x040
#define D3DRS_ALPHABLENDENABLE 27
#define D3DRS_SRCBLEND 19
#define D3DRS_DESTBLEND 20
#define D3DRS_ZENABLE 7
#define D3DRS_FOGENABLE 28
#define D3DRS_LIGHTING 137
#define D3DRS_CULLMODE 22
#define D3DRS_COLORWRITEENABLE 168
#define D3DRS_ZWRITEENABLE 14
#define D3DRS_STENCILENABLE 52
#define D3DRS_CLIPPING 136
#define D3DRS_SCISSORTESTENABLE 174
#define D3DCULL_NONE 1
#define D3DBLEND_SRCALPHA 5
#define D3DBLEND_INVSRCALPHA 6
#define D3DTS_VIEW 2
#define D3DTS_PROJECTION 3
#define OV_FVF (D3DFVF_XYZRHW | D3DFVF_DIFFUSE)
#define OV_MAX_BATCH_VERTS 8192

/* IDirect3DDevice9 vtable indices (IUnknown + methods). Verified vs d3d9.h. */
enum {
    OV_VT_Reset = 16,
    OV_VT_Present = 17,
    OV_VT_BeginScene = 41,
    OV_VT_EndScene = 42,
    OV_VT_SetTransform = 44,
    OV_VT_GetTransform = 45,
    OV_VT_MultiplyTransform = 46,
    OV_VT_SetViewport = 47,
    OV_VT_GetViewport = 48,
    OV_VT_SetRenderState = 57,
    OV_VT_GetRenderState = 58,
    OV_VT_SetTexture = 65,
    OV_VT_DrawPrimitiveUP = 83,
    OV_VT_SetVertexDeclaration = 87,
    OV_VT_GetVertexDeclaration = 88,
    OV_VT_SetFVF = 89,
    OV_VT_GetFVF = 90,
    OV_VT_SetVertexShader = 92,
    OV_VT_GetVertexShader = 93,
    OV_VT_SetPixelShader = 107,
    OV_VT_GetPixelShader = 108
};

/*
 * We probe GetViewport at slot 48 (SetViewport is 47). Never call 47 as a getter.
 */

typedef struct {
    UINT BackBufferWidth, BackBufferHeight;
    UINT BackBufferFormat, BackBufferCount;
    UINT MultiSampleType; DWORD MultiSampleQuality;
    UINT SwapEffect; HWND hDeviceWindow; BOOL Windowed;
    BOOL EnableAutoDepthStencil; UINT AutoDepthStencilFormat;
    DWORD Flags; UINT FullScreen_RefreshRateInHz; UINT PresentationInterval;
} OvPresentParams;

typedef struct { float _11,_12,_13,_14,_21,_22,_23,_24,_31,_32,_33,_34,_41,_42,_43,_44; } OvMatrix;
typedef struct { DWORD X, Y, Width, Height; float MinZ, MaxZ; } OvViewport;
typedef struct IDirect3D9 IDirect3D9;
typedef struct IDirect3DDevice9 IDirect3DDevice9;

typedef struct {
    float x, y, z, rhw;
    DWORD color;
} OverlayVtx;

typedef struct DrawPrim {
    int type;
    float x1, y1, z1, x2, y2, z2;
    unsigned char r, g, b, a;
    char text[96];
} DrawPrim;

typedef IDirect3D9*(WINAPI* Direct3DCreate9_fn)(UINT);
typedef HRESULT(__stdcall* CreateDevice_fn)(IDirect3D9*, UINT, UINT, HWND, DWORD, void*, IDirect3DDevice9**);
typedef HRESULT(__stdcall* EndScene_fn)(IDirect3DDevice9*);
typedef HRESULT(__stdcall* Present_fn)(IDirect3DDevice9*, const RECT*, const RECT*, HWND, const RGNDATA*);
typedef HRESULT(__stdcall* Reset_fn)(IDirect3DDevice9*, void*);
typedef HRESULT(__stdcall* SetTransform_fn)(IDirect3DDevice9*, DWORD, const OvMatrix*);
typedef HRESULT(__stdcall* GetTransform_fn)(IDirect3DDevice9*, DWORD, OvMatrix*);
typedef HRESULT(__stdcall* GetViewport_fn)(IDirect3DDevice9*, OvViewport*);
typedef HRESULT(__stdcall* SetFVF_fn)(IDirect3DDevice9*, DWORD);
typedef HRESULT(__stdcall* GetFVF_fn)(IDirect3DDevice9*, DWORD*);
typedef HRESULT(__stdcall* DrawPrimitiveUP_fn)(IDirect3DDevice9*, DWORD, UINT, const void*, UINT);
typedef HRESULT(__stdcall* SetRenderState_fn)(IDirect3DDevice9*, DWORD, DWORD);
typedef HRESULT(__stdcall* GetRenderState_fn)(IDirect3DDevice9*, DWORD, DWORD*);
typedef HRESULT(__stdcall* SetTexture_fn)(IDirect3DDevice9*, DWORD, void*);
typedef HRESULT(__stdcall* SetShader_fn)(IDirect3DDevice9*, void*);
typedef HRESULT(__stdcall* GetShader_fn)(IDirect3DDevice9*, void**);
typedef ULONG(__stdcall* Release_fn)(IDirect3DDevice9*);

static Direct3DCreate9_fn g_real_create9 = NULL;
static void** g_iat_create9_slot = NULL;
static CreateDevice_fn g_real_create_device = NULL;
static EndScene_fn g_real_endscene = NULL;
static Present_fn g_real_present = NULL;
static Reset_fn g_real_reset = NULL;
static SetTransform_fn g_real_settransform = NULL;

static IDirect3DDevice9* g_dev = NULL;
static IDirect3DDevice9* g_dummy_dev = NULL;
static HMODULE g_d3d9 = NULL;
static int g_overlay_ready = 0;
static volatile LONG g_hooked = 0;
static volatile LONG g_frames = 0;
static volatile LONG g_drew_this_frame = 0;
static volatile LONG g_presents_ever = 0;
static HANDLE g_late_hook_thread = NULL;

static CRITICAL_SECTION g_draw_cs;
static int g_cs_init = 0;

/* Double buffer: staging (writer) + front (EndScene reader). */
static DrawPrim g_stage[kDrawMaxPrims];
static int g_stage_count = 0;
static DrawPrim g_front[kDrawMaxPrims];
static int g_front_count = 0;
static volatile LONG g_publish_seq = 0;
static DWORD g_last_publish_ms = 0;
static int g_update_hz = 30; /* Lua rebuild cadence guidance; EndFrame rate-limits */
static int g_building = 0;

static OvMatrix g_view;
static OvMatrix g_proj;
static int g_have_view = 0;
static int g_have_proj = 0;

static OverlayVtx g_batch[OV_MAX_BATCH_VERTS];
static int g_batch_n = 0;

/* ---- 5x7 bitmap font (ASCII 32..90), bits MSB left ---- */
static const unsigned char kFont5x7[64][7] = {
    /* space */ {0,0,0,0,0,0,0},
    /* ! */ {0x04,0x04,0x04,0x04,0x04,0x00,0x04},
    /* " */ {0x0A,0x0A,0x0A,0,0,0,0},
    /* # */ {0x0A,0x0A,0x1F,0x0A,0x1F,0x0A,0x0A},
    /* $ */ {0x04,0x0F,0x14,0x0E,0x05,0x1E,0x04},
    /* % */ {0x18,0x19,0x02,0x04,0x08,0x13,0x03},
    /* & */ {0x08,0x14,0x14,0x08,0x15,0x12,0x0D},
    /* ' */ {0x0C,0x04,0x08,0,0,0,0},
    /* ( */ {0x02,0x04,0x08,0x08,0x08,0x04,0x02},
    /* ) */ {0x08,0x04,0x02,0x02,0x02,0x04,0x08},
    /* * */ {0x00,0x04,0x15,0x0E,0x15,0x04,0x00},
    /* + */ {0x00,0x04,0x04,0x1F,0x04,0x04,0x00},
    /* , */ {0,0,0,0,0x0C,0x04,0x08},
    /* - */ {0,0,0,0x1F,0,0,0},
    /* . */ {0,0,0,0,0,0x0C,0x0C},
    /* / */ {0x01,0x02,0x04,0x08,0x10,0,0},
    /* 0 */ {0x0E,0x11,0x13,0x15,0x19,0x11,0x0E},
    /* 1 */ {0x04,0x0C,0x04,0x04,0x04,0x04,0x0E},
    /* 2 */ {0x0E,0x11,0x01,0x02,0x04,0x08,0x1F},
    /* 3 */ {0x1F,0x02,0x04,0x02,0x01,0x11,0x0E},
    /* 4 */ {0x02,0x06,0x0A,0x12,0x1F,0x02,0x02},
    /* 5 */ {0x1F,0x10,0x1E,0x01,0x01,0x11,0x0E},
    /* 6 */ {0x06,0x08,0x10,0x1E,0x11,0x11,0x0E},
    /* 7 */ {0x1F,0x01,0x02,0x04,0x08,0x08,0x08},
    /* 8 */ {0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E},
    /* 9 */ {0x0E,0x11,0x11,0x0F,0x01,0x02,0x0C},
    /* : */ {0,0x0C,0x0C,0,0x0C,0x0C,0},
    /* ; */ {0,0x0C,0x0C,0,0x0C,0x04,0x08},
    /* < */ {0x02,0x04,0x08,0x10,0x08,0x04,0x02},
    /* = */ {0,0x1F,0,0x1F,0,0,0},
    /* > */ {0x08,0x04,0x02,0x01,0x02,0x04,0x08},
    /* ? */ {0x0E,0x11,0x01,0x02,0x04,0x00,0x04},
    /* @ */ {0x0E,0x11,0x17,0x15,0x17,0x10,0x0E},
    /* A */ {0x0E,0x11,0x11,0x1F,0x11,0x11,0x11},
    /* B */ {0x1E,0x11,0x11,0x1E,0x11,0x11,0x1E},
    /* C */ {0x0E,0x11,0x10,0x10,0x10,0x11,0x0E},
    /* D */ {0x1C,0x12,0x11,0x11,0x11,0x12,0x1C},
    /* E */ {0x1F,0x10,0x10,0x1E,0x10,0x10,0x1F},
    /* F */ {0x1F,0x10,0x10,0x1E,0x10,0x10,0x10},
    /* G */ {0x0E,0x11,0x10,0x17,0x11,0x11,0x0F},
    /* H */ {0x11,0x11,0x11,0x1F,0x11,0x11,0x11},
    /* I */ {0x0E,0x04,0x04,0x04,0x04,0x04,0x0E},
    /* J */ {0x07,0x02,0x02,0x02,0x02,0x12,0x0C},
    /* K */ {0x11,0x12,0x14,0x18,0x14,0x12,0x11},
    /* L */ {0x10,0x10,0x10,0x10,0x10,0x10,0x1F},
    /* M */ {0x11,0x1B,0x15,0x15,0x11,0x11,0x11},
    /* N */ {0x11,0x19,0x15,0x13,0x11,0x11,0x11},
    /* O */ {0x0E,0x11,0x11,0x11,0x11,0x11,0x0E},
    /* P */ {0x1E,0x11,0x11,0x1E,0x10,0x10,0x10},
    /* Q */ {0x0E,0x11,0x11,0x11,0x15,0x12,0x0D},
    /* R */ {0x1E,0x11,0x11,0x1E,0x14,0x12,0x11},
    /* S */ {0x0F,0x10,0x10,0x0E,0x01,0x01,0x1E},
    /* T */ {0x1F,0x04,0x04,0x04,0x04,0x04,0x04},
    /* U */ {0x11,0x11,0x11,0x11,0x11,0x11,0x0E},
    /* V */ {0x11,0x11,0x11,0x11,0x11,0x0A,0x04},
    /* W */ {0x11,0x11,0x11,0x15,0x15,0x1B,0x11},
    /* X */ {0x11,0x11,0x0A,0x04,0x0A,0x11,0x11},
    /* Y */ {0x11,0x11,0x0A,0x04,0x04,0x04,0x04},
    /* Z */ {0x1F,0x01,0x02,0x04,0x08,0x10,0x1F},
    /* [ */ {0x0E,0x08,0x08,0x08,0x08,0x08,0x0E},
    /* \ */ {0x10,0x08,0x04,0x02,0x01,0,0},
    /* ] */ {0x0E,0x02,0x02,0x02,0x02,0x02,0x0E},
    /* ^ */ {0x04,0x0A,0x11,0,0,0,0},
    /* _ */ {0,0,0,0,0,0,0x1F},
};

static HWND FindGameHwnd(void);

static void LogO(const char* s)
{
    OutputDebugStringA("[ExtProxy Overlay] ");
    OutputDebugStringA(s);
    OutputDebugStringA("\n");
}

static void ComRelease(void* p)
{
    void** vt;
    Release_fn rel;
    if (!p)
        return;
    vt = *(void***)p;
    if (!vt)
        return;
    rel = (Release_fn)vt[2];
    if (rel)
        rel((IDirect3DDevice9*)p);
}

static int MatIsNearIdentity(const OvMatrix* m)
{
    if (!m)
        return 1;
    return fabsf(m->_11 - 1.f) < 1e-3f && fabsf(m->_22 - 1.f) < 1e-3f
        && fabsf(m->_33 - 1.f) < 1e-3f && fabsf(m->_44 - 1.f) < 1e-3f
        && fabsf(m->_12) < 1e-3f && fabsf(m->_13) < 1e-3f && fabsf(m->_14) < 1e-3f
        && fabsf(m->_21) < 1e-3f && fabsf(m->_23) < 1e-3f && fabsf(m->_24) < 1e-3f
        && fabsf(m->_31) < 1e-3f && fabsf(m->_32) < 1e-3f && fabsf(m->_34) < 1e-3f
        && fabsf(m->_41) < 1e-3f && fabsf(m->_42) < 1e-3f && fabsf(m->_43) < 1e-3f;
}

static int MatLooksLikeProj(const OvMatrix* m)
{
    if (!m || MatIsNearIdentity(m))
        return 0;
    /* Perspective: non-zero FOV scale on _11/_22. Identity/UI often _44=1 and tiny off-diag. */
    if (fabsf(m->_11) < 0.05f)
        return 0;
    return 1;
}

static int MatLooksLikeView(const OvMatrix* m)
{
    if (!m || MatIsNearIdentity(m))
        return 0;
    return 1;
}

static DWORD ARGB(unsigned char a, unsigned char r, unsigned char g, unsigned char b)
{
    return ((DWORD)a << 24) | ((DWORD)r << 16) | ((DWORD)g << 8) | (DWORD)b;
}

unsigned char Overlay_ColorByte(double v)
{
    if (v != v) return 255; /* NaN */
    if (v < 0.0) return 0;
    if (v <= 1.0) return (unsigned char)(v * 255.0 + 0.5);
    if (v >= 255.0) return 255;
    return (unsigned char)(v + 0.5);
}

void Overlay_SetUpdateHz(int hz)
{
    if (hz < 0) hz = 0;
    if (hz > 240) hz = 240;
    g_update_hz = hz;
}

int Overlay_GetUpdateHz(void) { return g_update_hz; }
unsigned Overlay_FrameCount(void) { return (unsigned)g_frames; }
unsigned Overlay_DrawCount(void) { return (unsigned)g_front_count; }

void Overlay_Clear(void)
{
    if (!g_cs_init) return;
    EnterCriticalSection(&g_draw_cs);
    g_stage_count = 0;
    g_front_count = 0; /* legacy: wipe display immediately */
    g_building = 0;
    LeaveCriticalSection(&g_draw_cs);
}

void Overlay_BeginFrame(void)
{
    if (!g_cs_init) return;
    EnterCriticalSection(&g_draw_cs);
    g_stage_count = 0;
    g_building = 1; /* keep previous front until EndFrame (no flicker) */
    LeaveCriticalSection(&g_draw_cs);
}

void Overlay_EndFrame(void)
{
    DWORD now, minGap;
    if (!g_cs_init) return;
    now = GetTickCount();
    minGap = (g_update_hz > 0) ? (DWORD)(1000 / g_update_hz) : 0;
    if (minGap > 0 && g_last_publish_ms && (now - g_last_publish_ms) < minGap) {
        /* Still publish — caller asked for EndFrame; rate-limit only auto path. */
    }
    EnterCriticalSection(&g_draw_cs);
    memcpy(g_front, g_stage, (size_t)g_stage_count * sizeof(DrawPrim));
    g_front_count = g_stage_count;
    g_building = 0;
    g_last_publish_ms = now;
    InterlockedIncrement(&g_publish_seq);
    LeaveCriticalSection(&g_draw_cs);
}

/* Compat: Clear+Add without EndFrame still visible — publish staging if idle. */
static void AutoPublishIfNeeded(void)
{
    DWORD now, minGap;
    if (!g_cs_init) return;
    now = GetTickCount();
    minGap = (g_update_hz > 0) ? (DWORD)(1000 / g_update_hz) : 0;
    EnterCriticalSection(&g_draw_cs);
    if (g_building) {
        /* Mid-rebuild: keep showing previous front (no flicker to empty). */
        LeaveCriticalSection(&g_draw_cs);
        return;
    }
    if (g_stage_count > 0 &&
        (g_front_count != g_stage_count || memcmp(g_front, g_stage,
            (size_t)g_stage_count * sizeof(DrawPrim)) != 0)) {
        if (minGap == 0 || !g_last_publish_ms || (now - g_last_publish_ms) >= minGap) {
            memcpy(g_front, g_stage, (size_t)g_stage_count * sizeof(DrawPrim));
            g_front_count = g_stage_count;
            g_last_publish_ms = now;
            InterlockedIncrement(&g_publish_seq);
        }
    } else if (g_stage_count == 0 && g_front_count > 0 && !g_building) {
        /* Cleared and never refilled — allow empty publish after gap */
        if (minGap == 0 || (now - g_last_publish_ms) >= minGap) {
            g_front_count = 0;
            g_last_publish_ms = now;
        }
    }
    LeaveCriticalSection(&g_draw_cs);
}

int Overlay_Ready(void)
{
    return (g_real_endscene && g_hooked) ? 1 : 0;
}

static int PushPrim(const DrawPrim* p)
{
    int ok = 0;
    if (!g_cs_init || !p) return 0;
    EnterCriticalSection(&g_draw_cs);
    if (g_stage_count < kDrawMaxPrims) {
        g_stage[g_stage_count++] = *p;
        ok = 1;
        if (!g_building) {
            /* Legacy path: publish immediately so Clear+Add without EndFrame works,
             * but EndScene still draws front — copy stage→front now. */
            memcpy(g_front, g_stage, (size_t)g_stage_count * sizeof(DrawPrim));
            g_front_count = g_stage_count;
        }
    }
    LeaveCriticalSection(&g_draw_cs);
    return ok;
}

int Overlay_AddLine(float x1, float y1, float x2, float y2,
                    unsigned char r, unsigned char g, unsigned char b, unsigned char a)
{
    DrawPrim p;
    memset(&p, 0, sizeof(p));
    p.type = kDrawLine;
    p.x1 = x1; p.y1 = y1; p.x2 = x2; p.y2 = y2;
    p.r = r; p.g = g; p.b = b; p.a = a ? a : 255;
    return PushPrim(&p);
}

int Overlay_AddRect(float x, float y, float w, float h,
                    unsigned char r, unsigned char g, unsigned char b, unsigned char a)
{
    DrawPrim p;
    memset(&p, 0, sizeof(p));
    p.type = kDrawRect;
    p.x1 = x; p.y1 = y; p.x2 = w; p.y2 = h;
    p.r = r; p.g = g; p.b = b; p.a = a ? a : 255;
    return PushPrim(&p);
}

int Overlay_AddCircle(float x, float y, float radius,
                      unsigned char r, unsigned char g, unsigned char b, unsigned char a)
{
    DrawPrim p;
    memset(&p, 0, sizeof(p));
    p.type = kDrawCircle;
    p.x1 = x; p.y1 = y; p.x2 = radius;
    p.r = r; p.g = g; p.b = b; p.a = a ? a : 255;
    return PushPrim(&p);
}

int Overlay_AddText(float x, float y, const char* text,
                    unsigned char r, unsigned char g, unsigned char b, unsigned char a)
{
    DrawPrim p;
    memset(&p, 0, sizeof(p));
    p.type = kDrawText;
    p.x1 = x; p.y1 = y;
    p.r = r; p.g = g; p.b = b; p.a = a ? a : 255;
    if (text) strncpy(p.text, text, sizeof(p.text) - 1);
    return PushPrim(&p);
}

int Overlay_AddWorldLine(float x1, float y1, float z1, float x2, float y2, float z2,
                         unsigned char r, unsigned char g, unsigned char b, unsigned char a)
{
    DrawPrim p;
    memset(&p, 0, sizeof(p));
    p.type = kDrawWorldLine;
    p.x1 = x1; p.y1 = y1; p.z1 = z1;
    p.x2 = x2; p.y2 = y2; p.z2 = z2;
    p.r = r; p.g = g; p.b = b; p.a = a ? a : 255;
    return PushPrim(&p);
}

int Overlay_AddWorldBox(float x, float y, float z, float half_w, float height,
                        unsigned char r, unsigned char g, unsigned char b, unsigned char a)
{
    DrawPrim p;
    memset(&p, 0, sizeof(p));
    p.type = kDrawWorldBox;
    p.x1 = x; p.y1 = y; p.z1 = z;
    p.x2 = half_w; p.y2 = height;
    p.r = r; p.g = g; p.b = b; p.a = a ? a : 255;
    return PushPrim(&p);
}

int Overlay_ParseDrawCommand(const char* cmd)
{
    char kind[16];
    float a, b, c, d, e, f;
    int r = 80, g = 220, bl = 120, al = 255;
    if (!cmd) return 0;
    while (*cmd == ' ') cmd++;
    if (_strnicmp(cmd, "CLEAR", 5) == 0) {
        Overlay_Clear();
        return 1;
    }
    if (_strnicmp(cmd, "BEGIN", 5) == 0) {
        Overlay_BeginFrame();
        return 1;
    }
    if (_strnicmp(cmd, "END", 3) == 0) {
        Overlay_EndFrame();
        return 1;
    }
    if (_strnicmp(cmd, "WLINE:", 6) == 0) {
        if (sscanf(cmd + 6, "%f:%f:%f:%f:%f:%f:%d:%d:%d:%d",
                   &a, &b, &c, &d, &e, &f, &r, &g, &bl, &al) >= 6)
            return Overlay_AddWorldLine(a, b, c, d, e, f,
                (unsigned char)r, (unsigned char)g, (unsigned char)bl, (unsigned char)al);
    }
    if (sscanf(cmd, "%15[^:]:%f:%f:%f:%f:%d:%d:%d:%d", kind, &a, &b, &c, &d, &r, &g, &bl, &al) >= 5) {
        if (_stricmp(kind, "LINE") == 0)
            return Overlay_AddLine(a, b, c, d, (unsigned char)r, (unsigned char)g, (unsigned char)bl, (unsigned char)al);
        if (_stricmp(kind, "RECT") == 0)
            return Overlay_AddRect(a, b, c, d, (unsigned char)r, (unsigned char)g, (unsigned char)bl, (unsigned char)al);
        if (_stricmp(kind, "CIRCLE") == 0)
            return Overlay_AddCircle(a, b, c, (unsigned char)r, (unsigned char)g, (unsigned char)bl, (unsigned char)al);
    }
    if (_strnicmp(cmd, "TEXT:", 5) == 0) {
        float x, y;
        char text[96];
        if (sscanf(cmd + 5, "%f:%f:%95[^\n]", &x, &y, text) >= 3)
            return Overlay_AddText(x, y, text, 255, 255, 80, 255);
    }
    if (_strnicmp(cmd, "ESP:", 4) == 0) {
        Overlay_SetLootEsp(atoi(cmd + 4));
        return 1;
    }
    if (_strnicmp(cmd, "ESPR:", 5) == 0) {
        Overlay_SetLootEspRadius((float)atof(cmd + 5));
        return 1;
    }
    return 0;
}

static void MatMulVec(const OvMatrix* m, float x, float y, float z, float w,
                      float* ox, float* oy, float* oz, float* ow)
{
    *ox = x * m->_11 + y * m->_21 + z * m->_31 + w * m->_41;
    *oy = x * m->_12 + y * m->_22 + z * m->_32 + w * m->_42;
    *oz = x * m->_13 + y * m->_23 + z * m->_33 + w * m->_43;
    *ow = x * m->_14 + y * m->_24 + z * m->_34 + w * m->_44;
}

/* Prefer last world-pass SetTransform. GetTransform at Present is often identity/UI. */
static int RefreshMatrices(IDirect3DDevice9* dev)
{
    void** vtbl;
    GetTransform_fn getxf;
    OvMatrix view, proj;
    if (!dev) return g_have_view && g_have_proj;
    vtbl = *(void***)dev;
    getxf = (GetTransform_fn)vtbl[OV_VT_GetTransform];
    if (getxf) {
        if (SUCCEEDED(getxf(dev, D3DTS_VIEW, &view)) && MatLooksLikeView(&view)) {
            g_view = view;
            g_have_view = 1;
        }
        if (SUCCEEDED(getxf(dev, D3DTS_PROJECTION, &proj)) && MatLooksLikeProj(&proj)) {
            g_proj = proj;
            g_have_proj = 1;
        }
    }
    return g_have_view && g_have_proj;
}

static int GetViewportSafe(IDirect3DDevice9* dev, OvViewport* vp)
{
    void** vtbl = *(void***)dev;
    GetViewport_fn getvp;
    HWND hwnd;
    RECT rc;
    if (!dev || !vp)
        return 0;
    getvp = (GetViewport_fn)vtbl[OV_VT_GetViewport]; /* 48 — 47 is SetViewport */
    if (getvp) {
        memset(vp, 0, sizeof(*vp));
        if (SUCCEEDED(getvp(dev, vp)) && vp->Width > 64 && vp->Width < 16384
            && vp->Height > 64 && vp->Height < 16384)
            return 1;
    }
    hwnd = FindGameHwnd();
    if (hwnd && GetClientRect(hwnd, &rc) && rc.right > 64 && rc.bottom > 64) {
        vp->X = 0;
        vp->Y = 0;
        vp->Width = (DWORD)rc.right;
        vp->Height = (DWORD)rc.bottom;
        vp->MinZ = 0.f;
        vp->MaxZ = 1.f;
        return 1;
    }
    return 0;
}

static int WorldToScreenDev(IDirect3DDevice9* dev, float x, float y, float z,
                            float* sx, float* sy)
{
    float vx, vy, vz, vw, cx, cy, cz, cw;
    OvViewport vp;
    if (!dev || !sx || !sy) return 0;
    if (!RefreshMatrices(dev)) return 0;
    MatMulVec(&g_view, x, y, z, 1.f, &vx, &vy, &vz, &vw);
    MatMulVec(&g_proj, vx, vy, vz, vw, &cx, &cy, &cz, &cw);
    if (cw <= 0.001f) return 0;
    cx /= cw; cy /= cw;
    if (!GetViewportSafe(dev, &vp)) return 0;
    *sx = (cx + 1.f) * 0.5f * (float)vp.Width + (float)vp.X;
    *sy = (1.f - cy) * 0.5f * (float)vp.Height + (float)vp.Y;
    return 1;
}

int Overlay_WorldToScreen(float x, float y, float z, float* out_sx, float* out_sy)
{
    if (!g_dev) return 0;
    return WorldToScreenDev(g_dev, x, y, z, out_sx, out_sy);
}

int Overlay_GetCameraPosition(float* out_x, float* out_y, float* out_z)
{
    float r11, r12, r13, r21, r22, r23, r31, r32, r33;
    float tx, ty, tz;
    OvMatrix v = g_view;
    if (!out_x || !out_y || !out_z) return 0;
    if (v._11 == 0.f && v._22 == 0.f && v._33 == 0.f && v._44 == 0.f) return 0;
    r11 = v._11; r12 = v._21; r13 = v._31;
    r21 = v._12; r22 = v._22; r23 = v._32;
    r31 = v._13; r32 = v._23; r33 = v._33;
    tx = v._41; ty = v._42; tz = v._43;
    *out_x = -(r11 * tx + r12 * ty + r13 * tz);
    *out_y = -(r21 * tx + r22 * ty + r23 * tz);
    *out_z = -(r31 * tx + r32 * ty + r33 * tz);
    return 1;
}

static void BatchLine(float x1, float y1, float x2, float y2, DWORD color)
{
    if (g_batch_n + 2 > OV_MAX_BATCH_VERTS) return;
    g_batch[g_batch_n].x = x1; g_batch[g_batch_n].y = y1;
    g_batch[g_batch_n].z = 0; g_batch[g_batch_n].rhw = 1; g_batch[g_batch_n].color = color;
    g_batch_n++;
    g_batch[g_batch_n].x = x2; g_batch[g_batch_n].y = y2;
    g_batch[g_batch_n].z = 0; g_batch[g_batch_n].rhw = 1; g_batch[g_batch_n].color = color;
    g_batch_n++;
}

static void BatchRect(float x, float y, float w, float h, DWORD color)
{
    BatchLine(x, y, x + w, y, color);
    BatchLine(x + w, y, x + w, y + h, color);
    BatchLine(x + w, y + h, x, y + h, color);
    BatchLine(x, y + h, x, y, color);
}

static void BatchCircle(float cx, float cy, float radius, DWORD color)
{
    const int segs = 12;
    int i;
    for (i = 0; i < segs; i++) {
        float a0 = (float)(i * 6.2831853 / segs);
        float a1 = (float)((i + 1) * 6.2831853 / segs);
        BatchLine(cx + cosf(a0) * radius, cy + sinf(a0) * radius,
                  cx + cosf(a1) * radius, cy + sinf(a1) * radius, color);
    }
}

static void BatchGlyph(float x, float y, char ch, DWORD color, float scale)
{
    int idx;
    int row, col;
    const unsigned char* rows;
    if (ch >= 'a' && ch <= 'z') ch = (char)(ch - 'a' + 'A');
    if (ch < 32 || ch > 95) ch = '?';
    idx = ch - 32;
    if (idx < 0 || idx >= 64) return;
    rows = kFont5x7[idx];
    for (row = 0; row < 7; row++) {
        unsigned char bits = rows[row];
        for (col = 0; col < 5; col++) {
            if (bits & (0x10 >> col)) {
                float px = x + (float)col * scale;
                float py = y + (float)row * scale;
                /* 1px "dot" as tiny cross — reads as filled pixel */
                BatchLine(px, py, px + scale, py, color);
                if (scale >= 1.5f)
                    BatchLine(px, py + scale * 0.5f, px + scale, py + scale * 0.5f, color);
            }
        }
    }
}

static void BatchText(float x, float y, const char* text, DWORD color)
{
    float cx = x;
    float scale = 1.5f;
    if (!text) return;
    while (*text) {
        if (*text == '\n') {
            y += 8.f * scale;
            cx = x;
            text++;
            continue;
        }
        BatchGlyph(cx, y, *text, color, scale);
        cx += 6.f * scale;
        text++;
    }
}

static void BatchWorldLine(IDirect3DDevice9* dev, float x1, float y1, float z1,
                           float x2, float y2, float z2, DWORD color)
{
    float sx1, sy1, sx2, sy2;
    if (!WorldToScreenDev(dev, x1, y1, z1, &sx1, &sy1)) return;
    if (!WorldToScreenDev(dev, x2, y2, z2, &sx2, &sy2)) return;
    BatchLine(sx1, sy1, sx2, sy2, color);
}

static void BatchWorldBox(IDirect3DDevice9* dev, float x, float y, float z,
                          float half, float height, DWORD color)
{
    float sx0, sy0, sx1, sy1;
    float hw, hh;
    if (!WorldToScreenDev(dev, x, y, z, &sx0, &sy0)) return;
    if (!WorldToScreenDev(dev, x, y, z + (height > 0.1f ? height : 2.f), &sx1, &sy1)) {
        sx1 = sx0;
        sy1 = sy0 - 36.f;
    }
    hw = half * 10.f;
    if (hw < 5.f) hw = 5.f;
    if (hw > 36.f) hw = 36.f;
    hh = sy0 - sy1;
    if (hh < 12.f) hh = 12.f;
    if (hh > 120.f) hh = 120.f;
    BatchRect(sx0 - hw, sy1, hw * 2.f, hh, color);
    BatchLine(sx0, sy0, sx0, sy1, color);
    BatchLine(sx0 - hw * 0.35f, sy0, sx0 + hw * 0.35f, sy0, color);
}

static void FlushBatch(IDirect3DDevice9* dev)
{
    void** vtbl;
    SetFVF_fn setfvf;
    DrawPrimitiveUP_fn drawup;
    if (g_batch_n < 2 || !dev) return;
    vtbl = *(void***)dev;
    setfvf = (SetFVF_fn)vtbl[OV_VT_SetFVF];
    drawup = (DrawPrimitiveUP_fn)vtbl[OV_VT_DrawPrimitiveUP];
    if (setfvf) setfvf(dev, OV_FVF);
    if (drawup) drawup(dev, D3DPT_LINELIST, (UINT)(g_batch_n / 2), g_batch, sizeof(OverlayVtx));
    g_batch_n = 0;
}

#include "OverlayLootEsp.inc.c"

static void RenderOverlay(IDirect3DDevice9* dev)
{
    DrawPrim local[kDrawMaxPrims];
    int n, i;
    DWORD old_aa = 0, old_zb = 0, old_fog = 0, old_lit = 0, old_cull = 0;
    DWORD old_zw = 0, old_st = 0, old_sc = 0, old_cw = 0, old_fvf = 0;
    void** vtbl;
    GetRenderState_fn getrs;
    SetRenderState_fn setrs;
    SetTexture_fn sett;
    GetShader_fn getvs, getps, getvd;
    SetShader_fn setvs, setps, setvd;
    GetFVF_fn getfvf;
    void* old_vs = NULL;
    void* old_ps = NULL;
    void* old_vd = NULL;
    int want_esp;

    if (!dev || !g_cs_init) return;
    if (InterlockedCompareExchange(&g_drew_this_frame, 1, 0) != 0)
        return; /* already drew this Present cycle */

    AutoPublishIfNeeded();

    EnterCriticalSection(&g_draw_cs);
    n = g_front_count;
    if (n > 0) memcpy(local, g_front, (size_t)n * sizeof(DrawPrim));
    LeaveCriticalSection(&g_draw_cs);

    want_esp = Overlay_GetLootEsp();
    if (n <= 0 && !want_esp)
        return;

    RefreshMatrices(dev);
    g_batch_n = 0;

    vtbl = *(void***)dev;
    getrs = (GetRenderState_fn)vtbl[OV_VT_GetRenderState];
    setrs = (SetRenderState_fn)vtbl[OV_VT_SetRenderState];
    sett = (SetTexture_fn)vtbl[OV_VT_SetTexture];
    getvs = (GetShader_fn)vtbl[OV_VT_GetVertexShader];
    setvs = (SetShader_fn)vtbl[OV_VT_SetVertexShader];
    getps = (GetShader_fn)vtbl[OV_VT_GetPixelShader];
    setps = (SetShader_fn)vtbl[OV_VT_SetPixelShader];
    getvd = (GetShader_fn)vtbl[OV_VT_GetVertexDeclaration];
    setvd = (SetShader_fn)vtbl[OV_VT_SetVertexDeclaration];
    getfvf = (GetFVF_fn)vtbl[OV_VT_GetFVF];

    if (getrs) {
        getrs(dev, D3DRS_ALPHABLENDENABLE, &old_aa);
        getrs(dev, D3DRS_ZENABLE, &old_zb);
        getrs(dev, D3DRS_ZWRITEENABLE, &old_zw);
        getrs(dev, D3DRS_FOGENABLE, &old_fog);
        getrs(dev, D3DRS_LIGHTING, &old_lit);
        getrs(dev, D3DRS_CULLMODE, &old_cull);
        getrs(dev, D3DRS_STENCILENABLE, &old_st);
        getrs(dev, D3DRS_SCISSORTESTENABLE, &old_sc);
        getrs(dev, D3DRS_COLORWRITEENABLE, &old_cw);
    }
    if (getfvf)
        getfvf(dev, &old_fvf);
    if (getvs) getvs(dev, &old_vs);
    if (getps) getps(dev, &old_ps);
    if (getvd) getvd(dev, &old_vd);

    /* FVF XYZRHW is ignored while a VS / declaration is bound — WoW UI uses both. */
    if (setvd) setvd(dev, NULL);
    if (setvs) setvs(dev, NULL);
    if (setps) setps(dev, NULL);
    if (sett) {
        sett(dev, 0, NULL);
        sett(dev, 1, NULL);
    }
    if (setrs) {
        setrs(dev, D3DRS_ALPHABLENDENABLE, TRUE);
        setrs(dev, D3DRS_SRCBLEND, D3DBLEND_SRCALPHA);
        setrs(dev, D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
        setrs(dev, D3DRS_ZENABLE, FALSE);
        setrs(dev, D3DRS_ZWRITEENABLE, FALSE);
        setrs(dev, D3DRS_FOGENABLE, FALSE);
        setrs(dev, D3DRS_LIGHTING, FALSE);
        setrs(dev, D3DRS_CULLMODE, D3DCULL_NONE);
        setrs(dev, D3DRS_STENCILENABLE, FALSE);
        setrs(dev, D3DRS_SCISSORTESTENABLE, FALSE);
        setrs(dev, D3DRS_COLORWRITEENABLE, 0xF);
    }

    for (i = 0; i < n; i++) {
        DWORD col = ARGB(local[i].a, local[i].r, local[i].g, local[i].b);
        if (local[i].type == kDrawLine)
            BatchLine(local[i].x1, local[i].y1, local[i].x2, local[i].y2, col);
        else if (local[i].type == kDrawRect)
            BatchRect(local[i].x1, local[i].y1, local[i].x2, local[i].y2, col);
        else if (local[i].type == kDrawCircle)
            BatchCircle(local[i].x1, local[i].y1, local[i].x2, col);
        else if (local[i].type == kDrawWorldLine)
            BatchWorldLine(dev, local[i].x1, local[i].y1, local[i].z1,
                           local[i].x2, local[i].y2, local[i].z2, col);
        else if (local[i].type == kDrawWorldBox)
            BatchWorldBox(dev, local[i].x1, local[i].y1, local[i].z1,
                          local[i].x2, local[i].y2, col);
        else if (local[i].type == kDrawText && local[i].text[0])
            BatchText(local[i].x1, local[i].y1, local[i].text, col);

        if (g_batch_n + 64 > OV_MAX_BATCH_VERTS)
            FlushBatch(dev);
    }

    if (want_esp)
        DrawLootEspNative(dev);

    FlushBatch(dev);

    if (setvd) setvd(dev, old_vd);
    if (setvs) setvs(dev, old_vs);
    if (setps) setps(dev, old_ps);
    ComRelease(old_vs);
    ComRelease(old_ps);
    ComRelease(old_vd);
    if (old_fvf && vtbl[OV_VT_SetFVF])
        ((SetFVF_fn)vtbl[OV_VT_SetFVF])(dev, old_fvf);

    if (setrs) {
        setrs(dev, D3DRS_ALPHABLENDENABLE, old_aa);
        setrs(dev, D3DRS_ZENABLE, old_zb);
        setrs(dev, D3DRS_ZWRITEENABLE, old_zw);
        setrs(dev, D3DRS_FOGENABLE, old_fog);
        setrs(dev, D3DRS_LIGHTING, old_lit);
        setrs(dev, D3DRS_CULLMODE, old_cull);
        setrs(dev, D3DRS_STENCILENABLE, old_st);
        setrs(dev, D3DRS_SCISSORTESTENABLE, old_sc);
        if (old_cw)
            setrs(dev, D3DRS_COLORWRITEENABLE, old_cw);
    }
}

static HRESULT __stdcall HookedEndScene(IDirect3DDevice9* this)
{
    InterlockedIncrement(&g_frames);
    if (this) {
        g_dev = this;
        /* Present is the backbuffer. EndScene may be a shadow/UI pass. */
        if (InterlockedCompareExchange(&g_presents_ever, 0, 0) == 0)
            RenderOverlay(this);
    }
    return g_real_endscene(this);
}

static HRESULT __stdcall HookedPresent(IDirect3DDevice9* this,
    const RECT* s, const RECT* d, HWND h, const RGNDATA* dirty)
{
    InterlockedIncrement(&g_presents_ever);
    if (this) {
        g_dev = this;
        InterlockedExchange(&g_drew_this_frame, 0);
        RenderOverlay(this);
    }
    InterlockedExchange(&g_drew_this_frame, 0);
    return g_real_present(this, s, d, h, dirty);
}

static HRESULT __stdcall HookedSetTransform(IDirect3DDevice9* this, DWORD state, const OvMatrix* matrix)
{
    if (this && matrix) {
        if (state == D3DTS_VIEW && MatLooksLikeView(matrix)) {
            g_view = *matrix;
            g_have_view = 1;
            g_dev = this;
        } else if (state == D3DTS_PROJECTION && MatLooksLikeProj(matrix)) {
            g_proj = *matrix;
            g_have_proj = 1;
            g_dev = this;
        }
    }
    return g_real_settransform(this, state, matrix);
}

static void HookDevice(IDirect3DDevice9* dev);

static HRESULT __stdcall HookedReset(IDirect3DDevice9* this, void* pp)
{
    HRESULT hr;
    g_have_view = 0;
    g_have_proj = 0;
    hr = g_real_reset(this, pp);
    if (SUCCEEDED(hr)) {
        /* Shared vtable — hooks stay; re-bind device pointer. */
        g_dev = this;
        InterlockedExchange(&g_hooked, 1);
    }
    return hr;
}

static int PatchVtblSlot(void** slot, void* hook, void** out_real)
{
    DWORD old;
    if (!slot || !hook) return 0;
    if (*slot == hook) {
        if (out_real && !*out_real) {
            /* Already hooked by us earlier but real ptr lost — cannot recover. */
        }
        return 1;
    }
    if (!VirtualProtect(slot, sizeof(void*), PAGE_EXECUTE_READWRITE, &old))
        return 0;
    if (out_real && !*out_real) *out_real = *slot;
    else if (out_real && *out_real == NULL) *out_real = *slot;
    if (out_real) {
        if (*out_real == NULL || *out_real == hook)
            *out_real = *slot;
    }
    *slot = hook;
    VirtualProtect(slot, sizeof(void*), old, &old);
    return 1;
}

static void HookDevice(IDirect3DDevice9* dev)
{
    void** vtbl;
    if (!dev) return;
    vtbl = *(void***)dev;
    if (!vtbl) return;

    if (!g_real_endscene || vtbl[OV_VT_EndScene] != (void*)&HookedEndScene) {
        void* real = g_real_endscene;
        if (PatchVtblSlot(&vtbl[OV_VT_EndScene], (void*)&HookedEndScene, &real)) {
            if (!g_real_endscene) g_real_endscene = (EndScene_fn)real;
            LogO("EndScene vtable hooked");
        }
    }
    if (!g_real_present || vtbl[OV_VT_Present] != (void*)&HookedPresent) {
        void* real = g_real_present;
        if (PatchVtblSlot(&vtbl[OV_VT_Present], (void*)&HookedPresent, &real)) {
            if (!g_real_present) g_real_present = (Present_fn)real;
            LogO("Present vtable hooked");
        }
    }
    if (!g_real_settransform || vtbl[OV_VT_SetTransform] != (void*)&HookedSetTransform) {
        void* real = g_real_settransform;
        if (PatchVtblSlot(&vtbl[OV_VT_SetTransform], (void*)&HookedSetTransform, &real)) {
            if (!g_real_settransform) g_real_settransform = (SetTransform_fn)real;
            LogO("SetTransform vtable hooked");
        }
    }
    if (!g_real_reset || vtbl[OV_VT_Reset] != (void*)&HookedReset) {
        void* real = g_real_reset;
        if (PatchVtblSlot(&vtbl[OV_VT_Reset], (void*)&HookedReset, &real)) {
            if (!g_real_reset) g_real_reset = (Reset_fn)real;
            LogO("Reset vtable hooked");
        }
    }
    g_dev = dev;
    InterlockedExchange(&g_hooked, 1);
}

static HRESULT __stdcall HookedCreateDevice(
    IDirect3D9* this, UINT adapter, UINT type, HWND hwnd, DWORD flags,
    void* pp, IDirect3DDevice9** out)
{
    HRESULT hr = g_real_create_device(this, adapter, type, hwnd, flags, pp, out);
    if (SUCCEEDED(hr) && out && *out)
        HookDevice(*out);
    return hr;
}

static IDirect3D9* WINAPI HookedDirect3DCreate9(UINT sdk)
{
    IDirect3D9* d3d;
    void** vtbl;
    if (!g_real_create9) return NULL;
    d3d = g_real_create9(sdk);
    if (!d3d) return NULL;
    vtbl = *(void***)d3d;
    if (vtbl && !g_real_create_device) {
        if (PatchVtblSlot(&vtbl[16], (void*)&HookedCreateDevice, (void**)&g_real_create_device))
            LogO("CreateDevice vtable hooked");
    }
    return d3d;
}

static int PatchIatDirect3DCreate9(HMODULE mod)
{
    uint8_t* base;
    IMAGE_DOS_HEADER* dos;
    IMAGE_NT_HEADERS* nt;
    IMAGE_IMPORT_DESCRIPTOR* imp;
    if (!mod) return 0;
    base = (uint8_t*)mod;
    dos = (IMAGE_DOS_HEADER*)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    nt = (IMAGE_NT_HEADERS*)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;
    if (!nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress)
        return 0;
    imp = (IMAGE_IMPORT_DESCRIPTOR*)(base +
        nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress);
    for (; imp->Name; ++imp) {
        char* name = (char*)(base + imp->Name);
        IMAGE_THUNK_DATA* oft;
        IMAGE_THUNK_DATA* iat;
        if (_stricmp(name, "d3d9.dll") != 0) continue;
        oft = (IMAGE_THUNK_DATA*)(base + (imp->OriginalFirstThunk ? imp->OriginalFirstThunk : imp->FirstThunk));
        iat = (IMAGE_THUNK_DATA*)(base + imp->FirstThunk);
        for (; oft->u1.AddressOfData; ++oft, ++iat) {
            IMAGE_IMPORT_BY_NAME* ibn;
            if (oft->u1.Ordinal & IMAGE_ORDINAL_FLAG32) continue;
            ibn = (IMAGE_IMPORT_BY_NAME*)(base + oft->u1.AddressOfData);
            if (strcmp(ibn->Name, "Direct3DCreate9") == 0) {
                DWORD old;
                if ((void*)iat->u1.Function == (void*)&HookedDirect3DCreate9)
                    return 1;
                g_real_create9 = (Direct3DCreate9_fn)iat->u1.Function;
                g_iat_create9_slot = (void**)&iat->u1.Function;
                if (!VirtualProtect(&iat->u1.Function, sizeof(void*), PAGE_EXECUTE_READWRITE, &old))
                    return 0;
                iat->u1.Function = (ULONG_PTR)&HookedDirect3DCreate9;
                VirtualProtect(&iat->u1.Function, sizeof(void*), old, &old);
                LogO("IAT Direct3DCreate9 patched");
                return 1;
            }
        }
    }
    return 0;
}

static int PatchAllModuleIats(void)
{
    HMODULE mods[256];
    DWORD needed = 0;
    unsigned i, n;
    int hits = 0;
    typedef BOOL(WINAPI* K32EnumProcessModules_fn)(HANDLE, HMODULE*, DWORD, LPDWORD);
    K32EnumProcessModules_fn enm;
    HMODULE psapi = LoadLibraryA("psapi.dll");
    if (!psapi)
        return PatchIatDirect3DCreate9(GetModuleHandleA(NULL));
    enm = (K32EnumProcessModules_fn)GetProcAddress(psapi, "EnumProcessModules");
    if (!enm) {
        FreeLibrary(psapi);
        return PatchIatDirect3DCreate9(GetModuleHandleA(NULL));
    }
    if (!enm(GetCurrentProcess(), mods, sizeof(mods), &needed)) {
        FreeLibrary(psapi);
        return PatchIatDirect3DCreate9(GetModuleHandleA(NULL));
    }
    n = needed / sizeof(HMODULE);
    if (n > 256) n = 256;
    for (i = 0; i < n; i++) {
        if (PatchIatDirect3DCreate9(mods[i]))
            hits++;
    }
    FreeLibrary(psapi);
    if (!hits)
        hits = PatchIatDirect3DCreate9(GetModuleHandleA(NULL)) ? 1 : 0;
    return hits;
}

typedef struct { HWND best; DWORD pid; LONG bestArea; } FindWndCtx;

static BOOL CALLBACK EnumWndCb(HWND hwnd, LPARAM lp)
{
    FindWndCtx* ctx = (FindWndCtx*)lp;
    DWORD pid = 0;
    char title[128];
    RECT rc;
    LONG area;
    if (!IsWindowVisible(hwnd)) return TRUE;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != ctx->pid) return TRUE;
    if (!GetWindowTextA(hwnd, title, sizeof(title)) || !title[0]) return TRUE;
    if (!GetClientRect(hwnd, &rc)) return TRUE;
    area = (rc.right - rc.left) * (rc.bottom - rc.top);
    if (!ctx->best || area > ctx->bestArea) {
        ctx->bestArea = area;
        ctx->best = hwnd;
    }
    return TRUE;
}

static HWND FindGameHwnd(void)
{
    FindWndCtx ctx;
    ctx.best = NULL;
    ctx.bestArea = 0;
    ctx.pid = GetCurrentProcessId();
    EnumWindows(EnumWndCb, (LPARAM)&ctx);
    return ctx.best;
}

/*
 * Create a throwaway D3D9 device. Its IDirect3DDevice9 vtable lives in d3d9.dll
 * and is SHARED with the game's device — patching EndScene here hooks the live
 * renderer even when CreateDevice already ran before we injected.
 */
static int TrySharedVtblViaDummy(void)
{
    Direct3DCreate9_fn create9;
    IDirect3D9* d3d;
    IDirect3DDevice9* dev = NULL;
    OvPresentParams pp;
    HWND hwnd;
    HRESULT hr;
    char buf[160];

    if (g_hooked && g_real_endscene) return 1;
    if (!g_d3d9) g_d3d9 = LoadLibraryA("d3d9.dll");
    if (!g_d3d9) return 0;
    create9 = (Direct3DCreate9_fn)GetProcAddress(g_d3d9, "Direct3DCreate9");
    if (!create9) return 0;

    hwnd = FindGameHwnd();
    if (!hwnd) {
        hwnd = CreateWindowExA(0, "STATIC", "ExtProxyOv", WS_POPUP,
                               0, 0, 64, 64, NULL, NULL, GetModuleHandleA(NULL), NULL);
    }
    if (!hwnd) return 0;

    d3d = create9(D3D_SDK_VERSION);
    if (!d3d) return 0;

    memset(&pp, 0, sizeof(pp));
    pp.Windowed = TRUE;
    pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.hDeviceWindow = hwnd;
    pp.BackBufferFormat = D3DFMT_UNKNOWN;
    pp.PresentationInterval = D3DPRESENT_INTERVAL_DEFAULT;

    /* CreateDevice on IDirect3D9 — vtable slot 16 */
    {
        void** vtbl = *(void***)d3d;
        CreateDevice_fn cd = (CreateDevice_fn)vtbl[16];
        hr = cd(d3d, D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, hwnd,
                D3DCREATE_SOFTWARE_VERTEXPROCESSING, &pp, &dev);
        if (FAILED(hr)) {
            hr = cd(d3d, D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, hwnd,
                    D3DCREATE_MIXED_VERTEXPROCESSING, &pp, &dev);
        }
    }

    if (FAILED(hr) || !dev) {
        /* Release IDirect3D9 */
        void** vtbl = *(void***)d3d;
        Release_fn rel = (Release_fn)vtbl[2];
        if (rel) rel((IDirect3DDevice9*)d3d);
        LogO("dummy CreateDevice failed");
        return 0;
    }

    HookDevice(dev);
    g_dummy_dev = dev; /* keep alive so vtable page stays mapped */

    {
        void** vtbl = *(void***)d3d;
        Release_fn rel = (Release_fn)vtbl[2];
        if (rel) rel((IDirect3DDevice9*)d3d);
    }

    _snprintf(buf, sizeof(buf), "shared-vtable hook via dummy dev=%p endscene=%p",
              (void*)dev, (void*)g_real_endscene);
    LogO(buf);
    return g_hooked ? 1 : 0;
}

static DWORD WINAPI OverlayLateHookThread(LPVOID p)
{
    int i;
    char buf[160];
    (void)p;
    for (i = 0; i < 120 && !g_hooked; i++) {
        if (!g_real_create9)
            PatchAllModuleIats();
        if (!g_hooked && i >= 2)
            TrySharedVtblViaDummy();
        Sleep(250);
    }
    /* Keep retrying shared vtable periodically even if IAT path "succeeded"
     * without CreateDevice (common when DLL injects after device exists). */
    for (; i < 200; i++) {
        if (g_hooked && g_real_endscene) break;
        TrySharedVtblViaDummy();
        Sleep(500);
    }
    _snprintf(buf, sizeof(buf), "overlay ready hooked=%ld create9=%p endscene=%p present=%p",
              (long)g_hooked, (void*)g_real_create9, (void*)g_real_endscene, (void*)g_real_present);
    LogO(buf);
    if (!g_hooked)
        LogO("overlay FAILED — no EndScene hook; check d3d9 / restart client");
    return 0;
}

void Overlay_Init(void)
{
    if (g_overlay_ready) return;
    if (!g_cs_init) {
        InitializeCriticalSection(&g_draw_cs);
        g_cs_init = 1;
    }
    memset(&g_view, 0, sizeof(g_view));
    memset(&g_proj, 0, sizeof(g_proj));
    g_d3d9 = LoadLibraryA("d3d9.dll");
    if (!g_d3d9) {
        LogO("d3d9.dll missing");
        return;
    }
    if (!PatchAllModuleIats())
        LogO("IAT patch deferred — retry thread");
    /* Immediate shared-vtable attempt (covers late inject). */
    TrySharedVtblViaDummy();
    g_late_hook_thread = CreateThread(NULL, 0, OverlayLateHookThread, NULL, 0, NULL);
    g_overlay_ready = 1;
    LogO("Overlay_Init (EndScene+Present, double-buffer, 30Hz publish)");
}

void Overlay_Shutdown(void)
{
    DWORD old;
    Overlay_Clear();
    Overlay_EndFrame();
    InterlockedExchange(&g_hooked, 0);

    if (g_dev) {
        void** vtbl = *(void***)g_dev;
        if (vtbl) {
            if (g_real_endscene)
                PatchVtblSlot(&vtbl[OV_VT_EndScene], (void*)g_real_endscene, NULL);
            if (g_real_present)
                PatchVtblSlot(&vtbl[OV_VT_Present], (void*)g_real_present, NULL);
            if (g_real_settransform)
                PatchVtblSlot(&vtbl[OV_VT_SetTransform], (void*)g_real_settransform, NULL);
            if (g_real_reset)
                PatchVtblSlot(&vtbl[OV_VT_Reset], (void*)g_real_reset, NULL);
        }
        g_dev = NULL;
    }

    if (g_dummy_dev) {
        void** vtbl = *(void***)g_dummy_dev;
        Release_fn rel = vtbl ? (Release_fn)vtbl[2] : NULL;
        if (rel) rel(g_dummy_dev);
        g_dummy_dev = NULL;
    }

    if (g_iat_create9_slot && g_real_create9) {
        if (VirtualProtect(g_iat_create9_slot, sizeof(void*), PAGE_EXECUTE_READWRITE, &old)) {
            *g_iat_create9_slot = (void*)g_real_create9;
            VirtualProtect(g_iat_create9_slot, sizeof(void*), old, &old);
        }
        g_iat_create9_slot = NULL;
    }

    g_real_endscene = NULL;
    g_real_present = NULL;
    g_real_settransform = NULL;
    g_real_reset = NULL;
    g_real_create_device = NULL;
    g_real_create9 = NULL;

    if (g_late_hook_thread) {
        WaitForSingleObject(g_late_hook_thread, 500);
        CloseHandle(g_late_hook_thread);
        g_late_hook_thread = NULL;
    }
    if (g_d3d9) {
        FreeLibrary(g_d3d9);
        g_d3d9 = NULL;
    }
    g_overlay_ready = 0;
    LogO("Overlay_Shutdown restored vtables/IAT");
}
