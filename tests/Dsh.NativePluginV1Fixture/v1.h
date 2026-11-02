#ifndef DSH_FROZEN_V1_H
#define DSH_FROZEN_V1_H

#include <stdint.h>

#if defined(_WIN32)
#define DSH_EXPORT __declspec(dllexport)
#define DSH_CALL __cdecl
#else
#define DSH_EXPORT __attribute__((visibility("default")))
#define DSH_CALL
#endif

/* Frozen synthetic compatibility fixture for the v1 contract at 9123cc0. */
/* Do not include the current SDK header or append new fields here. */
typedef struct dsh_v1_host_api {
    int32_t abi_version;
    void* context;
    void (DSH_CALL *log)(void*, int32_t, const unsigned char*);
    int32_t (DSH_CALL *register_tool)(void*, const unsigned char*, const unsigned char*, const unsigned char*, int32_t);
    int32_t (DSH_CALL *unregister_tool)(void*, int32_t);
    int32_t (DSH_CALL *get_config)(void*, unsigned char*, int32_t);
} dsh_v1_host_api;

typedef struct dsh_v1_plugin_api {
    int32_t abi_version;
    const unsigned char* package;
    int32_t (DSH_CALL *activate)(void*);
    void (DSH_CALL *deactivate)(void*);
    int32_t (DSH_CALL *invoke_tool)(void*, int32_t, const unsigned char*, unsigned char*, int32_t);
} dsh_v1_plugin_api;

typedef int32_t (DSH_CALL *dsh_v1_entry)(dsh_v1_host_api*, dsh_v1_plugin_api*);

#endif
