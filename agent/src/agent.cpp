// BlazeUma agent, step 1: loaded into the game by the injector, it serves a local named pipe and
// answers "ping" and "quit". It does not touch IL2CPP, Unity or any game state.
//
// Pipe: \\.\pipe\BlazeUma.Agent.<game pid>, message mode, one client at a time, local only.
// Protocol: one command per message; one reply per command.

#include <windows.h>

#include <cstdio>
#include <string>

namespace {

constexpr const char* kVersion = "0.1";

HMODULE g_self = nullptr;
volatile bool g_quit = false;

std::string Handle(std::string command)
{
    while (!command.empty() && (command.back() == '\n' || command.back() == '\r' || command.back() == ' '))
        command.pop_back();

    if (command == "ping")
        return "pong " + std::to_string(GetCurrentProcessId()) + " v" + kVersion;
    if (command == "quit")
    {
        g_quit = true;
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
