#include "v1.h"
#include <stdio.h>
#include <string.h>

static const unsigned char package[] = "test.frozen-native-v1";
static dsh_v1_host_api* host;
static int32_t calls;

static int32_t DSH_CALL activate(void* context)
{
    calls = 0;
    host->log(context, 1, (const unsigned char*)"frozen v1 activated");
    if (host->register_tool(context, (const unsigned char*)"legacy_v1_count",
        (const unsigned char*)"Count actual legacy tool executions.",
        (const unsigned char*)"{\"type\":\"object\"}", 1) != 0)
        return -1;
    return host->register_tool(context, (const unsigned char*)"legacy_v1_config",
        (const unsigned char*)"Return configuration via the original callback.",
        (const unsigned char*)"{\"type\":\"object\"}", 2);
}

static void DSH_CALL deactivate(void* context)
{
    if (host->unregister_tool) {
        host->unregister_tool(context, 1);
        host->unregister_tool(context, 2);
    }
    host->log(context, 1, (const unsigned char*)"frozen v1 deactivated");
}

static int32_t DSH_CALL invoke_tool(void* context, int32_t handle,
    const unsigned char* input, unsigned char* output, int32_t capacity)
{
    unsigned char result[64 * 1024];
    int32_t length;
    (void)input;
    if (handle == 1) {
        /* The current host must not repeat this side effect to fetch its output. */
        length = snprintf((char*)result, sizeof(result), "{\"calls\":%d}", ++calls);
    } else if (handle == 2) {
        length = host->get_config ? host->get_config(context, result, sizeof(result)) : 0;
        if (length == 0) {
            memcpy(result, "{}", 2);
            length = 2;
        }
    } else {
        return -1;
    }
    if (length < 0 || length > (int32_t)sizeof(result))
        return -1;
    if (output && capacity >= length)
        memcpy(output, result, (size_t)length);
    return length;
}

DSH_EXPORT const unsigned char* DSH_CALL dsh_plugin_package(void) { return package; }
DSH_EXPORT int32_t DSH_CALL dsh_plugin_abi_version(void) { return 1; }

DSH_EXPORT int32_t DSH_CALL dsh_plugin_entry(dsh_v1_host_api* incoming, dsh_v1_plugin_api* api)
{
    if (!incoming || !api || incoming->abi_version != 1 || !incoming->log || !incoming->register_tool)
        return -1;
    host = incoming;
    api->abi_version = 1;
    api->package = package;
    api->activate = activate;
    api->deactivate = deactivate;
    api->invoke_tool = invoke_tool;
    return 0;
}
