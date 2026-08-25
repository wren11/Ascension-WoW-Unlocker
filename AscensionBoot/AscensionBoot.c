#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include <string.h>

static const char kOldName[] = "Extensions.dll";
static const char kNewName[] = "ExtProxy64.dll";

static void FailMsg(const wchar_t* text, DWORD err)
{
    wchar_t buf[1024];
    if (err)
        wsprintfW(buf, L"%s\n\nWin32=%u", text, err);
    else
        lstrcpyW(buf, text);
    MessageBoxW(NULL, buf, L"AscensionBoot", MB_OK | MB_ICONERROR);
}

static int CountAsciiZ(const BYTE* buf, DWORD size, const char* needle, size_t needleBytes)
{
    DWORD i;
    int n = 0;
    if (needleBytes == 0 || size < needleBytes)
        return 0;
    for (i = 0; i + needleBytes <= size; ++i) {
        if (memcmp(buf + i, needle, needleBytes) == 0)
            n++;
    }
    return n;
}

/* Returns 1 if launch exe already loads ExtProxy64 (no Extensions.dll string left). */
static int IsAlreadyPatched(const wchar_t* path, DWORD* outErr)
{
    HANDLE f;
    DWORD size, read;
    BYTE* buf;
    int oldN, newN;

    if (outErr) *outErr = 0;
    f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) {
        if (outErr) *outErr = GetLastError();
        return 0;
    }
    size = GetFileSize(f, NULL);
    if (size == INVALID_FILE_SIZE || size < 64) {
        CloseHandle(f);
        if (outErr) *outErr = ERROR_INVALID_DATA;
        return 0;
    }
    buf = (BYTE*)HeapAlloc(GetProcessHeap(), 0, size);
    if (!buf) {
        CloseHandle(f);
        if (outErr) *outErr = ERROR_NOT_ENOUGH_MEMORY;
        return 0;
    }
    if (!ReadFile(f, buf, size, &read, NULL) || read != size) {
        HeapFree(GetProcessHeap(), 0, buf);
        CloseHandle(f);
        if (outErr) *outErr = GetLastError();
        return 0;
    }
    CloseHandle(f);
    oldN = CountAsciiZ(buf, size, kOldName, sizeof(kOldName));
    newN = CountAsciiZ(buf, size, kNewName, sizeof(kNewName));
    HeapFree(GetProcessHeap(), 0, buf);
    return (oldN == 0 && newN > 0) ? 1 : 0;
}

/*
 * Patch "Extensions.dll" → "ExtProxy64.dll" (same length including NUL).
 * Return:
 *   >0  patches applied
 *    0  already patched / nothing to do
 *   <0  failure; *outWin32 holds GetLastError (or synthetic)
 */
static int PatchExtensionsString(const wchar_t* path, DWORD* outWin32)
{
    HANDLE f;
    DWORD size, read, written, i, err;
    BYTE* buf;
    int patches = 0;
    int attempts;

    if (outWin32) *outWin32 = 0;

    /* Fast path: already redirected — no exclusive write needed. */
    if (IsAlreadyPatched(path, &err))
        return 0;

    for (attempts = 0; attempts < 8; ++attempts) {
        if (attempts > 0)
            Sleep(150 * attempts);

        SetFileAttributesW(path, FILE_ATTRIBUTE_NORMAL);
        f = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL, NULL);
        if (f != INVALID_HANDLE_VALUE)
            break;

        err = GetLastError();
        if (outWin32) *outWin32 = err;

        /* Sharing violation / lock — retry; maybe game still closing. */
        if (err == ERROR_SHARING_VIOLATION || err == ERROR_LOCK_VIOLATION
            || err == ERROR_ACCESS_DENIED) {
            /* If another handle has it open but content is already patched, succeed. */
            if (IsAlreadyPatched(path, NULL))
                return 0;
            continue;
        }
        return -1;
    }

    if (f == INVALID_HANDLE_VALUE) {
        if (IsAlreadyPatched(path, NULL))
            return 0;
        return -1;
    }

    size = GetFileSize(f, NULL);
    if (size == INVALID_FILE_SIZE || size < 64) {
        CloseHandle(f);
        if (outWin32) *outWin32 = ERROR_INVALID_DATA;
        return -2;
    }

    buf = (BYTE*)HeapAlloc(GetProcessHeap(), 0, size);
    if (!buf) {
        CloseHandle(f);
        if (outWin32) *outWin32 = ERROR_NOT_ENOUGH_MEMORY;
        return -3;
    }

    if (!ReadFile(f, buf, size, &read, NULL) || read != size) {
        if (outWin32) *outWin32 = GetLastError();
        HeapFree(GetProcessHeap(), 0, buf);
        CloseHandle(f);
        return -4;
    }

    for (i = 0; i + sizeof(kOldName) <= size; ++i) {
        if (memcmp(buf + i, kOldName, sizeof(kOldName)) == 0) {
            memcpy(buf + i, kNewName, sizeof(kNewName));
            patches++;
        }
    }

    if (patches == 0) {
        int newN = CountAsciiZ(buf, size, kNewName, sizeof(kNewName));
        HeapFree(GetProcessHeap(), 0, buf);
        CloseHandle(f);
        if (newN > 0)
            return 0; /* already ExtProxy */
        if (outWin32) *outWin32 = ERROR_NOT_FOUND;
        return -6; /* stock string missing — wrong EXE */
    }

    SetFilePointer(f, 0, NULL, FILE_BEGIN);
    if (!WriteFile(f, buf, size, &written, NULL) || written != size) {
        if (outWin32) *outWin32 = GetLastError();
        HeapFree(GetProcessHeap(), 0, buf);
        CloseHandle(f);
        return -5;
    }

    HeapFree(GetProcessHeap(), 0, buf);
    CloseHandle(f);
    return patches;
}

static int GetBootDir(wchar_t* out, size_t outChars)
{
    wchar_t* slash;
    DWORD n = GetModuleFileNameW(NULL, out, (DWORD)outChars);
    if (n == 0 || n >= outChars)
        return 0;
    slash = wcsrchr(out, L'\\');
    if (!slash)
        return 0;
    slash[1] = 0;
    return 1;
}

/* Beside this exe, then ../ExtProxy/ (separate project layout). */
static int ResolveExtProxyDll(const wchar_t* bootDir, wchar_t* out, size_t outChars)
{
    wchar_t raw[MAX_PATH];
    const wchar_t* tries[2];
    int i;
    tries[0] = L"ExtProxy64.dll";
    tries[1] = L"..\\ExtProxy\\ExtProxy64.dll";
    for (i = 0; i < 2; ++i) {
        lstrcpyW(raw, bootDir);
        lstrcatW(raw, tries[i]);
        if (!GetFullPathNameW(raw, (DWORD)outChars, out, NULL))
            continue;
        if (GetFileAttributesW(out) != INVALID_FILE_ATTRIBUTES)
            return 1;
    }
    return 0;
}

static int EnsureTrailingSlash(wchar_t* path, size_t outChars)
{
    size_t n = wcslen(path);
    if (n == 0 || n + 2 >= outChars)
        return 0;
    if (path[n - 1] != L'\\' && path[n - 1] != L'/') {
        path[n] = L'\\';
        path[n + 1] = 0;
    }
    return 1;
}

/*
 * Ascension resolves Data/ MPQs from the EXE directory (GetModuleFileName),
 * NOT from cwd.
 *
 * Correct portable layout (GMToolBox dist):
 *   dist\AscensionBoot.exe          = injector (this file)
 *   dist\ExtProxy64.dll             = source of truth
 *   dist\Runtime\Ascension.launch.exe = patched stock copy
 *   dist\Runtime\ExtProxy64.dll     = staged from dist\
 *   dist\Runtime\Data\              = junction → Ascension install Data\
 *   cwd = Runtime
 *
 * Never write ExtProxy into the Ascension installation directory.
 */
int wmain(int argc, wchar_t** argv)
{
    wchar_t bootDir[MAX_PATH];
    wchar_t launchPath[MAX_PATH];
    wchar_t runtimeDir[MAX_PATH];
    wchar_t dataCheck[MAX_PATH];
    wchar_t srcDll[MAX_PATH];
    wchar_t dstDll[MAX_PATH];
    wchar_t params[4096];
    wchar_t detail[512];
    SHELLEXECUTEINFOW sei;
    int i, patches;
    const wchar_t* stockExe;
    DWORD err = 0;
    DWORD patchErr = 0;

    if (argc < 3) {
        MessageBoxW(NULL,
            L"Usage: AscensionBoot.exe <stock-Ascension.exe> <runtime-dir> [args...]\n\n"
            L"Stages ExtProxy64.dll from THIS folder into <runtime-dir>,\n"
            L"writes Ascension.launch.exe there (must contain Data\\ junction),\n"
            L"then starts it. Never writes into the Ascension install.",
            L"AscensionBoot", MB_OK | MB_ICONINFORMATION);
        return 1;
    }

    if (!GetBootDir(bootDir, MAX_PATH))
        return 2;

    stockExe = argv[1];
    if (GetFileAttributesW(stockExe) == INVALID_FILE_ATTRIBUTES) {
        FailMsg(L"Stock Ascension.exe not found.", GetLastError());
        return 3;
    }

    lstrcpyW(runtimeDir, argv[2]);
    if (!EnsureTrailingSlash(runtimeDir, MAX_PATH))
        return 3;
    if (GetFileAttributesW(runtimeDir) == INVALID_FILE_ATTRIBUTES) {
        if (!CreateDirectoryW(runtimeDir, NULL)
            && GetLastError() != ERROR_ALREADY_EXISTS) {
            FailMsg(L"Could not create runtime directory.", GetLastError());
            return 4;
        }
    }

    lstrcpyW(dataCheck, runtimeDir);
    lstrcatW(dataCheck, L"Data");
    if (GetFileAttributesW(dataCheck) == INVALID_FILE_ATTRIBUTES) {
        FailMsg(
            L"Runtime Data\\ is missing.\n"
            L"GMToolBox must create a junction to the Ascension Data folder first.",
            0);
        return 8;
    }

    if (!ResolveExtProxyDll(bootDir, srcDll, MAX_PATH)) {
        FailMsg(
            L"Missing ExtProxy64.dll beside AscensionBoot.exe or in ..\\ExtProxy\\.\n"
            L"Build ExtProxy, then this project.",
            0);
        return 6;
    }

    /* Launch exe MUST live beside Data\ (runtime dir — not the Ascension install). */
    lstrcpyW(launchPath, runtimeDir);
    lstrcatW(launchPath, L"Ascension.launch.exe");
    if (!CopyFileW(stockExe, launchPath, FALSE)) {
        err = GetLastError();
        /* Destination locked by a running client — OK if already patched. */
        if (err == ERROR_SHARING_VIOLATION || err == ERROR_LOCK_VIOLATION
            || err == ERROR_ACCESS_DENIED) {
            if (!IsAlreadyPatched(launchPath, NULL)) {
                FailMsg(
                    L"Ascension.launch.exe is locked (client still running).\n"
                    L"Close the game, then Launch again.",
                    err);
                return 4;
            }
        } else if (GetFileAttributesW(launchPath) == INVALID_FILE_ATTRIBUTES) {
            FailMsg(L"Could not create Ascension.launch.exe in the runtime folder.", err);
            return 4;
        }
    }
    SetFileAttributesW(launchPath, FILE_ATTRIBUTE_NORMAL);

    patches = PatchExtensionsString(launchPath, &patchErr);
    if (patches < 0) {
        if (patchErr == ERROR_SHARING_VIOLATION || patchErr == ERROR_LOCK_VIOLATION
            || patchErr == ERROR_ACCESS_DENIED) {
            FailMsg(
                L"Could not patch Ascension.launch.exe — file is locked.\n"
                L"Close every Ascension.launch / Ascension window, then Launch again.",
                patchErr);
        } else if (patches == -6) {
            FailMsg(
                L"Ascension.launch.exe has no Extensions.dll import string.\n"
                L"Wrong stock EXE, or binary already rewritten oddly. Re-copy from Ascension live.",
                patchErr);
        } else {
            wsprintfW(detail,
                L"Failed to patch Extensions.dll → ExtProxy64.dll in launch exe.\n"
                L"(patch-code=%d)", patches);
            FailMsg(detail, patchErr ? patchErr : (DWORD)(-patches));
        }
        return 5;
    }

    lstrcpyW(dstDll, runtimeDir);
    lstrcatW(dstDll, L"ExtProxy64.dll");
    /* Stage via .new then replace — survives a briefly-locked destination DLL. */
    {
        wchar_t dstNew[MAX_PATH];
        int staged = 0;
        int attempt;
        lstrcpyW(dstNew, dstDll);
        lstrcatW(dstNew, L".new");
        for (attempt = 0; attempt < 6 && !staged; ++attempt) {
            if (attempt) Sleep(120 * attempt);
            SetFileAttributesW(dstDll, FILE_ATTRIBUTE_NORMAL);
            SetFileAttributesW(dstNew, FILE_ATTRIBUTE_NORMAL);
            DeleteFileW(dstNew);
            if (!CopyFileW(srcDll, dstNew, FALSE)) {
                err = GetLastError();
                continue;
            }
            if (MoveFileExW(dstNew, dstDll, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
                staged = 1;
                break;
            }
            err = GetLastError();
            /* Same bytes already present + locked is fine — ExtProxy will load what's there. */
            if (err == ERROR_SHARING_VIOLATION || err == ERROR_ACCESS_DENIED) {
                DeleteFileW(dstNew);
                if (GetFileAttributesW(dstDll) != INVALID_FILE_ATTRIBUTES) {
                    staged = 1;
                    break;
                }
            }
        }
        DeleteFileW(dstNew);
        if (!staged) {
            FailMsg(L"Could not stage ExtProxy64.dll into the runtime folder.", err);
            return 6;
        }
    }
    SetFileAttributesW(dstDll, FILE_ATTRIBUTE_NORMAL);

    params[0] = 0;
    for (i = 3; i < argc; ++i) {
        if (params[0])
            lstrcatW(params, L" ");
        lstrcatW(params, argv[i]);
    }

    ZeroMemory(&sei, sizeof(sei));
    sei.cbSize = sizeof(sei);
    sei.fMask = SEE_MASK_NOCLOSEPROCESS;
    sei.lpVerb = L"open";
    sei.lpFile = launchPath;
    sei.lpParameters = params[0] ? params : NULL;
    sei.lpDirectory = runtimeDir;
    sei.nShow = SW_SHOWNORMAL;

    if (!ShellExecuteExW(&sei)) {
        FailMsg(
            L"Failed to start Ascension.launch.exe.\n"
            L"If UAC appeared and was denied, approve elevation.",
            GetLastError());
        return 7;
    }

    if (sei.hProcess)
        CloseHandle(sei.hProcess);
    return 0;
}
