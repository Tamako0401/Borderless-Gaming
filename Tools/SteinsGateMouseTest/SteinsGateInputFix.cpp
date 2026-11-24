#include <windows.h>
#include <bcrypt.h>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <cwchar>

static_assert(sizeof(void*) == 4, "STEINS;GATE Steam Game.exe is x86");
#pragma comment(linker, "/export:SgInstallMouseMapping=_SgInstallMouseMapping@16")

namespace
{
    constexpr wchar_t MessageName[] = L"BorderlessGaming.SteinsGate.MouseMapping.v2";
    constexpr wchar_t OwnerProperty[] = L"BorderlessGaming.SteinsGate.MouseOwner.v2";
    constexpr wchar_t RawProperty[] = L"BorderlessGaming.SteinsGate.MouseRaw.v2";
    constexpr wchar_t MappedProperty[] = L"BorderlessGaming.SteinsGate.MouseMapped.v2";

    // Steam Game.exe, x86, linker timestamp 2019-11-28.
    constexpr DWORD GameTimestamp = 0x5DDF7B17;
    constexpr DWORD GetCursorPosImport = 0xB71AC;
    constexpr unsigned char GameHash[] = {
        0x8e, 0x7e, 0xb8, 0x46, 0x92, 0xe9, 0xdd, 0x47,
        0xa0, 0x48, 0x7f, 0x9f, 0x24, 0x2d, 0x8e, 0x17,
        0x55, 0xbb, 0x18, 0x46, 0x43, 0xd7, 0x35, 0xe6,
        0x89, 0x92, 0xf4, 0xf6, 0x4b, 0x38, 0x73, 0x68
    };

    enum InstallResult : DWORD
    {
        Installed = 0,
        InvalidWindow = 1,
        GameBuildNotSupported = 2,
        ThreadHookFailed = 3,
        GameDidNotAcknowledge = 4
    };

    using CursorFn = BOOL (WINAPI*)(LPPOINT);
    std::atomic<CursorFn> originalCursor{nullptr};
    SRWLOCK sessionLock = SRWLOCK_INIT;
    struct Session
    {
        HWND game;
        HWND owner;
        DWORD ownerPid;
        RECT client;
        LONG nativeWidth;
        LONG nativeHeight;
        bool logged;
    };
    Session session{};

    bool IsSupportedGameFile(DWORD pid)
    {
        const HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (!process)
            return false;

        wchar_t path[32768];
        DWORD length = ARRAYSIZE(path);
        const bool havePath = !!QueryFullProcessImageNameW(process, 0, path, &length);
        CloseHandle(process);
        if (!havePath)
            return false;

        const auto name = std::wcsrchr(path, L'\\');
        if (!name || _wcsicmp(name + 1, L"Game.exe") != 0)
            return false;

        const HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return false;

        LARGE_INTEGER size{};
        const bool hasSize = GetFileSizeEx(file, &size) && size.QuadPart > 0 &&
            size.QuadPart <= 16 * 1024 * 1024;
        const HANDLE mapping = hasSize ? CreateFileMappingW(file, nullptr, PAGE_READONLY, 0, 0, nullptr) : nullptr;
        const auto bytes = mapping ? static_cast<const UCHAR*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0)) : nullptr;

        unsigned char digest[sizeof(GameHash)]{};
        BCRYPT_ALG_HANDLE algorithm = nullptr;
        const bool matches = bytes &&
            BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0 &&
            BCryptHash(algorithm, nullptr, 0, const_cast<UCHAR*>(bytes),
                static_cast<ULONG>(size.QuadPart), digest, sizeof(digest)) >= 0 &&
            std::memcmp(digest, GameHash, sizeof(digest)) == 0;

        if (algorithm)
            BCryptCloseAlgorithmProvider(algorithm, 0);
        if (bytes)
            UnmapViewOfFile(bytes);
        if (mapping)
            CloseHandle(mapping);
        CloseHandle(file);
        return matches;
    }

    bool ReadClient(HWND window, RECT& screen)
    {
        RECT client{};
        if (!GetClientRect(window, &client))
            return false;

        POINT a{client.left, client.top}, b{client.right, client.bottom};
        if (!ClientToScreen(window, &a) || !ClientToScreen(window, &b))
            return false;

        screen = {a.x, a.y, b.x, b.y};
        return b.x > a.x && b.y > a.y;
    }

    bool ReadPhysicalClient(HWND window, RECT& screen, MONITORINFO* monitor = nullptr)
    {
        const auto previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (!previous)
            return false;

        const bool read = ReadClient(window, screen) && (!monitor ||
            GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTONULL), monitor));
        SetThreadDpiAwarenessContext(previous);
        return read;
    }

    bool FullScreen16x9(HWND window, LONG nativeWidth, LONG nativeHeight, RECT& screen)
    {
        MONITORINFO monitor{sizeof(MONITORINFO)};
        if (nativeWidth <= 0 || nativeHeight <= 0 ||
            !ReadPhysicalClient(window, screen, &monitor) || !EqualRect(&screen, &monitor.rcMonitor))
            return false;

        const auto width = screen.right - screen.left;
        const auto height = screen.bottom - screen.top;
        return width > nativeWidth && height > nativeHeight &&
            9LL * width == 16LL * height && 9LL * nativeWidth == 16LL * nativeHeight;
    }

    HANDLE PackPoint(LONG x, LONG y)
    {
        return reinterpret_cast<HANDLE>(MAKELONG(x, y) + 1u);
    }

    bool IsActiveClient(const Session& current, RECT& live)
    {
        if (!ReadClient(current.game, live))
            return false;
        if (EqualRect(&current.client, &live))
            return true;

        RECT physical{};
        return ReadPhysicalClient(current.game, physical) && EqualRect(&current.client, &physical);
    }

    BOOL WINAPI ReadMappedCursor(POINT* point)
    {
        const auto original = originalCursor.load(std::memory_order_acquire);
        if (!original || !original(point))
            return FALSE;
        if (!point)
            return TRUE;

        const DWORD lastError = GetLastError();
        AcquireSRWLockShared(&sessionLock);
        const auto current = session;
        ReleaseSRWLockShared(&sessionLock);

        DWORD ownerPid = 0;
        RECT live{};
        if (current.game && GetForegroundWindow() == current.game &&
            GetPropW(current.game, OwnerProperty) == current.owner &&
            GetWindowThreadProcessId(current.owner, &ownerPid) != 0 && ownerPid == current.ownerPid &&
            IsActiveClient(current, live) &&
            PtInRect(&live, *point))
        {
            const LONG x = point->x - live.left;
            const LONG y = point->y - live.top;
            const LONG mappedX = static_cast<LONG>(static_cast<int64_t>(x) * current.nativeWidth /
                (live.right - live.left));
            const LONG mappedY = static_cast<LONG>(static_cast<int64_t>(y) * current.nativeHeight /
                (live.bottom - live.top));
            point->x = live.left + mappedX;
            point->y = live.top + mappedY;

            if (!current.logged)
            {
                AcquireSRWLockExclusive(&sessionLock);
                if (!session.logged && session.game == current.game && session.owner == current.owner)
                {
                    SetPropW(current.game, RawProperty, PackPoint(x, y));
                    SetPropW(current.game, MappedProperty, PackPoint(mappedX, mappedY));
                    session.logged = true;
                }
                ReleaseSRWLockExclusive(&sessionLock);
            }
        }
        SetLastError(lastError);
        return TRUE;
    }

    CursorFn* FindGameImport()
    {
        const auto image = reinterpret_cast<unsigned char*>(GetModuleHandleW(nullptr));
        if (!image)
            return nullptr;

        const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 || dos->e_lfanew > 4096)
            return nullptr;

        const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(image + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_I386 ||
            nt->FileHeader.TimeDateStamp != GameTimestamp || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC)
            return nullptr;

        const auto iat = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IAT];
        if (GetCursorPosImport < iat.VirtualAddress ||
            GetCursorPosImport + sizeof(CursorFn) > static_cast<uint64_t>(iat.VirtualAddress) + iat.Size)
            return nullptr;

        return reinterpret_cast<CursorFn*>(image + GetCursorPosImport);
    }

    bool InstallInGame(HWND game, HWND owner, LONG nativeWidth, LONG nativeHeight)
    {
        DWORD gamePid = 0, ownerPid = 0;
        if (GetWindowThreadProcessId(game, &gamePid) != GetCurrentThreadId() ||
            gamePid != GetCurrentProcessId() || !GetWindowThreadProcessId(owner, &ownerPid) ||
            ownerPid == gamePid)
            return false;

        RECT screen{};
        if (!FullScreen16x9(game, nativeWidth, nativeHeight, screen))
            return false;

        const auto slot = FindGameImport();
        if (!slot)
            return false;

        HMODULE pinned = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&ReadMappedCursor), &pinned))
            return false;

        AcquireSRWLockExclusive(&sessionLock);
        bool patched = *slot == ReadMappedCursor;
        if (!patched && *slot)
        {
            DWORD protection;
            if (VirtualProtect(slot, sizeof(*slot), PAGE_READWRITE, &protection))
            {
                originalCursor.store(*slot, std::memory_order_release);
                InterlockedExchangePointer(reinterpret_cast<void* volatile*>(slot),
                    reinterpret_cast<void*>(&ReadMappedCursor));
                DWORD old;
                VirtualProtect(slot, sizeof(*slot), protection, &old);
                patched = true;
            }
        }

        if (patched)
        {
            RemovePropW(game, RawProperty);
            RemovePropW(game, MappedProperty);
            session = {game, owner, ownerPid, screen, nativeWidth, nativeHeight, false};
            patched = !!SetPropW(game, OwnerProperty, owner);
        }
        ReleaseSRWLockExclusive(&sessionLock);
        return patched;
    }

    LRESULT CALLBACK GameWindowMessage(int code, WPARAM currentProcess, LPARAM data)
    {
        if (code == HC_ACTION)
        {
            const auto message = reinterpret_cast<const CWPSTRUCT*>(data);
            static const UINT installMessage = RegisterWindowMessageW(MessageName);
            if (installMessage && message->message == installMessage)
                InstallInGame(message->hwnd, reinterpret_cast<HWND>(message->lParam),
                    LOWORD(message->wParam), HIWORD(message->wParam));
        }
        return CallNextHookEx(nullptr, code, currentProcess, data);
    }
}

extern "C" DWORD WINAPI SgInstallMouseMapping(HWND game, HWND owner, UINT nativeWidth, UINT nativeHeight)
{
    DWORD gamePid = 0, ownerPid = 0;
    const DWORD thread = GetWindowThreadProcessId(game, &gamePid);
    if (!thread || !GetWindowThreadProcessId(owner, &ownerPid) || ownerPid != GetCurrentProcessId() ||
        gamePid == ownerPid || nativeWidth == 0 || nativeHeight == 0 ||
        nativeWidth > UINT16_MAX || nativeHeight > UINT16_MAX)
        return InvalidWindow;

    if (!IsSupportedGameFile(gamePid))
        return GameBuildNotSupported;

    RECT screen{};
    if (!FullScreen16x9(game, nativeWidth, nativeHeight, screen))
        return InvalidWindow;

    HMODULE self = nullptr;
    const UINT message = RegisterWindowMessageW(MessageName);
    if (!message || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
        GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(&GameWindowMessage), &self))
        return ThreadHookFailed;

    const auto hook = SetWindowsHookExW(WH_CALLWNDPROC, &GameWindowMessage, self, thread);
    if (!hook)
        return ThreadHookFailed;

    DWORD_PTR reply = 0;
    const bool sent = !!SendMessageTimeoutW(game, message, MAKEWPARAM(nativeWidth, nativeHeight),
        reinterpret_cast<LPARAM>(owner), SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT, 500, &reply);
    UnhookWindowsHookEx(hook);
    return sent && GetPropW(game, OwnerProperty) == owner ? Installed : GameDidNotAcknowledge;
}
