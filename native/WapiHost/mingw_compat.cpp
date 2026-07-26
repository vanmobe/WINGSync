#include <Windows.h>
#include <WinSock2.h>

#include <cstdarg>
#include <cstdio>

// libwapi.a is built with MinGW. These narrow wrappers preserve the ABI that
// the archive expects while keeping all vendor diagnostics away from stdout,
// which is reserved exclusively for WapiHost's line protocol.

using PopenFunction = FILE*(__cdecl*)(const char*, const char*);
using PcloseFunction = int(__cdecl*)(FILE*);
using AcrtIobFunction = FILE*(__cdecl*)(unsigned int);

// MinGW recorded these two CRT functions as DLL imports. When WapiHost uses
// MSVC's static CRT, expose equivalent import-address slots that point at the
// statically linked implementations.
extern "C" PopenFunction __imp__popen = &_popen;
extern "C" PcloseFunction __imp__pclose = &_pclose;
extern "C" AcrtIobFunction __imp___acrt_iob_func = &__acrt_iob_func;

extern "C" int __cdecl __mingw_printf(const char* format, ...)
{
    va_list arguments;
    va_start(arguments, format);
    const int result = std::vfprintf(stderr, format, arguments);
    va_end(arguments);
    return result;
}

extern "C" int __cdecl __mingw_fprintf(FILE* stream, const char* format, ...)
{
    va_list arguments;
    va_start(arguments, format);
    FILE* const destination = stream == stdout ? stderr : stream;
    const int result = std::vfprintf(destination, format, arguments);
    va_end(arguments);
    return result;
}

extern "C" int __cdecl __mingw_sprintf(char* buffer, const char* format, ...)
{
    va_list arguments;
    va_start(arguments, format);
    const int result = std::vsprintf(buffer, format, arguments);
    va_end(arguments);
    return result;
}

extern "C" int __cdecl __mingw_sscanf(const char* buffer, const char* format, ...)
{
    va_list arguments;
    va_start(arguments, format);
    const int result = std::vsscanf(buffer, format, arguments);
    va_end(arguments);
    return result;
}

extern "C" int __cdecl gettimeofday(timeval* value, void*)
{
    if (value == nullptr)
    {
        return -1;
    }

    FILETIME fileTime{};
    GetSystemTimeAsFileTime(&fileTime);

    ULARGE_INTEGER ticks{};
    ticks.LowPart = fileTime.dwLowDateTime;
    ticks.HighPart = fileTime.dwHighDateTime;

    constexpr unsigned long long windowsToUnixEpochTicks = 116444736000000000ULL;
    const unsigned long long unixTicks =
        ticks.QuadPart >= windowsToUnixEpochTicks
            ? ticks.QuadPart - windowsToUnixEpochTicks
            : 0;

    value->tv_sec = static_cast<long>(unixTicks / 10'000'000ULL);
    value->tv_usec = static_cast<long>((unixTicks % 10'000'000ULL) / 10ULL);
    return 0;
}
