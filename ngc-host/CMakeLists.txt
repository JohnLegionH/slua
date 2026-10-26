# Copyright (c) 2026 Legion Builds
# Licensed under the MIT License; see LICENSE.txt in the repository root.
#
# Wrapper project. It configures and builds the SLua tree (the parent
# directory, unmodified) as its own top-level project, and injects one extra
# target into it, slua_shim (slua_shim.dll / libslua_shim.so), through
# CMAKE_PROJECT_INCLUDE. The SLua top-level file reads builtins.txt relative to
# its own source directory, so it has to be the top-level project; this keeps
# that file untouched.
#
#   cmake -S <repo>/ngc-host -B <build dir> -G Ninja -DCMAKE_BUILD_TYPE=Release
#   cmake --build <build dir>
# The library ends up in <build dir>/slua/.
cmake_minimum_required(VERSION 3.19)
project(slua_host NONE)

include(ExternalProject)
get_filename_component(LL_ROOT "${CMAKE_CURRENT_LIST_DIR}/.." ABSOLUTE)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

# The runtime part of the saved-state stamp: the pin in LL-RUNTIME-PIN, checked against the
# parent tree here (see stamp.cmake). Configure fails if the parent tree is not the pinned one,
# unless -DSLUA_ALLOW_UNPINNED_TREE=ON, which stamps a hash of the parent tree instead.
option(SLUA_ALLOW_UNPINNED_TREE "Build against a parent tree that differs from LL-RUNTIME-PIN" OFF)
include("${CMAKE_CURRENT_LIST_DIR}/stamp.cmake")
slua_runtime_stamp("${LL_ROOT}" "${CMAKE_CURRENT_LIST_DIR}/LL-RUNTIME-PIN" "${SLUA_ALLOW_UNPINNED_TREE}" SLUA_RUNTIME_STAMP)
message(STATUS "Saved-state runtime stamp: ${SLUA_RUNTIME_STAMP}")

ExternalProject_Add(slua
    SOURCE_DIR "${LL_ROOT}"
    BINARY_DIR "${CMAKE_BINARY_DIR}/slua"
    CMAKE_GENERATOR "${CMAKE_GENERATOR}"
    CMAKE_ARGS
        -DCMAKE_BUILD_TYPE=${CMAKE_BUILD_TYPE}
        -DCMAKE_PROJECT_INCLUDE=${CMAKE_CURRENT_LIST_DIR}/inject_shim.cmake
        -DSLUA_RUNTIME_STAMP=${SLUA_RUNTIME_STAMP}
        # C++ exceptions stay on (the Executor needs them) and LUAU_EXTERN_C
        # stays off, which selects the exception build of the VM, not longjmp.
        -DLUAU_EXTERN_C=OFF
        -DLUAU_BUILD_CLI=OFF
        -DLUAU_BUILD_TESTS=OFF
        -DLUAU_BUILD_SHARED=OFF
        # Use the checked-in scanner and parser; never regenerate in the SLua tree.
        -DTAILSLIDE_SKIP_GEN=ON
        -DCMAKE_POSITION_INDEPENDENT_CODE=ON
    # At most two compile jobs.
    BUILD_COMMAND ${CMAKE_COMMAND} --build <BINARY_DIR> --target slua_shim --parallel 2
    INSTALL_COMMAND ""
    BUILD_ALWAYS ON
    USES_TERMINAL_BUILD ON)
