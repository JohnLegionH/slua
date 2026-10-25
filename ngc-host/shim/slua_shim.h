// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
//
// Flat C interface that lets a managed host drive the SLua Executor.
//
// Rules the implementation keeps:
//  - No C++ exception crosses an exported function: every export catches
//    everything and returns a negative SLUA_ERR_* code.
//  - The host never sees a lua_State. Host functions are native trampolines
//    that copy their arguments into slua_value records before calling out.
//  - A slua_vm is used by one thread at a time. A second concurrent caller is
//    refused with SLUA_ERR_BUSY and counted (slua_busy_violations).
#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define SLUA_API extern "C" __declspec(dllexport)
#define SLUA_CALL __cdecl
#else
#define SLUA_API extern "C" __attribute__((visibility("default")))
#define SLUA_CALL
#endif

// Result codes. Negative is an error, zero is success, positive is a
// non-error outcome the caller must look at.
#define SLUA_OK 0
#define SLUA_RESTORED_FRESH 1 // slua_restore refused the saved state and started the script fresh
#define SLUA_ERR_NOT_IMPLEMENTED (-1)
#define SLUA_ERR_EXCEPTION (-2)
#define SLUA_ERR_BAD_ARG (-3)
#define SLUA_ERR_BUSY (-4)
#define SLUA_ERR_COMPILE (-5)
#define SLUA_ERR_LOAD (-6)
#define SLUA_ERR_RESTORE (-7)
#define SLUA_ERR_STATE (-8) // call not valid in the script's current state

// Return code a host function uses to ask for a yield instead of returning values.
#define SLUA_RET_YIELD (-1000)

// Watchdog modes for slua_vm_new.
#define SLUA_WATCHDOG_DEFAULT 0
#define SLUA_WATCHDOG_RESIDENT 1
#define SLUA_WATCHDOG_THREADED 2

// Status of one slua_step. Values match Executor HandlerRunStatus, plus IDLE.
#define SLUA_STEP_OK 0
#define SLUA_STEP_PREEMPTED 1
#define SLUA_STEP_STATE_CHANGE 2
#define SLUA_STEP_FAULT 3
#define SLUA_STEP_NOT_RUN 4
#define SLUA_STEP_REFUSED 5
#define SLUA_STEP_IDLE 100 // nothing left to run

// Fault kinds, matching Executor FaultKind.
#define SLUA_FAULT_NONE 0
#define SLUA_FAULT_RUNTIME 1
#define SLUA_FAULT_OUT_OF_MEMORY 2
#define SLUA_FAULT_TIMEOUT 3
#define SLUA_FAULT_UNEXPECTED_YIELD 4
#define SLUA_FAULT_UNKNOWN 255

#define SLUA_T_NIL 0
#define SLUA_T_BOOL 1
#define SLUA_T_INT 2
#define SLUA_T_NUMBER 3
#define SLUA_T_STRING 4
#define SLUA_T_VECTOR 5

// One scripting value crossing the boundary. 48 bytes, no padding surprises.
struct slua_value
{
    int32_t type; // SLUA_T_*
    int32_t len;  // string byte length
    int64_t i;    // BOOL (0/1) and INT
    double n;     // NUMBER
    const char* s; // STRING. Valid only for the duration of the call it came in.
    float v[4];   // VECTOR uses v[0..2]
};

struct slua_vm;
struct slua_script;

// Called when a script calls a registered host function. Writes up to
// max_rets values to rets and returns how many. Returns SLUA_RET_YIELD to
// yield the script with no values. Any other negative value is an error and
// rets[0] holds the message as a STRING. Must not throw.
typedef int32_t(SLUA_CALL* slua_host_fn)(void* user, int32_t fn_id, int64_t script_tag, const slua_value* args, int32_t nargs, slua_value* rets, int32_t max_rets);

// level: 0 debug, 1 info, 2 warn, 3 error.
typedef void(SLUA_CALL* slua_log_fn)(int32_t level, const char* source, const char* message);

struct slua_step_result
{
    int32_t status;     // SLUA_STEP_*
    int32_t new_state;  // LSL state number when status is STATE_CHANGE
    float sleep_served; // seconds of sleep the engine asked for in this step, consumed by the shim
    int32_t fault_kind; // SLUA_FAULT_*
};

SLUA_API int32_t slua_version(char* buf, int32_t cap);
SLUA_API int32_t slua_set_log(slua_log_fn fn);
SLUA_API int32_t slua_init(void);

// Compiles source to an asset (header plus bytecode). *out and *err are
// allocated by the shim and released with slua_free. On failure *err holds the
// message and the code is SLUA_ERR_COMPILE.
SLUA_API int32_t slua_compile(const char* src, size_t len, int32_t is_lsl, uint8_t** out, size_t* out_len, char** err);
SLUA_API void slua_free(void* p);

SLUA_API int32_t slua_vm_new(int32_t watchdog_mode, slua_vm** out);
SLUA_API int32_t slua_vm_free(slua_vm* vm);
// Host functions must be registered before the first script is created.
SLUA_API int32_t slua_register_host_fn(slua_vm* vm, const char* table, const char* name, int32_t fn_id, slua_host_fn cb, void* user);
SLUA_API int32_t slua_watchdog_stats(slua_vm* vm, uint64_t* fires, uint64_t* late_fires);

SLUA_API int32_t slua_script_new(slua_vm* vm, const uint8_t* asset, size_t len, int32_t memory_limit, int64_t tag, slua_script** out);
SLUA_API int32_t slua_script_free(slua_script* s);
// Loads the image's default state and stages the main function.
SLUA_API int32_t slua_script_start(slua_script* s);
// One scheduling step: serve sleep, then resume the in-flight handler, or run
// the pending state_exit / state_entry. Returns SLUA_STEP_IDLE when none.
SLUA_API int32_t slua_step(slua_script* s, double quanta_seconds, slua_step_result* result);
SLUA_API int32_t slua_kill(slua_script* s);

SLUA_API int32_t slua_serialize(slua_script* s, uint8_t** out, size_t* out_len);
// Restores a saved state into a script that has not been started. Returns
// SLUA_RESTORED_FRESH after starting the script fresh when the state was
// saved by a different runtime commit or a different bound function list.
SLUA_API int32_t slua_restore(slua_script* s, const uint8_t* data, size_t len);

SLUA_API int32_t slua_fault(slua_script* s, int32_t* kind, char* buf, int32_t cap);
SLUA_API int32_t slua_used_memory(slua_script* s);
SLUA_API int32_t slua_get_memory_limit(slua_script* s);
SLUA_API int32_t slua_set_memory_limit(slua_script* s, int32_t limit);
SLUA_API int32_t slua_busy_violations(void);
