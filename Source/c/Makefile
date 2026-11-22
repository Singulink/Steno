# The C reference implementation of the Steno Encoding: a plain Makefile for people without CMake. Neither is needed to use the library:
# add steno.c and steno.h to your build.
#
#   make                 the static library build/libsteno.a
#   make shared          the shared library build/libsteno.so (-fvisibility=hidden; only the STENO_API functions are exported)
#   make test            build and run every test (TEST_FLAGS=-DSTENO_NO_SIMD for the scalar code)
#   make test-scalar     the tests with the vector code turned off
#   make test-shared     the tests that use only the interface, linked against the shared library
#   make sanitize        the tests under AddressSanitizer and UndefinedBehaviorSanitizer
#   make fuzz            the three libFuzzer harnesses for FUZZ_TIME seconds each (clang)
#   make exhaustive      every layout against the oracle (THREADS threads), every 3-byte string, the full fast-path sweep
#   make vs-rust         this library against the Rust implementation's C library: RUST_LIB=path/to/libsteno.so
#   make install         install the header and the library under PREFIX (default /usr/local)
#
# Set STENO_LARGE_VECTORS to the path of the large vector file to check it too.

CC ?= cc
AR ?= ar
CFLAGS ?= -O2
WARNINGS = -std=c99 -Wall -Wextra -Wpedantic -Wconversion -Wshadow
TEST_FLAGS ?=
PREFIX ?= /usr/local
BUILD ?= build
FUZZ_CC ?= clang
FUZZ_TIME ?= 30
THREADS ?= 4
THREADS_LIB ?= -pthread
DL_LIB ?= -ldl
RUST_LIB ?=
RUST_TEST_FLAGS ?=
DIFFERENTIAL_CASES ?= 5000

ORACLE = tests/oracle.c tests/oracle.h
TESTS = $(BUILD)/test_internal $(BUILD)/test_steno $(BUILD)/test_oracle_vectors $(BUILD)/test_api $(BUILD)/test_digest \
	$(BUILD)/test_differential $(BUILD)/test_sweep $(BUILD)/test_rust_capi $(BUILD)/test_readme $(BUILD)/test_threads $(BUILD)/test_hop

.PHONY: all shared test test-scalar test-shared run-tests sanitize fuzz exhaustive vs-rust install clean

all: $(BUILD)/libsteno.a

$(BUILD):
	mkdir -p $(BUILD)

$(BUILD)/steno.o: steno.c steno.h | $(BUILD)
	$(CC) $(WARNINGS) $(CFLAGS) $(TEST_FLAGS) -c steno.c -o $@

$(BUILD)/libsteno.a: $(BUILD)/steno.o
	$(AR) rcs $@ $(BUILD)/steno.o

shared: $(BUILD)/libsteno.so

$(BUILD)/libsteno.so: steno.c steno.h | $(BUILD)
	$(CC) $(WARNINGS) $(CFLAGS) $(TEST_FLAGS) -fPIC -fvisibility=hidden -DSTENO_BUILD_SHARED -shared steno.c -o $@

$(BUILD)/test_steno: tests/test_steno.c tests/ref.c tests/ref.h tests/harness.h $(ORACLE) $(BUILD)/steno.o
	$(CC) -std=c99 $(CFLAGS) $(TEST_FLAGS) tests/test_steno.c tests/ref.c tests/oracle.c $(BUILD)/steno.o -o $@

$(BUILD)/test_internal: tests/test_internal.c tests/ref.c steno.c steno.h | $(BUILD)
	$(CC) -std=c99 $(CFLAGS) $(TEST_FLAGS) tests/test_internal.c tests/ref.c -o $@

$(BUILD)/test_oracle_vectors: tests/test_oracle_vectors.c $(ORACLE) $(BUILD)/steno.o
	$(CC) -std=c99 $(CFLAGS) tests/test_oracle_vectors.c tests/oracle.c $(BUILD)/steno.o -o $@

$(BUILD)/test_api: tests/test_api.c $(BUILD)/steno.o
	$(CC) -std=c99 $(CFLAGS) tests/test_api.c $(BUILD)/steno.o -o $@

$(BUILD)/test_digest: tests/test_digest.c $(BUILD)/steno.o
	$(CC) -std=c99 $(CFLAGS) tests/test_digest.c $(BUILD)/steno.o -o $@

$(BUILD)/test_differential: tests/fuzz_differential.c $(ORACLE) $(BUILD)/steno.o
	$(CC) -std=c99 $(CFLAGS) -DSTENO_FUZZ_STANDALONE tests/fuzz_differential.c tests/oracle.c $(BUILD)/steno.o -o $@

$(BUILD)/test_sweep: tests/test_sweep.c tests/fuzz_differential.c $(ORACLE) $(BUILD)/steno.o
	$(CC) -std=c99 $(CFLAGS) tests/test_sweep.c tests/oracle.c $(BUILD)/steno.o -o $@

# The C interface test of the Rust implementation, against this library.
$(BUILD)/test_rust_capi: ../rust/capi/tests/c/test.c $(BUILD)/steno.o
	$(CC) -std=c99 $(CFLAGS) $(RUST_TEST_FLAGS) -I. ../rust/capi/tests/c/test.c $(BUILD)/steno.o -o $@

$(BUILD)/test_readme: tests/readme_examples.c $(BUILD)/steno.o
	$(CC) -std=c99 $(CFLAGS) -I. tests/readme_examples.c $(BUILD)/steno.o -o $@

$(BUILD)/test_threads: tests/test_threads.c $(BUILD)/steno.o
	$(CC) -std=c99 $(CFLAGS) tests/test_threads.c $(BUILD)/steno.o -o $@ $(THREADS_LIB)

$(BUILD)/test_hop: tests/test_hop.c $(BUILD)/steno.o
	$(CC) -std=c99 $(CFLAGS) tests/test_hop.c $(BUILD)/steno.o -o $@

$(BUILD)/test_layouts: tests/test_layouts.c $(ORACLE) steno.c steno.h | $(BUILD)
	$(CC) -std=c99 $(CFLAGS) $(TEST_FLAGS) tests/test_layouts.c tests/oracle.c -o $@ $(THREADS_LIB)

$(BUILD)/test_vs_rust: tests/test_vs_rust.c $(BUILD)/steno.o
	$(CC) -std=c99 $(CFLAGS) tests/test_vs_rust.c $(BUILD)/steno.o -o $@ $(DL_LIB)

test: $(TESTS)
	$(BUILD)/test_internal
	$(BUILD)/test_steno
	$(BUILD)/test_oracle_vectors
	$(BUILD)/test_api
	$(BUILD)/test_digest
	$(BUILD)/test_differential 1 $(DIFFERENTIAL_CASES)
	$(BUILD)/test_sweep
	$(BUILD)/test_rust_capi
	$(BUILD)/test_readme
	$(BUILD)/test_threads
	$(BUILD)/test_hop

test-scalar:
	$(MAKE) BUILD=$(BUILD)-scalar TEST_FLAGS=-DSTENO_NO_SIMD test

# The tests that use only steno.h, linked against the shared library built with -fvisibility=hidden: every function they call must be
# exported.
test-shared: $(BUILD)/libsteno.so
	$(CC) -std=c99 $(CFLAGS) -DSTENO_SHARED tests/test_steno.c tests/ref.c tests/oracle.c $(BUILD)/libsteno.so -Wl,-rpath,$(abspath $(BUILD)) -o $(BUILD)/shared_test_steno
	$(CC) -std=c99 $(CFLAGS) -DSTENO_SHARED tests/test_api.c $(BUILD)/libsteno.so -Wl,-rpath,$(abspath $(BUILD)) -o $(BUILD)/shared_test_api
	$(CC) -std=c99 $(CFLAGS) -DSTENO_SHARED tests/test_digest.c $(BUILD)/libsteno.so -Wl,-rpath,$(abspath $(BUILD)) -o $(BUILD)/shared_test_digest
	$(CC) -std=c99 $(CFLAGS) -DSTENO_SHARED -I. tests/readme_examples.c $(BUILD)/libsteno.so -Wl,-rpath,$(abspath $(BUILD)) -o $(BUILD)/shared_test_readme
	$(CC) -std=c99 $(CFLAGS) -I. ../rust/capi/tests/c/test.c $(BUILD)/libsteno.so -Wl,-rpath,$(abspath $(BUILD)) -o $(BUILD)/shared_test_rust_capi
	$(BUILD)/shared_test_steno
	$(BUILD)/shared_test_api
	$(BUILD)/shared_test_digest
	$(BUILD)/shared_test_readme
	$(BUILD)/shared_test_rust_capi

# The timing test is left out: sanitizers make every call much slower.
SANITIZE_FLAGS = -O1 -g -fno-omit-frame-pointer -fsanitize=address,undefined -fno-sanitize-recover=all

sanitize:
	$(MAKE) BUILD=$(BUILD)-sanitize CFLAGS="$(SANITIZE_FLAGS)" $(BUILD)-sanitize/test_internal $(BUILD)-sanitize/test_steno \
		$(BUILD)-sanitize/test_oracle_vectors $(BUILD)-sanitize/test_api $(BUILD)-sanitize/test_digest \
		$(BUILD)-sanitize/test_differential $(BUILD)-sanitize/test_sweep $(BUILD)-sanitize/test_rust_capi \
		$(BUILD)-sanitize/test_readme $(BUILD)-sanitize/test_threads
	$(BUILD)-sanitize/test_internal
	$(BUILD)-sanitize/test_steno
	$(BUILD)-sanitize/test_oracle_vectors
	$(BUILD)-sanitize/test_api
	$(BUILD)-sanitize/test_digest
	$(BUILD)-sanitize/test_differential 2 2000
	$(BUILD)-sanitize/test_sweep
	$(BUILD)-sanitize/test_rust_capi
	$(BUILD)-sanitize/test_readme
	$(BUILD)-sanitize/test_threads 4 300

fuzz: | $(BUILD)
	$(FUZZ_CC) -g -O1 -fsanitize=fuzzer,address,undefined tests/fuzz_decode.c tests/ref.c steno.c -o $(BUILD)/fuzz_decode
	$(FUZZ_CC) -g -O1 -fsanitize=fuzzer,address,undefined tests/fuzz_roundtrip.c tests/ref.c steno.c -o $(BUILD)/fuzz_roundtrip
	$(FUZZ_CC) -g -O1 -fsanitize=fuzzer,address,undefined tests/fuzz_differential.c tests/oracle.c steno.c -o $(BUILD)/fuzz_differential
	$(BUILD)/fuzz_decode -max_total_time=$(FUZZ_TIME) -max_len=256
	$(BUILD)/fuzz_roundtrip -max_total_time=$(FUZZ_TIME) -max_len=512
	$(BUILD)/fuzz_differential -max_total_time=$(FUZZ_TIME) -max_len=600

exhaustive: $(BUILD)/test_layouts $(BUILD)/test_steno $(BUILD)/test_sweep
	$(BUILD)/test_layouts $(THREADS)
	STENO_EXHAUSTIVE=1 $(BUILD)/test_steno
	$(BUILD)/test_sweep full

vs-rust: $(BUILD)/test_vs_rust
	@test -n "$(RUST_LIB)" || { echo "set RUST_LIB to the Rust implementation's C library (libsteno.so, libsteno.dylib or steno.dll)"; exit 2; }
	$(BUILD)/test_vs_rust $(RUST_LIB)

install: $(BUILD)/libsteno.a
	mkdir -p $(DESTDIR)$(PREFIX)/include $(DESTDIR)$(PREFIX)/lib
	cp steno.h $(DESTDIR)$(PREFIX)/include/steno.h
	cp $(BUILD)/libsteno.a $(DESTDIR)$(PREFIX)/lib/libsteno.a

clean:
	rm -rf $(BUILD) $(BUILD)-scalar $(BUILD)-sanitize
