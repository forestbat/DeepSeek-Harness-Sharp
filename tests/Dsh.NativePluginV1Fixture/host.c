#include "v1.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#include <windows.h>
#else
#include <dlfcn.h>
#include <sys/mman.h>
#include <unistd.h>
#endif

/* Synthetic old host consumer deliberately knows nothing about HostCall or a sized entry. */
typedef struct host_state {
    int32_t echo;
    int32_t config;
    int32_t write;
    int32_t deactivated;
} host_state;

static void DSH_CALL log_message(void* context, int32_t level, const unsigned char* message)
{
    host_state* state = (host_state*)context;
    (void)level;
    if (strstr((const char*)message, "deactivated"))
        state->deactivated++;
}

static int32_t DSH_CALL register_tool(void* context, const unsigned char* name,
    const unsigned char* description, const unsigned char* schema, int32_t handle)
{
    host_state* state = (host_state*)context;
    (void)description;
    (void)schema;
    if (!strcmp((const char*)name, "native_echo")) state->echo = handle;
    if (!strcmp((const char*)name, "native_config")) state->config = handle;
    if (!strcmp((const char*)name, "native_write_file")) state->write = handle;
    return 0;
}

static int32_t DSH_CALL unregister_tool(void* context, int32_t handle)
{
    (void)context;
    (void)handle;
    return 0;
}

static int32_t DSH_CALL get_config(void* context, unsigned char* output, int32_t capacity)
{
    const char* json = "{\"label\":\"synthetic-old-host\",\"enabled\":true}";
    int32_t length = (int32_t)strlen(json);
    (void)context;
    if (output && capacity >= length)
        memcpy(output, json, (size_t)length);
    return length;
}

int main(int argc, char** argv)
{
    dsh_v1_entry entry;
    dsh_v1_host_api* host;
    dsh_v1_plugin_api api = {0};
    host_state state = {0};
    unsigned char output[64 * 1024 + 1];
    unsigned char* allocation;
    size_t page;
    int32_t written;
    if (argc != 2) return 2;
#if defined(_WIN32)
    {
        SYSTEM_INFO info;
        HMODULE library = LoadLibraryA(argv[1]);
        if (!library) return 3;
        entry = (dsh_v1_entry)GetProcAddress(library, "dsh_plugin_entry");
        GetSystemInfo(&info);
        page = info.dwPageSize;
        allocation = (unsigned char*)VirtualAlloc(NULL, page * 2, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (allocation) {
            DWORD previous;
            if (!VirtualProtect(allocation + page, page, PAGE_NOACCESS, &previous)) return 4;
        }
    }
#else
    {
        void* library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
        if (!library) return 3;
        entry = (dsh_v1_entry)dlsym(library, "dsh_plugin_entry");
        page = (size_t)sysconf(_SC_PAGESIZE);
        allocation = (unsigned char*)mmap(NULL, page * 2, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON, -1, 0);
        if (allocation == MAP_FAILED) return 4;
        if (mprotect(allocation + page, page, PROT_NONE)) return 4;
    }
#endif
    if (!allocation || !entry) return 4;
    /* The next byte after the exact original host table is unreadable, not a zero-filled appended callback. */
    host = (dsh_v1_host_api*)(allocation + page - sizeof(dsh_v1_host_api));
    host->abi_version = 1;
    host->context = NULL;
    host->log = log_message;
    host->register_tool = register_tool;
    host->unregister_tool = unregister_tool;
    host->get_config = get_config;
    if (entry(host, &api) || api.abi_version != 1 || !api.activate || !api.invoke_tool || !api.deactivate) return 5;
    if (api.activate(&state)) return 6;
    if (!state.echo || !state.config || !state.write) return 7;
    written = api.invoke_tool(&state, state.echo, (const unsigned char*)"{\"message\":\"synthetic-v1-echo\"}", output, sizeof(output) - 1);
    if (written <= 0 || written >= (int32_t)sizeof(output)) return 8;
    output[written] = 0;
    if (!strstr((const char*)output, "echo:synthetic-v1-echo")) return 9;
    written = api.invoke_tool(&state, state.config, (const unsigned char*)"{}", output, sizeof(output) - 1);
    if (written <= 0 || written >= (int32_t)sizeof(output)) return 10;
    output[written] = 0;
    if (!strstr((const char*)output, "synthetic-old-host")) return 11;
    /* A missing optional callback must fail the requested capability, not fail ordinary v1 activation. */
    written = api.invoke_tool(&state, state.write, (const unsigned char*)"{}", output, sizeof(output) - 1);
    if (written >= 0) return 12;
    api.deactivate(&state);
    if (state.deactivated != 1) return 13;
    /* NativeAOT shared libraries stay mapped until this consumer process exits. */
    return 0;
}
