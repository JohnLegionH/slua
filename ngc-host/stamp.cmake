# Copyright (c) 2026 Legion Builds
# Licensed under the MIT License; see LICENSE.txt in the repository root.
#
# The runtime part of the saved-state stamp.
#
# ngc-host/LL-RUNTIME-PIN holds the commit of the parent SLua tree that the host library is
# built against. At configure time the parent tree's files (everything except ngc-host/ and
# the ngc-host workflow) must be the pin's files; if they are not, configure fails. The stamp
# is then the pin itself, so commits that only change ngc-host/ never change it.
#
# With SLUA_ALLOW_UNPINNED_TREE=ON the check is skipped for a tree that differs from the pin
# and the stamp is "tree-" plus a hash of the parent tree's file list (path and blob id of
# every file), so two builds of the same parent files still agree.
#
# Use from a project:   include(stamp.cmake); slua_runtime_stamp(<repo> <pin file> <allow> <out var>)
# Use from a script:    cmake -DSLUA_SOURCE_DIR=<repo> -DSLUA_PIN_FILE=<pin file>
#                             -DSLUA_STAMP_OUT=<file> [-DSLUA_ALLOW_UNPINNED_TREE=ON] -P stamp.cmake

# Files of <rev> that belong to the parent tree, as "mode type blob<TAB>path" lines.
function(_slua_parent_tree SOURCE_DIR REV OUT_VAR OK_VAR)
    execute_process(
        COMMAND git -C "${SOURCE_DIR}" ls-tree -r "${REV}"
        OUTPUT_VARIABLE listing
        RESULT_VARIABLE rc
        ERROR_QUIET)
    if(NOT rc EQUAL 0)
        set(${OK_VAR} FALSE PARENT_SCOPE)
        return()
    endif()
    string(REGEX REPLACE "[^\n]*\t(ngc-host/[^\n]*|\.github/workflows/ngc-host\.yml)\n" "" listing "${listing}")
    string(SHA256 digest "${listing}")
    set(${OUT_VAR} "${digest}" PARENT_SCOPE)
    set(${OK_VAR} TRUE PARENT_SCOPE)
endfunction()

function(slua_runtime_stamp SOURCE_DIR PIN_FILE ALLOW OUT_VAR)
    if(NOT EXISTS "${PIN_FILE}")
        message(FATAL_ERROR "The pin file ${PIN_FILE} (LL-RUNTIME-PIN) is missing.")
    endif()
    file(STRINGS "${PIN_FILE}" pin LIMIT_COUNT 1)
    string(STRIP "${pin}" pin)
    if(NOT pin MATCHES "^[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]+$")
        message(FATAL_ERROR "LL-RUNTIME-PIN must hold a commit id in lower-case hex, found '${pin}'.")
    endif()

    _slua_parent_tree("${SOURCE_DIR}" HEAD head_digest head_ok)
    if(NOT head_ok)
        message(FATAL_ERROR "Cannot list the files of the parent tree: ${SOURCE_DIR} is not a git checkout with a commit.")
    endif()

    _slua_parent_tree("${SOURCE_DIR}" "${pin}" pin_digest pin_ok)
    if(pin_ok AND head_digest STREQUAL pin_digest)
        set(${OUT_VAR} "${pin}" PARENT_SCOPE)
        return()
    endif()

    if(ALLOW)
        string(SUBSTRING "${head_digest}" 0 10 short)
        message(STATUS "Parent tree does not match LL-RUNTIME-PIN (${pin}); SLUA_ALLOW_UNPINNED_TREE is on, stamping tree-${short}")
        set(${OUT_VAR} "tree-${short}" PARENT_SCOPE)
        return()
    endif()

    if(pin_ok)
        set(why "The files of the parent tree differ from the files at commit ${pin}.")
    else()
        set(why "The pinned commit ${pin} is not in this clone (a shallow clone needs the full history).")
    endif()
    message(FATAL_ERROR "${why}\n"
        "ngc-host/LL-RUNTIME-PIN names the parent commit this library is built against, and saved scripts are stamped with it.\n"
        "Either update LL-RUNTIME-PIN to the commit whose files you are building, or configure with "
        "-DSLUA_ALLOW_UNPINNED_TREE=ON to stamp a hash of the parent tree instead.")
endfunction()

if(CMAKE_SCRIPT_MODE_FILE AND CMAKE_SCRIPT_MODE_FILE STREQUAL CMAKE_CURRENT_LIST_FILE)
    slua_runtime_stamp("${SLUA_SOURCE_DIR}" "${SLUA_PIN_FILE}" "${SLUA_ALLOW_UNPINNED_TREE}" stamp)
    file(WRITE "${SLUA_STAMP_OUT}" "${stamp}\n")
endif()
