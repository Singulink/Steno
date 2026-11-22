# The C reference implementation of the Steno Encoding. CMake is optional: the library is steno.c and steno.h.
#
#   cmake -S . -B build && cmake --build build && ctest --test-dir build --output-on-failure
#
# Options: STENO_NO_SIMD (scalar code only), STENO_BUILD_TESTS (default ON when this is the top-level project), and the standard
# BUILD_SHARED_LIBS for a shared library (only the STENO_API functions are exported; users get STENO_SHARED through the target).

cmake_minimum_required(VERSION 3.13)
project(steno VERSION 1.0.0 LANGUAGES C)

option(STENO_NO_SIMD "Turn off the vector code" OFF)

if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    set(steno_top ON)
else()
    set(steno_top OFF)
endif()

option(STENO_BUILD_TESTS "Build the tests" ${steno_top})

add_library(steno steno.c)
target_include_directories(steno PUBLIC $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}> $<INSTALL_INTERFACE:include>)
set_target_properties(steno PROPERTIES C_STANDARD 99 C_STANDARD_REQUIRED ON PUBLIC_HEADER steno.h VERSION ${PROJECT_VERSION}
                                       C_VISIBILITY_PRESET hidden)

get_target_property(steno_type steno TYPE)
if(steno_type STREQUAL "SHARED_LIBRARY")
    target_compile_definitions(steno PRIVATE STENO_BUILD_SHARED INTERFACE STENO_SHARED)
endif()

if(STENO_NO_SIMD)
    target_compile_definitions(steno PRIVATE STENO_NO_SIMD)
endif()

if(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
    target_compile_options(steno PRIVATE -Wall -Wextra -Wpedantic -Wconversion -Wshadow)
elseif(MSVC)
    target_compile_options(steno PRIVATE /W4)
endif()

include(GNUInstallDirs)
install(TARGETS steno EXPORT steno-targets
        ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
        LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
        RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR}
        PUBLIC_HEADER DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
install(EXPORT steno-targets NAMESPACE steno:: DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/steno FILE steno-config.cmake)

if(STENO_BUILD_TESTS)
    enable_testing()
    find_package(Threads REQUIRED)

    set(steno_vectors ${CMAKE_CURRENT_SOURCE_DIR}/../../TestVectors/steno-v1-vectors.txt)

    # The tests that use only steno.h link with the library (shared or static).
    add_executable(test_steno tests/test_steno.c tests/ref.c tests/oracle.c)
    add_executable(test_oracle_vectors tests/test_oracle_vectors.c tests/oracle.c)
    add_executable(test_api tests/test_api.c)
    add_executable(test_digest tests/test_digest.c)
    add_executable(test_differential tests/fuzz_differential.c tests/oracle.c)
    target_compile_definitions(test_differential PRIVATE STENO_FUZZ_STANDALONE)
    add_executable(test_sweep tests/test_sweep.c tests/oracle.c)
    add_executable(test_readme tests/readme_examples.c)
    add_executable(test_threads tests/test_threads.c)
    add_executable(test_hop tests/test_hop.c)
    set(steno_tests test_steno test_oracle_vectors test_api test_digest test_differential test_sweep test_readme test_threads test_hop)

    foreach(t ${steno_tests})
        target_link_libraries(${t} PRIVATE steno)
    endforeach()

    target_link_libraries(test_threads PRIVATE Threads::Threads)

    # The internal tests include steno.c itself.
    add_executable(test_internal tests/test_internal.c tests/ref.c)
    list(APPEND steno_tests test_internal)

    if(STENO_NO_SIMD)
        target_compile_definitions(test_internal PRIVATE STENO_NO_SIMD)
    endif()

    add_test(NAME internal COMMAND test_internal)
    add_test(NAME steno COMMAND test_steno ${steno_vectors})
    add_test(NAME oracle_vectors COMMAND test_oracle_vectors ${steno_vectors})
    add_test(NAME api COMMAND test_api)
    add_test(NAME digest COMMAND test_digest)
    add_test(NAME differential COMMAND test_differential 1 5000)
    add_test(NAME sweep COMMAND test_sweep)
    add_test(NAME readme COMMAND test_readme)
    add_test(NAME threads COMMAND test_threads)
    add_test(NAME hop COMMAND test_hop)

    # The C interface test of the Rust implementation, against this library.
    if(EXISTS ${CMAKE_CURRENT_SOURCE_DIR}/../rust/capi/tests/c/test.c)
        add_executable(test_rust_capi ${CMAKE_CURRENT_SOURCE_DIR}/../rust/capi/tests/c/test.c)
        target_link_libraries(test_rust_capi PRIVATE steno)
        list(APPEND steno_tests test_rust_capi)
        add_test(NAME rust_capi COMMAND test_rust_capi)
    endif()

    set_target_properties(${steno_tests} PROPERTIES C_STANDARD 99)

    if(MSVC)
        # The README examples have UTF-8 string literals.
        foreach(t ${steno_tests})
            target_compile_options(${t} PRIVATE /utf-8)
        endforeach()
    endif()
endif()
