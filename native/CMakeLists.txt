cmake_minimum_required(VERSION 3.24)

if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    project(WingSyncNative LANGUAGES CXX)
    enable_testing()
endif()

if(NOT WIN32 OR NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "WapiHost requires a Windows x64 MSVC toolchain.")
endif()

set(WAPI_SDK_DIR
    "${CMAKE_CURRENT_LIST_DIR}/../third_party/wapi"
    CACHE PATH
    "Directory containing the official wapi.h, wext.h, and libwapi.a")

foreach(required_file IN ITEMS wapi.h wext.h libwapi.a)
    if(NOT EXISTS "${WAPI_SDK_DIR}/${required_file}")
        message(FATAL_ERROR
            "Missing ${required_file} in WAPI_SDK_DIR='${WAPI_SDK_DIR}'. "
            "Use the exact official Windows WAPI SDK files.")
    endif()
endforeach()

add_executable(WapiHost
    WapiHost/main.cpp
    WapiHost/mingw_compat.cpp)

target_compile_features(WapiHost PRIVATE cxx_std_20)
target_compile_definitions(WapiHost PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _CRT_SECURE_NO_WARNINGS)
target_compile_options(WapiHost PRIVATE /W4 /EHsc /utf-8)
target_include_directories(WapiHost SYSTEM PRIVATE "${WAPI_SDK_DIR}")

# The official Windows SDK ships a GNU/MinGW-format x64 COFF archive. link.exe
# can consume its objects; mingw_compat.cpp supplies the handful of MinGW CRT
# entry points that those objects reference.
target_link_libraries(WapiHost PRIVATE
    "${WAPI_SDK_DIR}/libwapi.a"
    ws2_32)

set_target_properties(WapiHost PROPERTIES
    OUTPUT_NAME "WingSync.WapiHost"
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_test(NAME WapiHostSelfTest COMMAND WapiHost --self-test)

install(TARGETS WapiHost
    RUNTIME DESTINATION native)

if(EXISTS "${WAPI_SDK_DIR}/NOTICE.md")
    install(FILES "${WAPI_SDK_DIR}/NOTICE.md"
        DESTINATION licenses
        RENAME "wapi-NOTICE.md")
endif()
