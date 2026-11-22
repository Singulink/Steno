//! Speed benchmarks of the Rust reference implementation of the Steno Encoding. This crate is not published.
//!
//! - `benches/codec.rs`: the Criterion benchmark of this implementation, next to the standard library's UTF-16 and UTF-8 conversions.
//! - `benches/native.rs`: the native comparison with the other encodings (UTF-8, SCSU and BOCU-1 through ICU) and with general compressors,
//!   and the comparison of encoded values without decoding them.
//! - `src/report.rs`: turns Criterion's results into `bench/results.csv`, `bench/native-results.csv`, `bench/native-libraries.csv` and
//!   `BENCHMARKS.md`.
//! - `run.sh` and `run-native.sh`: run them.
//!
//! This library reads the shared benchmark workloads of the repository (`Benchmarks/Workloads`) as the common rules say: no trimming and no
//! normalisation.

pub mod icu;

use std::path::PathBuf;

/// The 14 texts, in the order of the reports: file name and display name.
pub const TEXTS: &[(&str, &str)] = &[
    ("english", "English"),
    ("french", "French"),
    ("polish", "Polish"),
    ("vietnamese", "Vietnamese"),
    ("russian", "Russian"),
    ("greek", "Greek"),
    ("arabic", "Arabic"),
    ("urdu", "Urdu"),
    ("hindi", "Hindi"),
    ("thai", "Thai"),
    ("amharic", "Amharic"),
    ("chinese", "Chinese"),
    ("japanese", "Japanese"),
    ("korean", "Korean"),
];

/// The two kinds of workload.
pub const KINDS: &[&str] = &["document", "values"];

/// The folder of the workload files.
pub fn workloads_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../Benchmarks/Workloads")
}

/// The values of one workload: the whole document without its final LF, or one value per line.
pub fn load(text: &str, kind: &str) -> Vec<String> {
    let path = workloads_dir().join(format!("{text}.{kind}.txt"));
    let content = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("cannot read {}: {e}", path.display()));
    let content = content.strip_suffix('\n').unwrap_or(&content);

    if kind == "document" {
        vec![content.to_string()]
    } else {
        content.split('\n').map(str::to_string).collect()
    }
}

/// The total length of the values in UTF-16 code units.
pub fn utf16_units(values: &[String]) -> usize {
    values.iter().map(|v| v.encode_utf16().count()).sum()
}
