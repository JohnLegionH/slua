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
//
// Version history:
//   2  value types key, rotation and list; shared images; memory ceiling; events with arguments; timers.
//   3  slua_add_sleep (a host function adds a delay to its script) and slua_register_pausing_fn
//      (a pausing function the library binds, which a waiting script can be saved inside).
//
// Versioning rule: any change to the signature of an export, to the meaning of
// a result code, or to the layout of slua_value or slua_step_result bumps
// SLUA_API_VERSION. A managed host compares slua_api_version() with the
// version it was written for before it calls anything else. Adding a new
// export that changes nothing existing does not need a bump.
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
#define SLUA_ERR_LIMIT (-9) // a memory limit above the ceiling, or below what the script already uses
#define SLUA_ERR_UNSUPPORTED_FUNCTION (-10) // LSL source calls an os*, iw* or bot* function; *err names it

// The API version this header describes; see the versioning rule above.
#define SLUA_API_VERSION 3

// Highest memory limit a script can have. A host may lower a limit, never raise it past this.
#define SLUA_MEMORY_LIMIT_MAX (128 * 1024)

// Most elements a list value can carry across the boundary.
#define SLUA_MAX_LIST_ITEMS 4096

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
#define SLUA_T_KEY 6      // a UUID or, in LSL, any string; carried as text in s/len
#define SLUA_T_ROTATION 7 // v[0..3] = x, y, z, s
#define SLUA_T_LIST 8     // len elements at items; elements are never lists

// One scripting value crossing the boundary. 56 bytes on 64-bit targets.
// Everything a value points at (s, items) is valid only for the duration of the
// call it came in on; a receiver that wants to keep it copies it.
struct slua_value
{
    int32_t type; // SLUA_T_*
    int32_t len;  // STRING and KEY: byte length. LIST: element count
    int64_t i;    // BOOL (0/1) and INT
    double n;     // NUMBER
    const char* s; // STRING and KEY text
    const struct slua_value* items; // LIST elements
    float v[4];   // VECTOR uses v[0..2], ROTATION v[0..3]
};

struct slua_vm;
struct slua_script;
struct slua_image;

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

// Which struct to measure in slua_struct_size.
#define SLUA_STRUCT_VALUE 0
#define SLUA_STRUCT_STEP_RESULT 1

SLUA_API int32_t slua_version(char* buf, int32_t cap);
// SLUA_API_VERSION of the loaded library.
SLUA_API int32_t slua_api_version(void);
// sizeof of a struct as the library sees it, so a host can check its own layout.
SLUA_API int32_t slua_struct_size(int32_t which);
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
// Registers table.name as a pausing function. A script calling it runs a native function the
// library binds under table.name: it calls cb with start_id and the call's arguments (return
// values are ignored) and yields the script. After the host steps the script again, the library
// calls cb with take_id and no arguments, and the values that call returns are the values of
// table.name(...). A negative return from either call raises a script error as for any host
// function; SLUA_RET_YIELD from either is an error, because the library does the yielding. It is
// bound in the environment for SLua and for LSL alike, so LSL compiled by the runtime's compiler
// calls it as it calls any ll function, and a script waiting inside it can be saved and restored.
// (A Lua closure made in the environment cannot take this job: the saved-state writer accepts
// only code that belongs to the script's own image.) Same registration rule as
// slua_register_host_fn: before the first image is made. start_id and take_id are passed back to
// cb to tell the two calls apart; they need not differ from other registered ids.
SLUA_API int32_t slua_register_pausing_fn(slua_vm* vm, const char* table, const char* name, int32_t start_id, int32_t take_id, slua_host_fn cb, void* user);

// Loads an asset into an image that any number of scripts in this VM can share.
// The image is released with slua_image_free; scripts made from it keep it alive.
SLUA_API int32_t slua_image_new(slua_vm* vm, const uint8_t* asset, size_t len, slua_image** out);
SLUA_API int32_t slua_image_free(slua_image* img);
// memory_limit: 0 means the default (SLUA_MEMORY_LIMIT_MAX); above the maximum is SLUA_ERR_LIMIT.
SLUA_API int32_t slua_script_new_from_image(slua_image* img, int32_t memory_limit, int64_t tag, slua_script** out);
// Same as slua_image_new followed by slua_script_new_from_image, for one script.
SLUA_API int32_t slua_script_new(slua_vm* vm, const uint8_t* asset, size_t len, int32_t memory_limit, int64_t tag, slua_script** out);
SLUA_API int32_t slua_script_free(slua_script* s);
// Loads the image's default state and stages the main function.
SLUA_API int32_t slua_script_start(slua_script* s);
// One scheduling step: serve sleep, then resume the in-flight handler, or run
// the pending state_exit / state_entry. Returns SLUA_STEP_IDLE when none.
SLUA_API int32_t slua_step(slua_script* s, double quanta_seconds, slua_step_result* result);
// Index of an event by its LSL name ("touch_start"), or SLUA_ERR_BAD_ARG.
SLUA_API int32_t slua_event_index(const char* name);
// Starts an event handler with arguments and runs its first step. Valid only
// when the script has no handler in flight and no state change waiting (keep
// calling slua_step until it returns SLUA_STEP_IDLE); otherwise SLUA_ERR_STATE.
// result->status is NOT_RUN when the script has no handler for the event.
// Later steps of the same handler go through slua_step.
SLUA_API int32_t slua_dispatch_event(slua_script* s, int32_t event, const slua_value* args, int32_t nargs, double quanta_seconds, slua_step_result* result);
// The interval an SLua script last asked for with LLTimers, in seconds; 0 when it has no timer.
// The host schedules a "timer" event dispatch for that time. Not meaningful for LSL.
SLUA_API int32_t slua_timer_interval(slua_script* s, double* seconds);
// Adds seconds to the sleep the script is asked to serve; the next step reports it in
// sleep_served. Valid only inside a host function that this script called (otherwise
// SLUA_ERR_STATE); seconds must be finite and above zero (otherwise SLUA_ERR_BAD_ARG). Calls add up.
SLUA_API int32_t slua_add_sleep(slua_script* s, double seconds);
SLUA_API int32_t slua_kill(slua_script* s);

SLUA_API int32_t slua_serialize(slua_script* s, uint8_t** out, size_t* out_len);
// Restores a saved state into a script that has not been started. Returns
// SLUA_RESTORED_FRESH after starting the script fresh when the state was
// saved by a different runtime commit or a different bound function list.
SLUA_API int32_t slua_restore(slua_script* s, const uint8_t* data, size_t len);

SLUA_API int32_t slua_fault(slua_script* s, int32_t* kind, char* buf, int32_t cap);
SLUA_API int32_t slua_used_memory(slua_script* s);
SLUA_API int32_t slua_get_memory_limit(slua_script* s);
// SLUA_ERR_LIMIT when limit is above SLUA_MEMORY_LIMIT_MAX or below the memory the script uses now.
SLUA_API int32_t slua_set_memory_limit(slua_script* s, int32_t limit);
SLUA_API int32_t slua_busy_violations(void);
