#ifndef DSH_PLUGIN_H
#define DSH_PLUGIN_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define DSH_CALL __cdecl
#define DSH_EXPORT __declspec(dllexport)
#else
#define DSH_CALL
#define DSH_EXPORT __attribute__((visibility("default")))
#endif

#define DSH_ABI_VERSION 1
#define DSH_PROTOCOL_FRAME_BYTES (1024 * 1024)

typedef struct dsh_host_api {
    int32_t abi_version;
    void* context;
    void (DSH_CALL *log)(void*, int32_t, const unsigned char*);
    int32_t (DSH_CALL *register_tool)(void*, const unsigned char*, const unsigned char*, const unsigned char*, int32_t);
    int32_t (DSH_CALL *unregister_tool)(void*, int32_t);
    int32_t (DSH_CALL *get_config)(void*, unsigned char*, int32_t);
    /* Read this tail only when the sized entry advertises at least sizeof(dsh_host_api). */
    int32_t (DSH_CALL *host_call)(void*, const unsigned char*, const unsigned char*, unsigned char*, int32_t);
} dsh_host_api;

#define DSH_V1_HOST_PREFIX_BYTES offsetof(dsh_host_api, host_call)

typedef struct dsh_plugin_api {
    int32_t abi_version;
    const unsigned char* package;
    int32_t (DSH_CALL *activate)(void*);
    void (DSH_CALL *deactivate)(void*);
    int32_t (DSH_CALL *invoke_tool)(void*, int32_t, const unsigned char*, unsigned char*, int32_t);
} dsh_plugin_api;

#ifdef __cplusplus
extern "C" {
#endif

/* The original entry assumes only DSH_V1_HOST_PREFIX_BYTES, never the appended tail. */
DSH_EXPORT int32_t DSH_CALL dsh_plugin_entry(dsh_host_api* host, dsh_plugin_api* plugin);
/* This optional entry negotiates host tail availability without changing the original export. */
DSH_EXPORT int32_t DSH_CALL dsh_plugin_entry_with_host_size(dsh_host_api* host, int32_t host_api_size, dsh_plugin_api* plugin);
DSH_EXPORT const unsigned char* DSH_CALL dsh_plugin_package(void);
DSH_EXPORT int32_t DSH_CALL dsh_plugin_abi_version(void);

#ifdef __cplusplus
}
#endif

/* Input strings are NUL-terminated UTF-8 and remain owned by the caller during the call. */
/* Output is caller-owned storage, valid only during the call, and is never retained by host_call. */
/* host_call requires an actual full frame before execution and returns bytes written or -1. */
/* There is no size probe or retry; a failure after execution must not be retried automatically. */
/* get_config and legacy invoke_tool keep their original v1 output-length convention. */

#endif
