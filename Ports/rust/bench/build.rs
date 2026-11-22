//! Compiles the plain C implementation of the Steno Encoding (`Ports/c/steno.c`) twice for the native comparison: as it is (`steno_c_*`,
//! with its vector code) and with `STENO_NO_SIMD` (`steno_cs_*`, scalar only). The exported names are renamed by macros, so that the two
//! copies (and the C interface of the Rust implementation, which this crate does not link) cannot clash.

use std::path::PathBuf;

/// Every function that `steno.c` exports.
const EXPORTS: &[&str] = &[
    "decode_code_points",
    "decode_utf16",
    "decode_utf8",
    "decoded_len_code_points",
    "decoded_len_utf16",
    "decoded_len_utf8",
    "decoder_feed_code_points",
    "decoder_feed_utf16",
    "decoder_feed_utf8",
    "decoder_finish",
    "decoder_init",
    "decoder_reset",
    "encode_code_points",
    "encode_utf16",
    "encode_utf8",
    "encoded_len_code_points",
    "encoded_len_utf16",
    "encoded_len_utf8",
    "encoder_feed_code_points",
    "encoder_feed_utf16",
    "encoder_feed_utf8",
    "encoder_finish",
    "encoder_init",
    "encoder_reset",
    "is_ascii_text",
    "max_decoded_len_code_points",
    "max_decoded_len_utf16",
    "max_decoded_len_utf8",
    "max_encoded_len_code_points",
    "max_encoded_len_utf16",
    "max_encoded_len_utf8",
    "status_name",
];

fn main() {
    let source = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../c/steno.c");
    println!("cargo:rerun-if-changed={}", source.display());
    println!("cargo:rerun-if-changed={}", source.with_file_name("steno.h").display());

    for (prefix, lib, scalar) in [("steno_c_", "steno_plain_c", false), ("steno_cs_", "steno_plain_c_scalar", true)] {
        let mut build = cc::Build::new();
        build.file(&source).opt_level(2).flag_if_supported("-std=c99").warnings(false);

        for name in EXPORTS {
            build.define(&format!("steno_{name}"), format!("{prefix}{name}").as_str());
        }

        if scalar {
            build.define("STENO_NO_SIMD", None);
        }

        build.compile(lib);
    }

    // The compiler, for the report.
    let compiler = cc::Build::new().get_compiler();
    let version = std::process::Command::new(compiler.path())
        .args(compiler.args())
        .arg("--version")
        .output()
        .ok()
        .and_then(|o| String::from_utf8(o.stdout).ok())
        .and_then(|s| s.lines().next().map(str::to_string))
        .unwrap_or_else(|| compiler.path().display().to_string());
    println!("cargo:rustc-env=STENO_C_COMPILER={version}");
}
