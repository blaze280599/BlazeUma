cmake_minimum_required(VERSION 3.25)
project(BlazeUmaAgent LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT: the agent must not depend on runtime DLLs being present in the game's process.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_library(BlazeUma.Agent SHARED src/agent.cpp)
target_compile_definitions(BlazeUma.Agent PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_compile_options(BlazeUma.Agent PRIVATE /W4 /permissive-)
