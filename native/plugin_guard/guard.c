#include "guard.h"

#if defined(_WIN32)

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* 只吞"确实是插件指令引发、且可继续"的故障; 栈溢出等不可安全恢复的交回系统, 不假装遏制。 */
static int dsh_guard_filter(EXCEPTION_POINTERS* info)
{
    switch (info->ExceptionRecord->ExceptionCode)
    {
    case EXCEPTION_ACCESS_VIOLATION:
    case EXCEPTION_ILLEGAL_INSTRUCTION:
    case EXCEPTION_PRIV_INSTRUCTION:
    case EXCEPTION_INT_DIVIDE_BY_ZERO:
    case EXCEPTION_FLT_DIVIDE_BY_ZERO:
    case EXCEPTION_IN_PAGE_ERROR:
        return EXCEPTION_EXECUTE_HANDLER;
    default:
        return EXCEPTION_CONTINUE_SEARCH;
    }
}

int dsh_guard_invoke_tool(dsh_invoke_tool_fn fn, void* context, int handle,
    const unsigned char* input, unsigned char* output, int capacity, int* result)
{
    __try
    {
        *result = fn(context, handle, input, output, capacity);
        return DSH_GUARD_OK;
    }
    __except (dsh_guard_filter(GetExceptionInformation()))
    {
        return DSH_GUARD_FAULTED;
    }
}

int dsh_guard_call_void_int(dsh_void_int_fn fn, void* context, int* result)
{
    __try
    {
        *result = fn(context);
        return DSH_GUARD_OK;
    }
    __except (dsh_guard_filter(GetExceptionInformation()))
    {
        return DSH_GUARD_FAULTED;
    }
}

int dsh_guard_call_void(dsh_void_fn fn, void* context)
{
    __try
    {
        fn(context);
        return DSH_GUARD_OK;
    }
    __except (dsh_guard_filter(GetExceptionInformation()))
    {
        return DSH_GUARD_FAULTED;
    }
}

#else

#include <setjmp.h>
#include <signal.h>
#include <string.h>

/* 每线程一个跳回点: 只有"正处在受护调用里"的线程才被信号处理器接住。 */
static __thread sigjmp_buf* dsh_guard_jump;
static struct sigaction dsh_guard_previous[4];
static int dsh_guard_signals[4] = { SIGSEGV, SIGBUS, SIGILL, SIGFPE };
static int dsh_guard_installed;

static void dsh_guard_handler(int number, siginfo_t* info, void* context)
{
    (void)info;
    (void)context;
    if (dsh_guard_jump != 0)
        siglongjmp(*dsh_guard_jump, 1);
    /* 不属任何受护调用: 恢复默认行为并重放信号, 不吞掉宿主自身的段错误。 */
    signal(number, SIG_DFL);
    raise(number);
}

static void dsh_guard_install(void)
{
    if (dsh_guard_installed)
        return;
    struct sigaction action;
    memset(&action, 0, sizeof(action));
    action.sa_sigaction = dsh_guard_handler;
    action.sa_flags = SA_SIGINFO;
    sigemptyset(&action.sa_mask);
    for (int index = 0; index < 4; index++)
        sigaction(dsh_guard_signals[index], &action, &dsh_guard_previous[index]);
    dsh_guard_installed = 1;
}

int dsh_guard_invoke_tool(dsh_invoke_tool_fn fn, void* context, int handle,
    const unsigned char* input, unsigned char* output, int capacity, int* result)
{
    dsh_guard_install();
    sigjmp_buf jump;
    if (sigsetjmp(jump, 1) != 0)
    {
        dsh_guard_jump = 0;
        return DSH_GUARD_FAULTED;
    }
    dsh_guard_jump = &jump;
    *result = fn(context, handle, input, output, capacity);
    dsh_guard_jump = 0;
    return DSH_GUARD_OK;
}

int dsh_guard_call_void_int(dsh_void_int_fn fn, void* context, int* result)
{
    dsh_guard_install();
    sigjmp_buf jump;
    if (sigsetjmp(jump, 1) != 0)
    {
        dsh_guard_jump = 0;
        return DSH_GUARD_FAULTED;
    }
    dsh_guard_jump = &jump;
    *result = fn(context);
    dsh_guard_jump = 0;
    return DSH_GUARD_OK;
}

int dsh_guard_call_void(dsh_void_fn fn, void* context)
{
    dsh_guard_install();
    sigjmp_buf jump;
    if (sigsetjmp(jump, 1) != 0)
    {
        dsh_guard_jump = 0;
        return DSH_GUARD_FAULTED;
    }
    dsh_guard_jump = &jump;
    fn(context);
    dsh_guard_jump = 0;
    return DSH_GUARD_OK;
}

#endif
