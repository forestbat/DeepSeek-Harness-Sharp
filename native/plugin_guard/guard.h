#ifndef DSH_PLUGIN_GUARD_H
#define DSH_PLUGIN_GUARD_H

/*
 * dsh_plugin_guard: 在进程内、按调用遏制原生插件引发的段错误/访问违例。
 * Windows 用 SEH(__try/__except) 捕获同线程 AV/ILL; POSIX 用 sigaction + sigsetjmp/siglongjmp。
 * 只包住"一次插件调用", 一旦故障即返回 FAULTED, 调用方据此把插件标记 Faulted 并卸载。
 */

#if defined(_WIN32)
#define DSH_GUARD_CALL __cdecl
#define DSH_GUARD_EXPORT __declspec(dllexport)
#else
#define DSH_GUARD_CALL
#define DSH_GUARD_EXPORT
#endif

#define DSH_GUARD_OK 0
#define DSH_GUARD_FAULTED 1

#ifdef __cplusplus
extern "C" {
#endif

typedef int(DSH_GUARD_CALL* dsh_invoke_tool_fn)(void* context, int handle,
    const unsigned char* input, unsigned char* output, int capacity);

/* 调用插件工具函数; 捕获故障时返回 DSH_GUARD_FAULTED 且不写 *result, 否则返回 DSH_GUARD_OK 并写 *result。 */
DSH_GUARD_EXPORT int dsh_guard_invoke_tool(dsh_invoke_tool_fn fn, void* context, int handle,
    const unsigned char* input, unsigned char* output, int capacity, int* result);

typedef int(DSH_GUARD_CALL* dsh_void_int_fn)(void* context);

/* 调用插件激活 void(int) 函数; 故障被捕获时返回 DSH_GUARD_FAULTED。 */
DSH_GUARD_EXPORT int dsh_guard_call_void_int(dsh_void_int_fn fn, void* context, int* result);

typedef void(DSH_GUARD_CALL* dsh_void_fn)(void* context);

/* 调用插件反激活等 void(void*) 函数; 故障被捕获时返回 DSH_GUARD_FAULTED。 */
DSH_GUARD_EXPORT int dsh_guard_call_void(dsh_void_fn fn, void* context);

#ifdef __cplusplus
}
#endif

#endif
