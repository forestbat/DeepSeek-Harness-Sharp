/*
 * dsh_fault_probe: 仅用于测试的"会访问违例"的原生函数, 供宿主验证 dsh_plugin_guard 的信号级遏制。
 * 真实原生函数(无托管元数据), CLR 的 VEH 不会认领其 AV, 因此会走到 guard 的 SEH/sigsetjmp。
 */

#if defined(_WIN32)
#define PROBE_EXPORT __declspec(dllexport)
#define PROBE_CALL __cdecl
#else
#define PROBE_EXPORT
#define PROBE_CALL
#endif

PROBE_EXPORT int PROBE_CALL dsh_fault_probe_ok(void* context, int handle,
    const unsigned char* input, unsigned char* output, int capacity)
{
    (void)context;
    (void)handle;
    (void)input;
    (void)output;
    (void)capacity;
    return 42;
}

PROBE_EXPORT int PROBE_CALL dsh_fault_probe_boom(void* context, int handle,
    const unsigned char* input, unsigned char* output, int capacity)
{
    (void)context;
    (void)handle;
    (void)input;
    (void)output;
    (void)capacity;
    volatile int* pointer = 0;
    return *pointer;
}
