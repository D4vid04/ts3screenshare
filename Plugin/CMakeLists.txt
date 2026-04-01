cmake_minimum_required(VERSION 3.20)
project(TS3ScreenSharePlugin VERSION 1.0.0)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ── Download TS3 Plugin SDK automatically ─────────────────────────────────────
include(FetchContent)
FetchContent_Declare(
    ts3sdk
    GIT_REPOSITORY https://github.com/teamspeak/ts3client-pluginsdk.git
    GIT_TAG        master
    GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(ts3sdk)

# ── Plugin DLL ────────────────────────────────────────────────────────────────
add_library(TS3ScreenSharePlugin SHARED
    src/plugin.cpp
    src/pipe_client.cpp
)

target_include_directories(TS3ScreenSharePlugin PRIVATE
    src
    ${ts3sdk_SOURCE_DIR}/include
)

set_target_properties(TS3ScreenSharePlugin PROPERTIES
    PREFIX ""
    OUTPUT_NAME "TS3ScreenSharePlugin"
)

target_compile_definitions(TS3ScreenSharePlugin PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

# ── Post-build: deploy to TS3 plugins folder ──────────────────────────────────
if(WIN32)
    set(TS3_PLUGINS_DIR "$ENV{APPDATA}/TS3Client/plugins")
    add_custom_command(TARGET TS3ScreenSharePlugin POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${TS3_PLUGINS_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy "$<TARGET_FILE:TS3ScreenSharePlugin>" "${TS3_PLUGINS_DIR}/"
        COMMENT "Deploying plugin to ${TS3_PLUGINS_DIR}"
    )
endif()
