# Copyright (c) 2026 Legion Builds
# Licensed under the MIT License; see LICENSE.txt in the repository root.
#
# Included by the SLua top-level project (CMAKE_PROJECT_INCLUDE). Adds the shim
# target once every SLua target has been defined.
if(DEFINED SLUA_SHIM_INJECTED)
    return()
endif()
set(SLUA_SHIM_INJECTED ON)
set(SLUA_SHIM_DIR "${CMAKE_CURRENT_LIST_DIR}")

function(slua_define_shim)
    # The runtime part of the saved-state stamp, worked out by the wrapper project (stamp.cmake).
    if(NOT SLUA_RUNTIME_STAMP)
        message(FATAL_ERROR "SLUA_RUNTIME_STAMP is not set; configure through ngc-host/CMakeLists.txt.")
    endif()

    add_library(slua_shim SHARED "${SLUA_SHIM_DIR}/shim/slua_shim.cpp" "${SLUA_SHIM_DIR}/shim/slua_shim.h")
    target_include_directories(slua_shim PRIVATE "${SLUA_SHIM_DIR}/shim")
    target_compile_features(slua_shim PRIVATE cxx_std_17)
    target_compile_definitions(slua_shim PRIVATE SLUA_RUNTIME_COMMIT="${SLUA_RUNTIME_STAMP}")
    target_link_libraries(slua_shim PRIVATE Luau.Executor Luau.VM Luau.Compiler Luau.LSLBuiltins Luau.Bytecode Luau.Common libtailslide)
    if(MSVC)
        target_compile_options(slua_shim PRIVATE /EHsc)
    else()
        target_compile_options(slua_shim PRIVATE -fexceptions -fvisibility=hidden)
        target_link_options(slua_shim PRIVATE -Wl,--exclude-libs,ALL)
    endif()
endfunction()

cmake_language(DEFER DIRECTORY "${CMAKE_SOURCE_DIR}" CALL slua_define_shim)
