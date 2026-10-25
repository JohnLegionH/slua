// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
//
// Stub implementation: every export reports "not implemented". It exists so
// the deciding tests can be committed and run red before the real shim.
#include "slua_shim.h"

#define STUB(...)                                   \
    try                                             \
    {                                               \
        (void)sizeof(int[1]);                       \
        return SLUA_ERR_NOT_IMPLEMENTED;            \
    }                                               \
    catch (...)                                     \
    {                                               \
        return SLUA_ERR_EXCEPTION;                  \
    }

SLUA_API int32_t slua_version(char*, int32_t) { STUB() }
SLUA_API int32_t slua_set_log(slua_log_fn) { STUB() }
SLUA_API int32_t slua_init(void) { STUB() }
SLUA_API int32_t slua_compile(const char*, size_t, int32_t, uint8_t**, size_t*, char**) { STUB() }
SLUA_API void slua_free(void*) {}
SLUA_API int32_t slua_vm_new(int32_t, slua_vm**) { STUB() }
SLUA_API int32_t slua_vm_free(slua_vm*) { STUB() }
SLUA_API int32_t slua_register_host_fn(slua_vm*, const char*, const char*, int32_t, slua_host_fn, void*) { STUB() }
SLUA_API int32_t slua_watchdog_stats(slua_vm*, uint64_t*, uint64_t*) { STUB() }
SLUA_API int32_t slua_script_new(slua_vm*, const uint8_t*, size_t, int32_t, int64_t, slua_script**) { STUB() }
SLUA_API int32_t slua_script_free(slua_script*) { STUB() }
SLUA_API int32_t slua_script_start(slua_script*) { STUB() }
SLUA_API int32_t slua_step(slua_script*, double, slua_step_result*) { STUB() }
SLUA_API int32_t slua_kill(slua_script*) { STUB() }
SLUA_API int32_t slua_serialize(slua_script*, uint8_t**, size_t*) { STUB() }
SLUA_API int32_t slua_restore(slua_script*, const uint8_t*, size_t) { STUB() }
SLUA_API int32_t slua_fault(slua_script*, int32_t*, char*, int32_t) { STUB() }
SLUA_API int32_t slua_used_memory(slua_script*) { STUB() }
SLUA_API int32_t slua_get_memory_limit(slua_script*) { STUB() }
SLUA_API int32_t slua_set_memory_limit(slua_script*, int32_t) { STUB() }
SLUA_API int32_t slua_busy_violations(void) { STUB() }
