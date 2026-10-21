// BlazeUma agent: loaded into the game by the injector, it serves a local named pipe and runs
// requested game calls on Unity's main thread via a WH_GETMESSAGE hook (no code patching).
//
// Pipe: \\.\pipe\BlazeUma.Agent.<game pid>, message mode, one client at a time, local only.
// Protocol: one command per message, one reply per command.
//   ping                                    -> pong <pid> v<ver>
//   tap <obj_hex> <fn_hex> <mi_hex>         -> ok | error <...>    (calls fn(obj, mi) on main thread)
//   quit                                    -> bye                 (removes hook, unloads DLL)

#include <windows.h>

#include <atomic>
#include <cstdio>
#include <mutex>
#include <string>

namespace {

constexpr const char* kVersion = "0.2";

HMODULE g_self = nullptr;
volatile bool g_quit = false;

// A single pending main-thread call. The pipe serves one client at a time, so one slot is enough.
struct TapJob
{
    void* obj = nullptr;
    void (*fn)(void*, void*) = nullptr;
    void* mi = nullptr;
    HANDLE done = nullptr;    // auto-reset event
    volatile LONG status = 0; // 0 pending, 1 ok, 2 SEH, 3 timed out (hook must drop silently)
};

std::mutex g_slot_mutex;
TapJob* g_slot = nullptr;
HHOOK g_hook = nullptr;

// Run the function under SEH. Separate function because MSVC forbids __try in a function with
// unwinding C++ objects (lock_guard etc.).
static LONG RunUnderSeh(void (*fn)(void*, void*), void* obj, void* mi)
{
    __try { fn(obj, mi); return 1; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 2; }
}

// Runs on the game's main thread each time a window message is retrieved.
LRESULT CALLBACK HookProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code == HC_ACTION)
    {
        TapJob* job = nullptr;
        {
            std::lock_guard<std::mutex> lk(g_slot_mutex);
            job = g_slot;
            g_slot = nullptr;
        }
        if (job != nullptr)
        {
            if (InterlockedCompareExchange(&job->status, 1, 0) == 0)
                InterlockedExchange(&job->status, RunUnderSeh(job->fn, job->obj, job->mi));
            SetEvent(job->done);
        }
    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

static BOOL CALLBACK FindMainThreadProc(HWND hwnd, LPARAM lp)
{
    DWORD pid = 0;
    DWORD tid = GetWindowThreadProcessId(hwnd, &pid);
    if (pid == GetCurrentProcessId() && IsWindowVisible(hwnd))
    {
        *reinterpret_cast<DWORD*>(lp) = tid;
        return FALSE;
    }
    return TRUE;
}

DWORD FindGameMainThread()
{
    DWORD tid = 0;
    EnumWindows(FindMainThreadProc, reinterpret_cast<LPARAM>(&tid));
    return tid;
}

bool EnsureHook(DWORD* out_tid)
{
    DWORD tid = FindGameMainThread();
    if (tid == 0) return false;
    *out_tid = tid;
    if (g_hook != nullptr) return true;
    g_hook = SetWindowsHookExW(WH_GETMESSAGE, HookProc, g_self, tid);
    return g_hook != nullptr;
}

void RemoveHook()
{
    if (g_hook != nullptr) { UnhookWindowsHookEx(g_hook); g_hook = nullptr; }
}

std::string HandleTap(const std::string& args)
{
    unsigned long long obj = 0, fn = 0, mi = 0;
    if (sscanf_s(args.c_str(), "%llx %llx %llx", &obj, &fn, &mi) != 3 || fn == 0 || mi == 0 || obj == 0)
        return "error bad args; expected '<obj_hex> <fn_hex> <mi_hex>'";

    DWORD tid = 0;
    if (!EnsureHook(&tid))
        return "error could not install main-thread hook";

    TapJob job;
    job.obj = reinterpret_cast<void*>(obj);
    job.fn = reinterpret_cast<void(*)(void*, void*)>(fn);
    job.mi = reinterpret_cast<void*>(mi);
    job.done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (job.done == nullptr) return "error CreateEvent failed";

    {
        std::lock_guard<std::mutex> lk(g_slot_mutex);
        g_slot = &job;
    }
    // Nudge the main thread so WH_GETMESSAGE fires even if it is idle.
    PostThreadMessageW(tid, WM_NULL, 0, 0);

    DWORD wait = WaitForSingleObject(job.done, 3000);
    CloseHandle(job.done);

    if (wait == WAIT_TIMEOUT)
    {
        // Mark the slot abandoned so the hook, if it fires late, does not call fn.
        std::lock_guard<std::mutex> lk(g_slot_mutex);
        InterlockedExchange(&job.status, 3);
        g_slot = nullptr;
        return "error timed out waiting for main thread";
    }
    LONG s = InterlockedCompareExchange(&job.status, 0, 0);
    return s == 1 ? "ok" : (s == 2 ? "error exception in game call" : "error unknown");
}

std::string Handle(std::string command)
{
    while (!command.empty() && (command.back() == '\n' || command.back() == '\r' || command.back() == ' '))
        command.pop_back();

    if (command == "ping")
        return "pong " + std::to_string(GetCurrentProcessId()) + " v" + kVersion;
    if (command.rfind("tap ", 0) == 0)
        return HandleTap(command.substr(4));
    if (command == "quit")
    {
        g_quit = true;
        RemoveHook();
        return "bye";
    }
    return "error unknown command: " + command;
}

void Serve(HANDLE pipe)
{
    char buffer[4096];
    DWORD read = 0;
    while (!g_quit && ReadFile(pipe, buffer, sizeof(buffer), &read, nullptr) && read > 0)
    {
        std::string reply = Handle(std::string(buffer, read));
        DWORD written = 0;
        if (!WriteFile(pipe, reply.data(), static_cast<DWORD>(reply.size()), &written, nullptr))
            break;
        FlushFileBuffers(pipe);
    }
}

DWORD WINAPI ServerThread(LPVOID)
{
    wchar_t name[64];
    swprintf_s(name, L"\\\\.\\pipe\\BlazeUma.Agent.%lu", GetCurrentProcessId());

    while (!g_quit)
    {
        HANDLE pipe = CreateNamedPipeW(
            name, PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            1, 4096, 4096, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE)
        {
            Sleep(1000);
            continue;
        }
        if (ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED)
            Serve(pipe);
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }

    // Unload ourselves; the game keeps running.
    FreeLibraryAndExitThread(g_self, 0);
}

} // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_self = module;
        DisableThreadLibraryCalls(module);
        // Never do real work inside DllMain (loader lock); start a thread and return.
        if (HANDLE thread = CreateThread(nullptr, 0, ServerThread, nullptr, 0, nullptr))
            CloseHandle(thread);
    }
    return TRUE;
}
