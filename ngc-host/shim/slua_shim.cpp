// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
//
// Flat C interface over the SLua Executor. See slua_shim.h for the contract.
// Nothing in this file lets a C++ exception leave an exported function, and
// the managed side never receives a lua_State.
#include "slua_shim.h"

#include "lua.h"
#include "lualib.h"
#include "luacode.h"
#include "llsl.h"

#include "Luau/BytecodeHeader.h"
#include "Luau/Common.h"
#include "Luau/Compiler.h"
#include "Luau/Executor.h"
#include "Luau/LSLBuiltins.h"
#include "Luau/LSLCompiler.h"
#include "Luau/ParseResult.h"
#include "Luau/Script.h"

#include <algorithm>
#include <atomic>
#include <cfloat>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#else
#include <time.h>
#if defined(__GLIBC__)
#include <malloc.h>
#endif
#endif

// The runtime part of the saved-state stamp: the pinned parent commit, or a hash of the parent
// tree (see ngc-host/stamp.cmake). Set by the build.
#ifndef SLUA_RUNTIME_COMMIT
#define SLUA_RUNTIME_COMMIT "unknown"
#endif

using namespace Luau::Executor;

namespace
{

constexpr int kMaxHostArgs = 32;

// The engine that runs LSL scripts calling os*, iw* and bot* functions, as named
// in the compile refusal. The header token that selects it is a placeholder
// until it is decided; this is the only place either is written.
constexpr const char* kOsFunctionEngineName = "Phlox";
constexpr const char* kOsFunctionEngineHeader = "//engine:PLACEHOLDER";
constexpr int kMaxHostRets = 8;

std::atomic<int32_t> gBusyViolations{0};
std::atomic<slua_log_fn> gLog{nullptr};
std::once_flag gInitOnce;

void logAdapter(LogLevel level, const char* source, const char* message)
{
    if (slua_log_fn fn = gLog.load())
        fn((int32_t)level, source, message);
}

double clockProvider(lua_State*)
{
    return lua_clock();
}

// Keeps an SLua script's handler register current: LLEvents:on and :off call
// this, and the host is the one that stores the result. A name that is not an
// LSL event is refused, which the script sees as an error from :on.
bool handlerRegistration(lua_State* L, const char* event_name, bool registered)
{
    const int index = Luau::lslEventIndex(event_name);
    if (index <= 0)
        return false;
    Script* script = Script::fromLuaState(L);
    if (script == nullptr)
        return false;
    const uint64_t bit = Luau::lslEventBit(index);
    const uint64_t handlers = script->getEventHandlers();
    script->setEventHandlers(registered ? (handlers | bit) : (handlers & ~bit));
    return true;
}

// A plain host function is bound to table.name directly. A pausing function is registered as a
// pair of records: the start call (carrying table.name) and the take call right after it. Neither
// is bound by name; the environment binds the library's wrapper under table.name instead.
enum class HostFnKind
{
    Plain,
    PausingStart,
    PausingTake,
};

struct HostFnRec
{
    std::string table;
    std::string name;
    int32_t id;
    slua_host_fn cb;
    void* user;
    HostFnKind kind = HostFnKind::Plain;
};

} // namespace

// Live-object counts for slua_debug_alloc_stats. A base class counts in its constructor and
// destructor, so every path that makes or frees one is covered.
template<typename Tag>
struct LiveCounted
{
    static std::atomic<int64_t>& count()
    {
        static std::atomic<int64_t> n{0};
        return n;
    }
    LiveCounted() { count()++; }
    LiveCounted(const LiveCounted&) = delete;
    ~LiveCounted() { count()--; }
};

struct slua_vm : public Provisioner<Script>, LiveCounted<slua_vm>
{
    explicit slua_vm(const HostCallbacks& callbacks)
        : Provisioner<Script>(callbacks)
    {
    }

    std::vector<HostFnRec> fns;
    // The environments this VM has made, one per flavour and API version.
    std::map<std::pair<bool, uint32_t>, std::shared_ptr<IEnvironment>> environments;
    // One thread at a time. Set while any export is working on this VM or its scripts.
    std::atomic<int> busy{0};
    int liveScripts = 0;
    int liveImages = 0;
    uint64_t fnHash = 0;
};

struct slua_image : LiveCounted<slua_image>
{
    slua_vm* vm = nullptr;
    std::shared_ptr<IImage> image;
};

struct slua_script : LiveCounted<slua_script>
{
    slua_vm* vm = nullptr;
    std::shared_ptr<IImage> image;
    std::shared_ptr<Script> script;
    int64_t tag = 0;
    // The interval LLTimers last asked the host to wake the script at, 0 for none.
    double timerInterval = 0.0;
    // Host functions of this script that are running now; slua_add_sleep is valid only while this is above zero.
    std::atomic<int> hostCallDepth{0};
    // The handler set the host last read or was told in a step result (see noteHandlers).
    uint64_t reportedHandlers = 0;
    int32_t reportedHandlersKnown = 0;
    // Set by slua_add_sleep, cleared when a host function starts; tells the call's end that it asked for a delay.
    std::atomic<bool> delayAddedInCall{false};
};

namespace
{

// LLTimers tells the host when the next "timer" event is wanted. The host reads
// it back with slua_timer_interval and dispatches the event itself.
void timerEventRequest(lua_State* L, double interval)
{
    Script* script = Script::fromLuaState(L);
    if (script == nullptr)
        return;
    if (slua_script* owner = static_cast<slua_script*>(script->getHostContext()))
        owner->timerInterval = interval;
}

// Refuses a second concurrent caller instead of corrupting the VM.
class VmGuard
{
public:
    explicit VmGuard(slua_vm* vm)
        : mVm(vm)
    {
        int expected = 0;
        mOk = vm->busy.compare_exchange_strong(expected, 1);
        if (!mOk)
            gBusyViolations.fetch_add(1);
    }
    ~VmGuard()
    {
        if (mOk)
            mVm->busy.store(0);
    }
    bool ok() const { return mOk; }

private:
    slua_vm* mVm;
    bool mOk;
};

uint64_t fnvHash(const std::string& s)
{
    uint64_t h = 1469598103934665603ULL;
    for (unsigned char c : s)
    {
        h ^= c;
        h *= 1099511628211ULL;
    }
    return h;
}

// Hash of the bound function list: sorted "table.name" entries, so the order
// functions were registered in does not matter.
uint64_t hashFunctionList(const std::vector<HostFnRec>& fns)
{
    std::vector<std::string> names;
    for (const HostFnRec& f : fns)
    {
        if (f.kind == HostFnKind::PausingTake)
            continue;
        // A pausing function is a different binding from a plain function of the same name:
        // a saved script can be waiting inside its wrapper.
        names.push_back(f.table + "." + f.name + (f.kind == HostFnKind::PausingStart ? "!pausing" : ""));
    }
    std::sort(names.begin(), names.end());
    std::string all;
    for (const std::string& n : names)
    {
        all += n;
        all += '\n';
    }
    return fnvHash(all);
}

// Text and element arrays made while reading one call's arguments. Deques keep
// the addresses of earlier entries stable as later ones are added.
// Made only when a call carries a key or a list, so plain calls pay nothing.
struct ReadScratch
{
    std::unique_ptr<std::deque<std::string>> texts;
    std::unique_ptr<std::deque<std::vector<slua_value>>> lists;
};

// Reads the Lua value at idx into v. Strings point into the Lua string, which
// the caller keeps on the stack or in a table for the whole call; key text and
// list elements live in the scratch. A list may not hold a list.
void readValue(lua_State* L, int idx, slua_value& v, ReadScratch& scratch, bool allowList)
{
    std::memset(&v, 0, sizeof(v));
    switch (lua_type(L, idx))
    {
    case LUA_TNIL:
        v.type = SLUA_T_NIL;
        break;
    case LUA_TBOOLEAN:
        v.type = SLUA_T_BOOL;
        v.i = lua_toboolean(L, idx) ? 1 : 0;
        break;
    case LUA_TNUMBER:
        v.type = SLUA_T_NUMBER;
        v.n = lua_tonumber(L, idx);
        break;
    case LUA_TINTEGER:
    {
        int isint = 0;
        v.type = SLUA_T_INT;
        v.i = lua_tointeger64(L, idx, &isint);
        break;
    }
    case LUA_TSTRING:
    {
        size_t len = 0;
        v.type = SLUA_T_STRING;
        v.s = lua_tolstring(L, idx, &len);
        v.len = (int32_t)len;
        break;
    }
    case LUA_TVECTOR:
    {
        const float* f = lua_tovector(L, idx);
        v.type = SLUA_T_VECTOR;
        v.v[0] = f[0];
        v.v[1] = f[1];
        v.v[2] = f[2];
        break;
    }
    case LUA_TUSERDATA:
    {
        if (lua_userdatatag(L, idx) == UTAG_QUATERNION)
        {
            const float* q = (const float*)lua_touserdatatagged(L, idx, UTAG_QUATERNION);
            v.type = SLUA_T_ROTATION;
            std::memcpy(v.v, q, sizeof(float) * 4);
            break;
        }
        if (lua_userdatatag(L, idx) == UTAG_UUID)
        {
            // The key's text form, as the script itself would print it.
            size_t len = 0;
            luaL_checkstack(L, 2, "key");
            const char* text = luaL_tolstring(L, idx, &len);
            if (!scratch.texts)
                scratch.texts = std::make_unique<std::deque<std::string>>();
            scratch.texts->emplace_back(text, len);
            lua_pop(L, 1);
            v.type = SLUA_T_KEY;
            v.s = scratch.texts->back().c_str();
            v.len = (int32_t)scratch.texts->back().size();
            break;
        }
        luaL_errorL(L, "unsupported type %s", luaL_typename(L, idx));
        break;
    }
    case LUA_TTABLE:
    {
        if (!allowList)
            luaL_errorL(L, "a list cannot hold a list");
        const int n = (int)lua_objlen(L, idx);
        if (n > SLUA_MAX_LIST_ITEMS)
            luaL_errorL(L, "list has too many elements (%d)", n);
        luaL_checkstack(L, 3, "list");
        if (!scratch.lists)
            scratch.lists = std::make_unique<std::deque<std::vector<slua_value>>>();
        scratch.lists->emplace_back((size_t)n);
        std::vector<slua_value>& items = scratch.lists->back();
        const int abs = lua_absindex(L, idx);
        for (int k = 0; k < n; ++k)
        {
            lua_rawgeti(L, abs, k + 1);
            readValue(L, lua_gettop(L), items[(size_t)k], scratch, false);
            lua_pop(L, 1);
        }
        v.type = SLUA_T_LIST;
        v.len = n;
        v.items = items.data();
        break;
    }
    default:
        luaL_errorL(L, "unsupported type %s", luaL_typename(L, idx));
    }
}

// Pushes a boundary value onto the Lua stack as the script's own type for it.
void pushValue(lua_State* L, const slua_value& v, bool allowList)
{
    switch (v.type)
    {
    case SLUA_T_NIL:
        lua_pushnil(L);
        break;
    case SLUA_T_BOOL:
        lua_pushboolean(L, v.i != 0);
        break;
    case SLUA_T_INT:
        luaSL_pushnativeinteger(L, (int)v.i);
        break;
    case SLUA_T_NUMBER:
        lua_pushnumber(L, v.n);
        break;
    case SLUA_T_STRING:
        lua_pushlstring(L, v.s ? v.s : "", (size_t)v.len);
        break;
    case SLUA_T_VECTOR:
        lua_pushvector(L, v.v[0], v.v[1], v.v[2]);
        break;
    case SLUA_T_KEY:
        luaSL_pushuuidlstring(L, v.s ? v.s : "", (size_t)v.len);
        break;
    case SLUA_T_ROTATION:
        luaSL_pushquaternion(L, v.v[0], v.v[1], v.v[2], v.v[3]);
        break;
    case SLUA_T_LIST:
    {
        if (!allowList)
            luaL_errorL(L, "a list cannot hold a list");
        if (v.len < 0 || v.len > SLUA_MAX_LIST_ITEMS || (v.len > 0 && v.items == nullptr))
            luaL_errorL(L, "list is not valid");
        luaL_checkstack(L, 3, "list");
        lua_createtable(L, v.len, 0);
        for (int32_t k = 0; k < v.len; ++k)
        {
            pushValue(L, v.items[k], false);
            lua_rawseti(L, -2, k + 1);
        }
        break;
    }
    default:
        luaL_errorL(L, "unsupported value type %d", v.type);
    }
}

// Marks a script as inside a host function for as long as the call runs.
class HostCallScope
{
public:
    explicit HostCallScope(slua_script* owner)
        : mOwner(owner)
    {
        if (mOwner)
            mOwner->hostCallDepth.fetch_add(1);
    }
    ~HostCallScope()
    {
        if (mOwner)
            mOwner->hostCallDepth.fetch_sub(1);
    }
    HostCallScope(const HostCallScope&) = delete;
    HostCallScope& operator=(const HostCallScope&) = delete;

private:
    slua_script* mOwner;
};

// Copies the script's arguments into flat records, calls the managed host
// function, and pushes whatever it returns. Runs on the script's thread,
// inside lua_resume. luaL_errorL unwinds only native frames up to the
// Executor's own catch of lua_exception.
int runHost(lua_State* L, int index, bool resumed)
{
    Script* script = Script::fromLuaState(L);
    if (script == nullptr)
        luaL_errorL(L, "host function called outside a script");
    slua_vm* vm = dynamic_cast<slua_vm*>(&script->getProvisioner());
    if (vm == nullptr || index < 0 || (size_t)index >= vm->fns.size())
        luaL_errorL(L, "host function is not bound");
    const HostFnRec& fn = vm->fns[(size_t)index];
    slua_script* owner = static_cast<slua_script*>(script->getHostContext());
    const int64_t tag = owner ? owner->tag : 0;

    // The take call of a pausing function has no arguments; whatever is on the stack is not the script's.
    const int nargs = resumed ? 0 : lua_gettop(L);
    if (nargs > kMaxHostArgs)
        luaL_errorL(L, "too many arguments (%d)", nargs);
    slua_value args[kMaxHostArgs];
    ReadScratch scratch;
    for (int i = 0; i < nargs; ++i)
        readValue(L, i + 1, args[i], scratch, true);

    slua_value rets[kMaxHostRets];
    std::memset(rets, 0, sizeof(rets));
    int32_t n;
    if (owner)
        owner->delayAddedInCall.store(false);
    {
        HostCallScope inCall(owner);
        n = fn.cb(fn.user, fn.id, tag, args, nargs, rets, kMaxHostRets);
    }

    if (fn.kind != HostFnKind::Plain && n == SLUA_RET_YIELD)
        luaL_errorL(L, "a pausing function's host call cannot yield; the library yields for it");
    if (fn.kind == HostFnKind::PausingStart && n >= 0)
        return lua_yield(L, 0); // the start call's values are not the call's results; the script waits
    if (n == SLUA_RET_YIELD)
        return lua_yield(L, 0);
    // A call that asked for a delay and gives back nothing ends the script's turn, as llSleep does in
    // Second Life: the script is held for the delay anyway, and the host is told in this step's result.
    // A call that gives values back cannot yield here without losing them, so the delay takes effect
    // at the script's next interrupt instead. Where the script cannot yield (inside a sort comparator,
    // for instance) the same applies.
    if (n == 0 && fn.kind == HostFnKind::Plain && owner && owner->delayAddedInCall.load() && lua_isyieldable(L))
        return lua_yield(L, 0);
    if (n < 0)
    {
        char msg[512];
        if (rets[0].type == SLUA_T_STRING && rets[0].s != nullptr)
            std::snprintf(msg, sizeof(msg), "%.*s", rets[0].len < 500 ? rets[0].len : 500, rets[0].s);
        else
            std::snprintf(msg, sizeof(msg), "host function failed (%d)", (int)n);
        luaL_errorL(L, "%s", msg);
    }
    if (n > kMaxHostRets)
        luaL_errorL(L, "host function returned too many values");

    for (int i = 0; i < n; ++i)
        pushValue(L, rets[i], true);
    return n;
}

int hostTrampoline(lua_State* L)
{
    return runHost(L, (int)lua_tonumber(L, lua_upvalueindex(1)), false);
}

// A pausing function's closure holds the index of its start record; its take record follows it.
// When the host steps the script again, Luau calls this in place of returning to the script, and
// the take call's values become the pausing call's results.
int pausingContinuation(lua_State* L, int status)
{
    if (status != LUA_OK)
        luaL_errorL(L, "pausing function was not resumed normally (%d)", status);
    return runHost(L, (int)lua_tonumber(L, lua_upvalueindex(1)) + 1, true);
}

void populateEnvironment(IEnvironment& environment, lua_State* L)
{
    luaSL_set_constant_globals(L);
    slua_vm* vm = dynamic_cast<slua_vm*>(&environment.getProvisioner());
    if (vm == nullptr)
        return;
    for (size_t i = 0; i < vm->fns.size(); ++i)
    {
        const HostFnRec& f = vm->fns[i];
        if (f.kind == HostFnKind::PausingTake)
            continue;
        lua_getglobal(L, f.table.c_str());
        if (!lua_istable(L, -1))
        {
            lua_pop(L, 1);
            lua_newtable(L);
            lua_pushvalue(L, -1);
            lua_setglobal(L, f.table.c_str());
        }
        // The upvalue is the index into the VM's function table. Ares sees the
        // closure as a permanent because it is reachable from _G by name. A pausing function
        // is a native closure with a continuation: a Lua closure made in the environment
        // cannot be saved while a script waits inside it, because the saved-state writer
        // accepts only code that is part of the script's own image.
        lua_pushnumber(L, (double)i);
        if (f.kind == HostFnKind::PausingStart)
            lua_pushcclosurek(L, hostTrampoline, f.name.c_str(), 1, pausingContinuation);
        else
            lua_pushcclosurek(L, hostTrampoline, f.name.c_str(), 1, nullptr);
        lua_rawsetfield(L, -2, f.name.c_str());
        lua_pop(L, 1);
    }
}

// Saved-state header: "SLSH", u32 version, u16 commit length, commit, u64 function-list hash.
constexpr uint32_t kStateVersion = 1;

void put32(std::string& s, uint32_t v) { s.append((const char*)&v, 4); }
void put16(std::string& s, uint16_t v) { s.append((const char*)&v, 2); }
void put64(std::string& s, uint64_t v) { s.append((const char*)&v, 8); }

// Names of undeclared os*, iw* and bot* functions in a compiler message such as
// "`osGetGridName' is undeclared.", in order of first appearance. A function the
// script declares itself under such a name is not undeclared and does not match.
void collectUndeclaredHostFunctions(const std::string& text, std::vector<std::string>& names)
{
    static const char kTail[] = "' is undeclared";
    size_t pos = 0;
    while ((pos = text.find('`', pos)) != std::string::npos)
    {
        ++pos;
        const size_t end = text.find('\'', pos);
        if (end == std::string::npos)
            break;
        if (text.compare(end, sizeof(kTail) - 1, kTail) != 0)
            continue;
        const std::string name = text.substr(pos, end - pos);
        size_t prefix = 0;
        if (name.compare(0, 2, "os") == 0 || name.compare(0, 2, "iw") == 0)
            prefix = 2;
        else if (name.compare(0, 3, "bot") == 0)
            prefix = 3;
        if (prefix == 0 || name.size() <= prefix || !(name[prefix] >= 'A' && name[prefix] <= 'Z'))
            continue;
        if (std::find(names.begin(), names.end(), name) == names.end())
            names.push_back(name);
    }
}

std::string unsupportedFunctionMessage(const std::vector<std::string>& names)
{
    std::string msg;
    for (const std::string& n : names)
        msg += (msg.empty() ? "" : ", ") + n;
    msg += ": not available in this script engine. LSL scripts that call os*, iw* or bot* functions run on the ";
    msg += kOsFunctionEngineName;
    msg += " engine (script header ";
    msg += kOsFunctionEngineHeader;
    msg += ").";
    return msg;
}

// Blocks handed to the host (compiled assets, saved state, error text) are released with
// slua_free. Each carries a 16-byte header with its size so the library can say how many
// are outstanding.
std::atomic<int64_t> gOutBlocks{0};
std::atomic<int64_t> gOutBytes{0};
constexpr size_t kBlockHeader = 16;

void* outAlloc(size_t size)
{
    char* base = (char*)std::malloc(size + kBlockHeader);
    if (base == nullptr)
        return nullptr;
    std::memcpy(base, &size, sizeof size);
    gOutBlocks++;
    gOutBytes += (int64_t)size;
    return base + kBlockHeader;
}

void outFree(void* p)
{
    if (p == nullptr)
        return;
    char* base = (char*)p - kBlockHeader;
    size_t size;
    std::memcpy(&size, base, sizeof size);
    gOutBlocks--;
    gOutBytes -= (int64_t)size;
    std::free(base);
}

char* dupString(const std::string& s)
{
    char* p = (char*)outAlloc(s.size() + 1);
    if (p == nullptr)
        throw std::bad_alloc();
    std::memcpy(p, s.data(), s.size());
    p[s.size()] = 0;
    return p;
}

// Bytes in use on the process's C heap: the figure does not move when the system allocator
// grows or returns address space. 0 where the system gives none.
uint64_t heapInUseBytes()
{
#if defined(_WIN32)
    HEAP_SUMMARY hs;
    hs.cb = sizeof hs;
    if (HeapSummary(GetProcessHeap(), 0, &hs))
        return (uint64_t)hs.cbAllocated;
    return 0;
#elif defined(__GLIBC__) && (__GLIBC__ > 2 || (__GLIBC__ == 2 && __GLIBC_MINOR__ >= 33))
    struct mallinfo2 mi = mallinfo2();
    return (uint64_t)mi.uordblks + (uint64_t)mi.hblkhd;
#else
    return 0;
#endif
}

} // namespace

#define SLUA_TRY try {
#define SLUA_CATCH                                                \
    }                                                             \
    catch (...)                                                   \
    {                                                             \
        return SLUA_ERR_EXCEPTION;                                \
    }

// The build part of the saved-state stamp, also what slua_version reports. A further part
// (for example the version of the LSL compiler patch) is appended here as " name=value".
static const std::string& stampText()
{
    static const std::string text = std::string("runtime=") + SLUA_RUNTIME_COMMIT + " api=" + std::to_string((int)SLUA_API_VERSION);
    return text;
}

SLUA_API int32_t slua_version(char* buf, int32_t cap)
{
    SLUA_TRY
    if (buf == nullptr || cap <= 0)
        return SLUA_ERR_BAD_ARG;
    std::snprintf(buf, (size_t)cap, "%s", stampText().c_str());
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_api_version(void)
{
    return SLUA_API_VERSION;
}

SLUA_API int32_t slua_struct_size(int32_t which)
{
    switch (which)
    {
    case SLUA_STRUCT_VALUE:
        return (int32_t)sizeof(slua_value);
    case SLUA_STRUCT_STEP_RESULT:
        return (int32_t)sizeof(slua_step_result);
    default:
        return SLUA_ERR_BAD_ARG;
    }
}

SLUA_API int32_t slua_set_log(slua_log_fn fn)
{
    SLUA_TRY
    gLog.store(fn);
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_init(void)
{
    SLUA_TRY
    std::call_once(gInitOnce, []() {
        Luau::setRequiredSLuaFlags();
        luauSL_init_global_builtins(nullptr);
        logCallback() = logAdapter;
    });
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_debug_alloc_stats(uint64_t* out, int32_t n)
{
    if (out == nullptr || n < 0 || n > 6)
        return SLUA_ERR_BAD_ARG;
    const uint64_t v[6] = {
        (uint64_t)LiveCounted<slua_vm>::count().load(),
        (uint64_t)LiveCounted<slua_image>::count().load(),
        (uint64_t)LiveCounted<slua_script>::count().load(),
        (uint64_t)gOutBlocks.load(),
        (uint64_t)gOutBytes.load(),
        heapInUseBytes(),
    };
    for (int32_t i = 0; i < n; i++)
        out[i] = v[i];
    return SLUA_OK;
}

SLUA_API void slua_free(void* p)
{
    outFree(p);
}

SLUA_API int32_t slua_compile(const char* src, size_t len, int32_t is_lsl, uint8_t** out, size_t* out_len, char** err)
{
    SLUA_TRY
    if (src == nullptr || out == nullptr || out_len == nullptr || err == nullptr)
        return SLUA_ERR_BAD_ARG;
    *out = nullptr;
    *out_len = 0;
    *err = nullptr;
    slua_init();

    std::string source(src, len);
    std::string asset;
    try
    {
        if (is_lsl)
        {
            asset = compileLSLAssetOrThrow(source, 0);
        }
        else
        {
            Luau::CompileOptions copts = {};
            copts.optimizationLevel = 1;
            copts.debugLevel = 1;
            copts.typeInfoLevel = 1;
            copts.libraryMemberConstantCb = &luauSL_lookup_constant_cb;
            asset = Luau::compileAssetOrThrow(source, 0, copts);
        }
    }
    catch (Luau::ParseErrors& e)
    {
        std::string msg;
        std::vector<std::string> osNames;
        for (const Luau::ParseError& pe : e.getErrors())
        {
            msg += std::to_string(pe.getLocation().begin.line + 1) + ": " + pe.what() + "\n";
            if (is_lsl)
                collectUndeclaredHostFunctions(pe.what(), osNames);
        }
        if (!osNames.empty())
        {
            *err = dupString(unsupportedFunctionMessage(osNames));
            return SLUA_ERR_UNSUPPORTED_FUNCTION;
        }
        *err = dupString(msg);
        return SLUA_ERR_COMPILE;
    }
    catch (Luau::CompileError& e)
    {
        *err = dupString(std::to_string(e.getLocation().begin.line + 1) + ": " + e.what());
        return SLUA_ERR_COMPILE;
    }
    catch (std::exception& e)
    {
        *err = dupString(e.what());
        return SLUA_ERR_COMPILE;
    }

    uint8_t* mem = (uint8_t*)outAlloc(asset.size() ? asset.size() : 1);
    if (mem == nullptr)
        return SLUA_ERR_EXCEPTION;
    std::memcpy(mem, asset.data(), asset.size());
    *out = mem;
    *out_len = asset.size();
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_vm_new(int32_t watchdog_mode, slua_vm** out)
{
    SLUA_TRY
    if (out == nullptr)
        return SLUA_ERR_BAD_ARG;
    *out = nullptr;
    slua_init();
    HostCallbacks callbacks;
    callbacks.clockProvider = clockProvider;
    callbacks.populateEnvironment = populateEnvironment;
    callbacks.eventHandlerRegistrationCb = handlerRegistration;
    callbacks.setTimerEventCb = timerEventRequest;
    switch (watchdog_mode)
    {
    case SLUA_WATCHDOG_RESIDENT:
        callbacks.interruptInstallPolicy = InterruptInstallPolicy::Resident;
        break;
    case SLUA_WATCHDOG_THREADED:
        callbacks.interruptInstallPolicy = InterruptInstallPolicy::Threaded;
        break;
    default:
        break;
    }
    *out = new slua_vm(callbacks);
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_vm_free(slua_vm* vm)
{
    SLUA_TRY
    if (vm == nullptr)
        return SLUA_ERR_BAD_ARG;
    {
        VmGuard guard(vm);
        if (!guard.ok())
            return SLUA_ERR_BUSY;
        // Scripts hold their image, and through it the environment, so freeing
        // the VM under live scripts would leave them pointing at nothing.
        if (vm->liveScripts != 0 || vm->liveImages != 0)
            return SLUA_ERR_STATE;
        vm->environments.clear();
    }
    delete vm;
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_register_host_fn(slua_vm* vm, const char* table, const char* name, int32_t fn_id, slua_host_fn cb, void* user)
{
    SLUA_TRY
    if (vm == nullptr || table == nullptr || name == nullptr || cb == nullptr)
        return SLUA_ERR_BAD_ARG;
    VmGuard guard(vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;
    // Functions are bound into an environment when it is created.
    if (!vm->environments.empty())
        return SLUA_ERR_STATE;
    vm->fns.push_back(HostFnRec{table, name, fn_id, cb, user});
    vm->fnHash = hashFunctionList(vm->fns);
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_watchdog_stats(slua_vm* vm, uint64_t* fires, uint64_t* late_fires)
{
    SLUA_TRY
    if (vm == nullptr || fires == nullptr || late_fires == nullptr)
        return SLUA_ERR_BAD_ARG;
    WatchdogStats st = vm->getWatchdogStats();
    *fires = st.fires;
    *late_fires = st.lateFires;
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_register_pausing_fn(slua_vm* vm, const char* table, const char* name, int32_t start_id, int32_t take_id, slua_host_fn cb, void* user)
{
    SLUA_TRY
    if (vm == nullptr || table == nullptr || name == nullptr || cb == nullptr)
        return SLUA_ERR_BAD_ARG;
    VmGuard guard(vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;
    if (!vm->environments.empty())
        return SLUA_ERR_STATE;
    vm->fns.push_back(HostFnRec{table, name, start_id, cb, user, HostFnKind::PausingStart});
    vm->fns.push_back(HostFnRec{table, name, take_id, cb, user, HostFnKind::PausingTake});
    vm->fnHash = hashFunctionList(vm->fns);
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_add_sleep(slua_script* s, double seconds)
{
    SLUA_TRY
    if (s == nullptr || !(seconds > 0.0) || seconds > (double)FLT_MAX)
        return SLUA_ERR_BAD_ARG;
    // Called from inside the script's own host function, so the VM guard is already held by this
    // thread and is not taken again.
    if (s->hostCallDepth.load() == 0)
        return SLUA_ERR_STATE;
    const float total = s->script->getSleep() + (float)seconds;
    if (!std::isfinite(total))
        return SLUA_ERR_BAD_ARG;
    s->script->setSleep(total);
    s->delayAddedInCall.store(true);
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_image_new(slua_vm* vm, const uint8_t* asset, size_t len, slua_image** out)
{
    SLUA_TRY
    if (vm == nullptr || asset == nullptr || out == nullptr)
        return SLUA_ERR_BAD_ARG;
    *out = nullptr;
    VmGuard guard(vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;

    Luau::BytecodeHeader header;
    size_t start = 0;
    if (!Luau::readBytecodeHeader((const char*)asset, len, header, start))
        return SLUA_ERR_LOAD;

    auto key = std::make_pair(header.isLSL, header.apiVersion);
    auto it = vm->environments.find(key);
    if (it == vm->environments.end())
        it = vm->environments.emplace(key, vm->createEnvironment(header.isLSL, header.apiVersion)).first;

    ImageConfig image_config;
    image_config.asset = (const char*)asset;
    image_config.assetSize = len;
    image_config.name = "asset";
    std::shared_ptr<IImage> image = vm->buildImage(it->second, image_config);
    if (image == nullptr || !image->isValid())
        return SLUA_ERR_LOAD;

    auto holder = std::make_unique<slua_image>();
    holder->vm = vm;
    holder->image = image;
    vm->liveImages++;
    *out = holder.release();
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_image_free(slua_image* img)
{
    SLUA_TRY
    if (img == nullptr)
        return SLUA_ERR_BAD_ARG;
    {
        VmGuard guard(img->vm);
        if (!guard.ok())
            return SLUA_ERR_BUSY;
        // Scripts made from the image hold their own reference to it.
        img->image.reset();
        img->vm->liveImages--;
    }
    delete img;
    return SLUA_OK;
    SLUA_CATCH
}

namespace
{

// Makes a script on an image. The caller holds the VM guard.
int32_t makeScript(slua_vm* vm, const std::shared_ptr<IImage>& image, int32_t memory_limit, int64_t tag, slua_script** out)
{
    // A host may lower a script's limit, never raise it past the ceiling.
    if (memory_limit > SLUA_MEMORY_LIMIT_MAX)
        return SLUA_ERR_LIMIT;
    auto holder = std::make_unique<slua_script>();
    holder->vm = vm;
    holder->tag = tag;
    holder->image = image;
    ScriptConfig config;
    config.scriptId = "script";
    config.memoryLimit = memory_limit > 0 ? memory_limit : kDefaultMemoryLimit;
    config.hostContext = holder.get();
    holder->script = vm->instantiateScript(image, config);
    if (holder->script == nullptr)
        return SLUA_ERR_LOAD;
    vm->liveScripts++;
    *out = holder.release();
    return SLUA_OK;
}

} // namespace

SLUA_API int32_t slua_script_new_from_image(slua_image* img, int32_t memory_limit, int64_t tag, slua_script** out)
{
    SLUA_TRY
    if (img == nullptr || out == nullptr)
        return SLUA_ERR_BAD_ARG;
    *out = nullptr;
    VmGuard guard(img->vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;
    return makeScript(img->vm, img->image, memory_limit, tag, out);
    SLUA_CATCH
}

SLUA_API int32_t slua_script_new(slua_vm* vm, const uint8_t* asset, size_t len, int32_t memory_limit, int64_t tag, slua_script** out)
{
    SLUA_TRY
    if (vm == nullptr || asset == nullptr || out == nullptr)
        return SLUA_ERR_BAD_ARG;
    *out = nullptr;
    if (memory_limit > SLUA_MEMORY_LIMIT_MAX)
        return SLUA_ERR_LIMIT;
    // A private image: the script keeps the only reference once the handle is gone.
    slua_image* img = nullptr;
    int32_t rc = slua_image_new(vm, asset, len, &img);
    if (rc != SLUA_OK)
        return rc;
    rc = slua_script_new_from_image(img, memory_limit, tag, out);
    slua_image_free(img);
    return rc;
    SLUA_CATCH
}

SLUA_API int32_t slua_script_free(slua_script* s)
{
    SLUA_TRY
    if (s == nullptr)
        return SLUA_ERR_BAD_ARG;
    {
        VmGuard guard(s->vm);
        if (!guard.ok())
            return SLUA_ERR_BUSY;
        s->script.reset();
        s->image.reset();
        s->vm->liveScripts--;
    }
    delete s;
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_script_start(slua_script* s)
{
    SLUA_TRY
    if (s == nullptr)
        return SLUA_ERR_BAD_ARG;
    VmGuard guard(s->vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;
    return s->script->loadDefaultState() ? SLUA_OK : SLUA_ERR_LOAD;
    SLUA_CATCH
}

namespace
{

// CPU time the calling thread has used so far, in seconds; 0 where the system gives none. A step
// reports the difference over its run, so a scheduler can tell a script that burns CPU from one
// the operating system kept off the processor. Windows counts thread time in the scheduler's
// ticks (about 15 ms); Linux reads it to the nanosecond.
double threadCpuSeconds()
{
#if defined(_WIN32)
    FILETIME created, exited, kernel, user;
    if (!GetThreadTimes(GetCurrentThread(), &created, &exited, &kernel, &user))
        return 0.0;
    ULARGE_INTEGER k, u;
    k.LowPart = kernel.dwLowDateTime;
    k.HighPart = kernel.dwHighDateTime;
    u.LowPart = user.dwLowDateTime;
    u.HighPart = user.dwHighDateTime;
    return (double)(k.QuadPart + u.QuadPart) * 1e-7; // 100 ns units
#else
    timespec ts;
    if (clock_gettime(CLOCK_THREAD_CPUTIME_ID, &ts) != 0)
        return 0.0;
    return (double)ts.tv_sec + (double)ts.tv_nsec * 1e-9;
#endif
}

// The handler set as the host sees it: one bit per event the script handles, and whether the set is
// complete (always for LSL; for SLua once the main chunk has run).
void currentHandlers(const Script& script, uint64_t& mask, int32_t& known)
{
    mask = script.getEventHandlers();
    known = script.eventHandlersKnown() ? 1 : 0;
}

// Tells the host the handler set: returns whether it differs from what the host last read
// (slua_handler_mask) or was told by a step result, and makes the current set the one it knows.
int32_t noteHandlers(slua_script& owner)
{
    uint64_t mask;
    int32_t known;
    currentHandlers(*owner.script, mask, known);
    const bool changed = mask != owner.reportedHandlers || known != owner.reportedHandlersKnown;
    owner.reportedHandlers = mask;
    owner.reportedHandlersKnown = known;
    return changed ? 1 : 0;
}

// The delay the script is owed, which the step reports to the host and clears. A step takes it
// twice: at the start, for a delay that was saved with the script, and at the end, for a delay the
// script's own run asked for (a host function, or the runtime's punishment for a long run).
float takeSleep(Script& script)
{
    const float sleep = script.getSleep();
    if (sleep > 0.0f)
        script.setSleep(0.0f);
    return sleep > 0.0f ? sleep : 0.0f;
}

} // namespace

SLUA_API int32_t slua_step(slua_script* s, double quanta_seconds, slua_step_result* result)
{
    SLUA_TRY
    if (s == nullptr || result == nullptr || quanta_seconds <= 0.0)
        return SLUA_ERR_BAD_ARG;
    VmGuard guard(s->vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;
    Script& script = *s->script;
    std::memset(result, 0, sizeof(*result));

    if (script.getFaultKind() != FaultKind::None)
    {
        result->status = SLUA_STEP_FAULT;
        result->fault_kind = (int32_t)script.getFaultKind();
        return SLUA_OK;
    }
    if (!script.hasInstance())
        return SLUA_ERR_STATE;

    // The engine asks for sleeps (ll.Sleep, punishment for long runs). A real
    // scheduler would delay the next step; the spike serves them at once and
    // reports how long they were.
    result->sleep_served = takeSleep(script);

    const double cpuStart = threadCpuSeconds();
    RunResult rr;
    for (;;)
    {
        int event = Luau::LSLEvent::None;
        if (!script.isHandlerActive())
        {
            // The engine only pends state_exit when the departing state handles it.
            if (script.isStateChangePending() && !(script.getCurrentEvents() & Luau::LSLEventBit::StateExit))
                script.nextState();

            if (script.getCurrentEvents() & Luau::LSLEventBit::StateExit)
                event = Luau::LSLEvent::StateExit;
            else if (script.getCurrentEvents() & Luau::LSLEventBit::StateEntry)
                event = Luau::LSLEvent::StateEntry;
            else
            {
                result->status = SLUA_STEP_IDLE;
                result->handlers_changed = noteHandlers(*s);
                return SLUA_OK;
            }

            // A state without the handler just drops the event.
            const uint64_t bit = Luau::lslEventBit(event);
            if (!(script.getEventHandlers() & bit))
            {
                script.setCurrentEvents(script.getCurrentEvents() & ~bit);
                continue;
            }
        }
        RunWindow window(script, quanta_seconds);
        rr = event != Luau::LSLEvent::None ? script.callEventHandler(event, nullptr) : script.resumeEventHandler();
        break;
    }
    result->status = (int32_t)rr.status;
    result->new_state = rr.newState;
    result->fault_kind = (int32_t)script.getFaultKind();
    result->sleep_served += takeSleep(script);
    result->cpu_seconds = std::max(0.0, threadCpuSeconds() - cpuStart);
    result->handlers_changed = noteHandlers(*s);
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_event_index(const char* name)
{
    SLUA_TRY
    if (name == nullptr)
        return SLUA_ERR_BAD_ARG;
    const int index = Luau::lslEventIndex(name);
    return index > 0 ? index : SLUA_ERR_BAD_ARG;
    SLUA_CATCH
}

namespace
{

struct EventArgs
{
    const slua_value* args;
    int32_t count;
};

// Runs inside callEventHandler, after the handler and the event name are on the
// handler thread's stack. A lua_exception here (memory, bad value) is caught by
// callEventHandler and becomes a fault.
void pushEventArgs(lua_State* L, void* ctx)
{
    const EventArgs* ea = static_cast<const EventArgs*>(ctx);
    luaL_checkstack(L, ea->count + 4, "event arguments");
    for (int32_t i = 0; i < ea->count; ++i)
        pushValue(L, ea->args[i], true);
}

} // namespace

SLUA_API int32_t slua_dispatch_event(slua_script* s, int32_t event, const slua_value* args, int32_t nargs, double quanta_seconds, slua_step_result* result)
{
    SLUA_TRY
    if (s == nullptr || result == nullptr || quanta_seconds <= 0.0 || nargs < 0 || nargs > kMaxHostArgs || (nargs > 0 && args == nullptr))
        return SLUA_ERR_BAD_ARG;
    if (event <= 0 || Luau::lslEventName(event) == nullptr)
        return SLUA_ERR_BAD_ARG;
    VmGuard guard(s->vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;
    Script& script = *s->script;
    std::memset(result, 0, sizeof(*result));

    if (script.getFaultKind() != FaultKind::None)
    {
        result->status = SLUA_STEP_FAULT;
        result->fault_kind = (int32_t)script.getFaultKind();
        return SLUA_OK;
    }
    if (!script.hasInstance())
        return SLUA_ERR_STATE;
    // One handler at a time, and a pending state change runs its state_exit and
    // state_entry first (keep calling slua_step until it reports IDLE).
    const uint64_t stateBits = Luau::LSLEventBit::StateExit | Luau::LSLEventBit::StateEntry;
    if (script.isHandlerActive() || script.isStateChangePending() || (script.getCurrentEvents() & stateBits))
        return SLUA_ERR_STATE;

    // Same sleep handling as slua_step.
    result->sleep_served = takeSleep(script);

    // A script that is known not to handle the event is not run at all.
    if (script.eventHandlersKnown() && !(script.getEventHandlers() & Luau::lslEventBit(event)))
    {
        result->status = SLUA_STEP_NOT_RUN;
        result->handlers_changed = noteHandlers(*s);
        return SLUA_OK;
    }

    EventArgs ea{args, nargs};
    const double cpuStart = threadCpuSeconds();
    RunResult rr;
    {
        RunWindow window(script, quanta_seconds);
        rr = script.callEventHandler(event, pushEventArgs, &ea);
    }
    result->status = (int32_t)rr.status;
    result->new_state = rr.newState;
    result->fault_kind = (int32_t)script.getFaultKind();
    result->sleep_served += takeSleep(script);
    result->cpu_seconds = std::max(0.0, threadCpuSeconds() - cpuStart);
    result->handlers_changed = noteHandlers(*s);
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_timer_interval(slua_script* s, double* seconds)
{
    SLUA_TRY
    if (s == nullptr || seconds == nullptr)
        return SLUA_ERR_BAD_ARG;
    VmGuard guard(s->vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;
    *seconds = s->timerInterval;
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_handler_mask(slua_script* s, uint64_t* mask, int32_t* known)
{
    SLUA_TRY
    if (s == nullptr || mask == nullptr)
        return SLUA_ERR_BAD_ARG;
    VmGuard guard(s->vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;
    int32_t isKnown;
    currentHandlers(*s->script, *mask, isKnown);
    if (known != nullptr)
        *known = isKnown;
    // The host has the set now, so the next step reports a change only from here.
    s->reportedHandlers = *mask;
    s->reportedHandlersKnown = isKnown;
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_kill(slua_script* s)
{
    SLUA_TRY
    if (s == nullptr)
        return SLUA_ERR_BAD_ARG;
    VmGuard guard(s->vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;
    if (s->script->isHandlerActive())
        s->script->abortHandler();
    else if (s->script->getFaultKind() == FaultKind::None)
        s->script->setFault(FaultKind::Unknown, "script killed by host");
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_serialize(slua_script* s, uint8_t** out, size_t* out_len)
{
    SLUA_TRY
    if (s == nullptr || out == nullptr || out_len == nullptr)
        return SLUA_ERR_BAD_ARG;
    *out = nullptr;
    *out_len = 0;
    VmGuard guard(s->vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;
    std::string payload;
    if (!s->script->serializeState(payload))
        return SLUA_ERR_STATE;

    std::string blob = "SLSH";
    put32(blob, kStateVersion);
    const std::string& stamp = stampText();
    put16(blob, (uint16_t)stamp.size());
    blob += stamp;
    put64(blob, s->vm->fnHash);
    blob += payload;

    uint8_t* mem = (uint8_t*)outAlloc(blob.size());
    if (mem == nullptr)
        return SLUA_ERR_EXCEPTION;
    std::memcpy(mem, blob.data(), blob.size());
    *out = mem;
    *out_len = blob.size();
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_restore(slua_script* s, const uint8_t* data, size_t len)
{
    SLUA_TRY
    if (s == nullptr || data == nullptr)
        return SLUA_ERR_BAD_ARG;
    VmGuard guard(s->vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;
    if (s->script->hasInstance())
        return SLUA_ERR_STATE;

    // Header: a blob that is not ours at all is an error; a blob from another
    // runtime or another bound function list starts the script fresh.
    size_t pos = 0;
    auto need = [&](size_t n) { return len - pos >= n; };
    if (!need(4) || std::memcmp(data, "SLSH", 4) != 0)
        return SLUA_ERR_RESTORE;
    pos = 4;
    if (!need(4))
        return SLUA_ERR_RESTORE;
    uint32_t version;
    std::memcpy(&version, data + pos, 4);
    pos += 4;
    if (version != kStateVersion)
    {
        logWarn("shim", "saved state has format version %u, this build writes %u: starting the script fresh", version, kStateVersion);
        return s->script->loadDefaultState() ? SLUA_RESTORED_FRESH : SLUA_ERR_LOAD;
    }
    if (!need(2))
        return SLUA_ERR_RESTORE;
    uint16_t commit_len;
    std::memcpy(&commit_len, data + pos, 2);
    pos += 2;
    if (!need((size_t)commit_len + 8))
        return SLUA_ERR_RESTORE;
    const std::string commit((const char*)data + pos, commit_len);
    pos += commit_len;
    uint64_t hash;
    std::memcpy(&hash, data + pos, 8);
    pos += 8;

    if (commit != stampText())
    {
        logWarn("shim", "saved state is stamped '%s', this build is '%s': starting the script fresh", commit.c_str(), stampText().c_str());
        return s->script->loadDefaultState() ? SLUA_RESTORED_FRESH : SLUA_ERR_LOAD;
    }
    if (hash != s->vm->fnHash)
    {
        logWarn("shim", "saved state was made with a different set of bound host functions: starting the script fresh");
        return s->script->loadDefaultState() ? SLUA_RESTORED_FRESH : SLUA_ERR_LOAD;
    }
    if (!s->script->restoreState((const char*)data + pos, len - pos))
    {
        logWarn("shim", "saved state could not be restored: starting the script fresh");
        if (s->script->hasInstance())
            s->script->reset();
        return s->script->loadDefaultState() ? SLUA_RESTORED_FRESH : SLUA_ERR_LOAD;
    }
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_fault(slua_script* s, int32_t* kind, char* buf, int32_t cap)
{
    SLUA_TRY
    if (s == nullptr || kind == nullptr || buf == nullptr || cap <= 0)
        return SLUA_ERR_BAD_ARG;
    VmGuard guard(s->vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;
    *kind = (int32_t)s->script->getFaultKind();
    const std::string& text = s->script->getExtendedFaultString().empty() ? s->script->getFaultString() : s->script->getExtendedFaultString();
    std::snprintf(buf, (size_t)cap, "%s", text.c_str());
    return SLUA_OK;
    SLUA_CATCH
}

SLUA_API int32_t slua_used_memory(slua_script* s)
{
    SLUA_TRY
    if (s == nullptr)
        return SLUA_ERR_BAD_ARG;
    VmGuard guard(s->vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;
    return s->script->getUsedMemory();
    SLUA_CATCH
}

SLUA_API int32_t slua_get_memory_limit(slua_script* s)
{
    SLUA_TRY
    if (s == nullptr)
        return SLUA_ERR_BAD_ARG;
    return s->script->getMemoryLimit();
    SLUA_CATCH
}

SLUA_API int32_t slua_set_memory_limit(slua_script* s, int32_t limit)
{
    SLUA_TRY
    if (s == nullptr)
        return SLUA_ERR_BAD_ARG;
    VmGuard guard(s->vm);
    if (!guard.ok())
        return SLUA_ERR_BUSY;
    // The Executor refuses a limit above its default or below the memory in use.
    // setMemoryLimitUnsafe is deliberately not exposed.
    if (limit > SLUA_MEMORY_LIMIT_MAX)
        return SLUA_ERR_LIMIT;
    return s->script->setMemoryLimit(limit) ? SLUA_OK : SLUA_ERR_LIMIT;
    SLUA_CATCH
}

SLUA_API int32_t slua_busy_violations(void)
{
    return gBusyViolations.load();
}
