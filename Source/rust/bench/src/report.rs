//! Turns the results of the benchmark (Criterion's `target/criterion/<group>/<benchmark>/new/estimates.json`) into `bench/results.csv`,
//! in the common format of the reference implementations, and `BENCHMARKS.md`.
//!
//! Run it after the benchmark: `cargo run --release -p singulink-steno-bench --bin report` (`bench/run.sh` runs both). Benchmarks that have no results are left out with a warning,
//! so a partial run gives a partial report. Set `STENO_BENCH_MACHINE` to name the machine if the CPU name cannot be found.

use std::fmt::Write as _;
use std::path::{Path, PathBuf};
use std::process::Command;

use singulink_steno_bench::{load, utf16_units, KINDS, TEXTS};

/// (operation, form, codec)
const BENCHMARKS: &[(&str, &str, &str)] = &[
    ("encode", "native", "steno"),
    ("encode", "utf16", "steno"),
    ("encode", "utf16", "utf8"),
    ("decode", "native", "steno"),
    ("decode", "utf16", "steno"),
    ("decode", "utf16", "utf8"),
];

struct Row {
    text: &'static str,
    kind: &'static str,
    operation: &'static str,
    form: &'static str,
    codec: &'static str,
    ns: f64,
    error_ns: f64,
    values: usize,
    units: usize,
}

/// The folder of this crate, `bench`.
fn root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
}

/// The workspace folder.
fn workspace() -> PathBuf {
    root().join("..")
}

/// The number that follows `"key":` after `from` in `json`.
fn number_after(json: &str, from: usize, key: &str) -> Option<f64> {
    let pattern = format!("\"{key}\":");
    let at = from + json[from..].find(&pattern)? + pattern.len();
    let rest = json[at..].trim_start();
    let end = rest.find(|c: char| !(c.is_ascii_digit() || matches!(c, '.' | '-' | '+' | 'e' | 'E'))).unwrap_or(rest.len());
    rest[..end].parse().ok()
}

/// The mean time per iteration in ns and the half-width of its 95% confidence interval.
fn read_estimate(path: &Path) -> Option<(f64, f64)> {
    let json = std::fs::read_to_string(path).ok()?;
    let mean = json.find("\"mean\":")?;
    let point = number_after(&json, mean, "point_estimate")?;
    let lower = number_after(&json, mean, "lower_bound")?;
    let upper = number_after(&json, mean, "upper_bound")?;
    Some((point, (upper - lower) / 2.0))
}

fn criterion_version() -> String {
    lock_version("criterion")
}

/// The version of a package in `Cargo.lock`.
fn lock_version(name: &str) -> String {
    let lock = std::fs::read_to_string(workspace().join("Cargo.lock")).unwrap_or_default();
    let line = format!("name = \"{name}\"");
    lock.split("[[package]]")
        .find(|p| p.lines().any(|l| l == line))
        .and_then(|p| p.lines().find_map(|l| l.strip_prefix("version = \"")).map(|v| v.trim_end_matches('"').to_string()))
        .unwrap_or_else(|| "unknown".into())
}

/// The codecs of the native comparison, with their form, and the library that does the work.
const NATIVE: &[(&str, &str, &str)] = &[
    ("steno", "native", "Steno, UTF-8"),
    ("steno", "utf16", "Steno, UTF-16"),
    ("steno-c", "native", "Steno C, UTF-8"),
    ("steno-c", "utf16", "Steno C, UTF-16"),
    ("steno-c-scalar", "native", "Steno C scalar, UTF-8"),
    ("steno-c-scalar", "utf16", "Steno C scalar, UTF-16"),
    ("utf8-copy", "native", "UTF-8 copy"),
    ("utf8", "utf16", "UTF-8"),
    ("scsu-icu", "utf16", "SCSU"),
    ("bocu1-icu", "utf16", "BOCU-1"),
    ("zstd-1", "native", "Zstd 1"),
    ("zstd-19", "native", "Zstd 19"),
    ("brotli-1", "native", "Brotli 1"),
    ("brotli-11", "native", "Brotli 11"),
    ("lz4", "native", "LZ4"),
    ("deflate-6", "native", "Deflate 6"),
];

/// Writes `bench/native-results.csv` and `bench/native-libraries.csv`, and the section of `BENCHMARKS.md` on the native comparison, if
/// the native benchmark has results.
fn native_report(criterion: &std::path::Path, rustc: &str, md: &mut String) {
    let mut rows: Vec<Row> = Vec::new();

    for &(text, _) in TEXTS {
        for &kind in KINDS {
            let values = load(text, kind);
            let units = utf16_units(&values);

            for &op in &["encode", "decode"] {
                for &(codec, form, _) in NATIVE {
                    let path =
                        criterion.join(format!("native.{text}.{kind}")).join(format!("{op}.{form}.{codec}")).join("new/estimates.json");

                    if let Some((ns, error_ns)) = read_estimate(&path) {
                        rows.push(Row { text, kind, operation: op, form, codec, ns, error_ns, values: values.len(), units });
                    }
                }
            }
        }

        // The comparisons: on the sorted, distinct 16-character values; `values` is the number of pairs.
        let mut values = load(text, "values");
        values.sort();
        values.dedup();
        let units = utf16_units(&values);

        for pairs in ["neighbours", "random"] {
            for codec in ["steno", "steno-decode", "utf8"] {
                let path = criterion.join(format!("compare.{text}")).join(format!("compare.{pairs}.{codec}")).join("new/estimates.json");

                if let Some((ns, error_ns)) = read_estimate(&path) {
                    rows.push(Row {
                        text,
                        kind: "values",
                        operation: "compare",
                        form: pairs,
                        codec,
                        ns,
                        error_ns,
                        values: values.len() - 1,
                        units,
                    });
                }
            }
        }
    }

    if rows.is_empty() {
        return;
    }

    let mut csv = String::from("implementation,text,kind,operation,form,codec,ns_per_operation,error_ns,values,utf16_units\n");

    for r in &rows {
        let _ = writeln!(
            csv,
            "rust,{},{},{},{},{},{:.1},{:.1},{},{}",
            r.text, r.kind, r.operation, r.form, r.codec, r.ns, r.error_ns, r.values, r.units
        );
    }

    std::fs::write(root().join("native-results.csv"), csv).expect("write bench/native-results.csv");

    // The libraries, with their versions.
    let env = std::fs::read_to_string(criterion.join("native-environment.txt")).unwrap_or_default();
    let icu = env.lines().find_map(|l| l.strip_prefix("icu=")).unwrap_or("unknown").to_string();
    let c_compiler = env.lines().find_map(|l| l.strip_prefix("c-compiler=")).unwrap_or("unknown").to_string();
    let c_version = std::fs::read_to_string(workspace().join("../c/steno.h"))
        .ok()
        .and_then(|h| {
            h.lines().find_map(|l| l.strip_prefix("#define STENO_VERSION_STRING \"").map(|v| v.trim_end_matches('"').to_string()))
        })
        .unwrap_or_else(|| "unknown".into());
    let simd = if std::env::var_os("STENO_SIMD").is_some_and(|v| v == "0") { "vector code off" } else { "vector code on" };
    let steno = format!("{} ({simd})", lock_version("singulink-steno"));
    let libraries: Vec<(&str, &str, &str, String)> = vec![
        ("steno", "singulink-steno", "Rust, this crate", steno),
        (
            "steno-c",
            "the plain C implementation (Source/c/steno.c), built with the cc crate at -O2, vector code on",
            "C",
            format!("{c_version}, {c_compiler}"),
        ),
        (
            "steno-c-scalar",
            "the plain C implementation (Source/c/steno.c), built with -DSTENO_NO_SIMD",
            "C",
            format!("{c_version}, {c_compiler}"),
        ),
        ("utf8-copy", "Rust standard library (str::from_utf8 and a copy)", "Rust", rustc.to_string()),
        ("utf8", "Rust standard library (char::decode_utf16, str::encode_utf16)", "Rust", rustc.to_string()),
        ("scsu-icu", "ICU4C, ucnv_fromUChars and ucnv_toUChars", "C, the system icu.dll", icu.clone()),
        ("bocu1-icu", "ICU4C, ucnv_fromUChars and ucnv_toUChars", "C, the system icu.dll", icu),
        (
            "zstd-1 zstd-19",
            "libzstd through the zstd crate (bulk API, contexts reused)",
            "C",
            format!("zstd-sys {}", lock_version("zstd-sys")),
        ),
        (
            "brotli-1 brotli-11",
            "Google brotli through the brotlic crate (BrotliEncoderCompress, BrotliDecoderDecompress)",
            "C",
            format!("brotlic-sys {}", lock_version("brotlic-sys")),
        ),
        ("lz4", "liblz4 through the lz4 crate (block API)", "C", format!("lz4-sys {}", lock_version("lz4-sys"))),
        (
            "deflate-6",
            "zlib through flate2 and libz-sys (raw deflate, streams reset per value)",
            "C",
            format!("libz-sys {}", lock_version("libz-sys")),
        ),
    ];

    let mut lib_csv = String::from("codec,library,implementation,version\n");

    for (codec, lib, implementation, version) in &libraries {
        for c in codec.split(' ') {
            let _ = writeln!(lib_csv, "{c},\"{lib}\",\"{implementation}\",\"{version}\"");
        }
    }

    std::fs::write(root().join("native-libraries.csv"), lib_csv).expect("write bench/native-libraries.csv");

    // BENCHMARKS.md.
    let find = |text: &str, kind: &str, op: &str, form: &str, codec: &str| {
        rows.iter().find(|r| r.text == text && r.kind == kind && r.operation == op && r.form == form && r.codec == codec)
    };

    let _ = writeln!(md, "## Native Comparison");
    let _ = writeln!(md);
    let _ = writeln!(md, "Generated from `bench/native-results.csv` (`cargo bench -p singulink-steno-bench --bench native`). Each value is encoded and decoded on its own, into buffers allocated once, with flat sampling: 0.5 s of warm-up, 1 s of measurement and 20 samples per benchmark. The figures are Criterion's means; `bench/native-results.csv` has the half-width of each 95% confidence interval.");
    let _ = writeln!(md);
    let _ = writeln!(md, "| Codec | Library | Implementation | Version |");
    let _ = writeln!(md, "|---|---|---|---|");

    for (codec, lib, implementation, version) in &libraries {
        let _ = writeln!(md, "| `{}` | {lib} | {implementation} | {version} |", codec.replace(' ', "`, `"));
    }

    let _ = writeln!(md);
    let _ = writeln!(md, "Forms: Steno, Steno C, UTF-8 copy and the compressors take and give UTF-8 (`&str` or UTF-8 bytes); Steno and Steno C in their UTF-16 form, UTF-8, SCSU and BOCU-1 take and give UTF-16 code units. Steno C is called through its C interface, once per value.");
    let _ = writeln!(md);

    for (kind, title, per_value) in [
        ("document", "Whole Documents: Millions of UTF-16 Code Units per Second (Higher Is Better)", false),
        ("values", "16-Character Values: Nanoseconds per Value (Lower Is Better)", true),
    ] {
        for op in ["encode", "decode"] {
            let present: Vec<_> = NATIVE
                .iter()
                .filter(|&&(c, f, _)| rows.iter().any(|r| r.codec == c && r.form == f && r.operation == op && r.kind == kind))
                .collect();

            if present.is_empty() {
                continue;
            }

            let _ = writeln!(md, "### {title}, {}", if op == "encode" { "Encode" } else { "Decode" });
            let _ = writeln!(md);
            let _ = writeln!(md, "| Text | {} |", present.iter().map(|p| p.2).collect::<Vec<_>>().join(" | "));
            let _ = writeln!(md, "|---|{}", "--:|".repeat(present.len()));

            for &(text, name) in TEXTS {
                let cells: Vec<String> = present
                    .iter()
                    .map(|&&(codec, form, _)| match find(text, kind, op, form, codec) {
                        Some(r) if per_value => num(r.ns / r.values as f64, 1),
                        Some(r) => num(r.units as f64 / r.ns * 1000.0, 0),
                        None => "n/a".into(),
                    })
                    .collect();
                let _ = writeln!(md, "| {name} | {} |", cells.join(" | "));
            }

            let _ = writeln!(md);
        }
    }

    let _ = writeln!(md, "### Comparing Two Values: Nanoseconds per Comparison (Lower Is Better)");
    let _ = writeln!(md);
    let _ = writeln!(md, "On the distinct 16-character values of each text, sorted: each pair of neighbours, and as many random pairs. Steno compares the Steno bytes as they are; Steno decoded decodes both values to UTF-8 and compares the text; UTF-8 compares the two `str`.");
    let _ = writeln!(md);
    let _ = writeln!(md, "| Text | Neighbours, Steno | Neighbours, Steno decoded | Neighbours, UTF-8 | Random, Steno | Random, Steno decoded | Random, UTF-8 |");
    let _ = writeln!(md, "|---|--:|--:|--:|--:|--:|--:|");

    for &(text, name) in TEXTS {
        let cell = |pairs: &str, codec: &str| match find(text, "values", "compare", pairs, codec) {
            Some(r) => num(r.ns / r.values as f64, 1),
            None => "n/a".into(),
        };
        let _ = writeln!(
            md,
            "| {name} | {} | {} | {} | {} | {} | {} |",
            cell("neighbours", "steno"),
            cell("neighbours", "steno-decode"),
            cell("neighbours", "utf8"),
            cell("random", "steno"),
            cell("random", "steno-decode"),
            cell("random", "utf8")
        );
    }

    let _ = writeln!(md);
    let _ = writeln!(md, "Caveats of the native comparison:");
    let _ = writeln!(md);
    let _ = writeln!(md, "- UTF-8 copy is the honest baseline for a system that already holds UTF-8: its encoding is a validation and a copy. Steno's UTF-8 form takes a `&str`, which Rust has already validated, so it does no validation of its own.");
    let _ = writeln!(md, "- ICU is called through its C API once per value; the call resets the converter and checks its arguments, which costs most on short values. Its converters work on UTF-16 only.");
    let _ = writeln!(md, "- The compressors work on each value by itself, as a database stores a field. Their frame headers and set-up cost most on short values: one-shot Brotli (`BrotliEncoderCompress`) sets up an encoder on every call, while Zstd and Deflate reuse a context and LZ4 needs none.");
    let _ = writeln!(md, "- All compressors are the C reference libraries, built here with `zig cc` from the sources that their crates bundle, with the crates' default build settings.");
    let _ = writeln!(md);
}

fn command_output(program: &str, args: &[&str]) -> Option<String> {
    let out = Command::new(program).args(args).output().ok()?;
    out.status.success().then(|| String::from_utf8_lossy(&out.stdout).trim().to_string())
}

fn machine() -> String {
    if let Ok(m) = std::env::var("STENO_BENCH_MACHINE") {
        return m;
    }

    let cpu = if cfg!(windows) {
        command_output("reg", &["query", r"HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "/v", "ProcessorNameString"])
            .and_then(|o| o.lines().find_map(|l| l.split("REG_SZ").nth(1).map(|s| s.trim().to_string())))
    } else {
        std::fs::read_to_string("/proc/cpuinfo")
            .ok()
            .and_then(|c| c.lines().find_map(|l| l.strip_prefix("model name").map(|r| r.trim_start_matches([' ', '\t', ':']).to_string())))
            .or_else(|| command_output("sysctl", &["-n", "machdep.cpu.brand_string"]))
    };

    format!("{}, {} {}", cpu.unwrap_or_else(|| "unknown CPU".into()), std::env::consts::OS, std::env::consts::ARCH)
}

/// Today's date (UTC) as YYYY-MM-DD.
fn today() -> String {
    let days = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs() / 86_400).unwrap_or(0) as i64;
    // Civil date from days since 1970-01-01 (Howard Hinnant's algorithm).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = yoe + era * 400 + i64::from(m <= 2);
    format!("{y:04}-{m:02}-{d:02}")
}

/// Formats with a thousands separator and the given number of decimals.
fn num(x: f64, decimals: usize) -> String {
    let s = format!("{x:.decimals$}");
    let (int, frac) = s.split_once('.').map_or((s.as_str(), None), |(i, f)| (i, Some(f)));
    let mut out = String::new();

    for (i, ch) in int.chars().enumerate() {
        if i > 0 && (int.len() - i) % 3 == 0 {
            out.push(',');
        }

        out.push(ch);
    }

    if let Some(f) = frac {
        out.push('.');
        out.push_str(f);
    }

    out
}

fn main() {
    let target = std::env::var_os("CARGO_TARGET_DIR").map_or_else(|| workspace().join("target"), PathBuf::from);
    let criterion = target.join("criterion");
    let mut rows = Vec::new();
    let mut missing = Vec::new();

    for &(text, _) in TEXTS {
        for &kind in KINDS {
            let values = load(text, kind);
            let units = utf16_units(&values);

            for &(operation, form, codec) in BENCHMARKS {
                let path = criterion.join(format!("{text}.{kind}")).join(format!("{operation}.{form}.{codec}")).join("new/estimates.json");

                match read_estimate(&path) {
                    Some((ns, error_ns)) => {
                        rows.push(Row { text, kind, operation, form, codec, ns, error_ns, values: values.len(), units });
                    }
                    None => missing.push(format!("{text}.{kind}/{operation}.{form}.{codec}")),
                }
            }
        }
    }

    if !missing.is_empty() {
        eprintln!("warning: {} of {} benchmarks have no result, for example {}", missing.len(), missing.len() + rows.len(), missing[0]);
    }

    // bench/results.csv
    let mut csv = String::from("implementation,text,kind,operation,form,codec,ns_per_operation,error_ns,values,utf16_units\n");

    for r in &rows {
        let _ = writeln!(
            csv,
            "rust,{},{},{},{},{},{:.1},{:.1},{},{}",
            r.text, r.kind, r.operation, r.form, r.codec, r.ns, r.error_ns, r.values, r.units
        );
    }

    std::fs::write(root().join("results.csv"), csv).expect("write bench/results.csv");

    // BENCHMARKS.md
    let find = |text: &str, kind: &str, op: &str, form: &str, codec: &str| {
        rows.iter().find(|r| r.text == text && r.kind == kind && r.operation == op && r.form == form && r.codec == codec)
    };
    let rustc = command_output("rustc", &["-V"]).unwrap_or_else(|| "rustc (unknown version)".into());

    let mut md = String::new();
    let _ = writeln!(md, "# Benchmarks of the Rust reference implementation");
    let _ = writeln!(md);
    let _ = writeln!(md, "Generated by `bench/src/report.rs` from the results of `bench/run.sh`. Do not edit by hand.");
    let _ = writeln!(md);
    let _ = writeln!(md, "- Tool: Criterion {}, 1 s of warm-up, 2 s of measurement and 50 samples per benchmark.", criterion_version());
    let _ = writeln!(md, "- Compiler: {rustc}, release profile with `lto = true` and `codegen-units = 1`.");
    let _ = writeln!(md, "- Machine: {}. Date: {}.", machine(), today());
    let _ = writeln!(md, "- Workloads: `Benchmarks/Workloads` of the repository: each text as one whole document, and cut into values of at most 16 UTF-16 code units.");
    let _ = writeln!(
        md,
        "- The figures are Criterion's mean time per iteration (one iteration processes every value of the workload once), with the half-width of its 95% confidence interval after `±`."
    );
    let _ = writeln!(md);
    let _ = writeln!(md, "Forms: **str** is `&str` to Steno bytes and Steno bytes to a `String`; **UTF-16** is `&[u16]` to Steno bytes and back. The **UTF-8** baseline is the standard library converting the same UTF-16 code units to UTF-8 (`char::decode_utf16` into a `String`) and UTF-8 bytes to UTF-16 (`str::from_utf8`, then `str::encode_utf16` into a `Vec`). **Ratio** is the time of the UTF-8 baseline divided by the time of Steno in the UTF-16 form: above 1 means that Steno is faster.");
    let _ = writeln!(md);

    for (kind, title, per_value) in [
        ("values", "16-char values: nanoseconds per value (lower is better)", true),
        ("document", "Whole documents: millions of UTF-16 code units per second (higher is better)", false),
    ] {
        let _ = writeln!(md, "## {title}");
        let _ = writeln!(md);
        let _ = writeln!(
            md,
            "| Text | Encode str | Encode UTF-16 | UTF-8 baseline | Ratio | Decode str | Decode UTF-16 | UTF-8 baseline | Ratio |"
        );
        let _ = writeln!(md, "|---|--:|--:|--:|--:|--:|--:|--:|--:|");

        for &(text, name) in TEXTS {
            let cell = |op: &str, form: &str, codec: &str| match find(text, kind, op, form, codec) {
                Some(r) if per_value => format!("{} ± {}", num(r.ns / r.values as f64, 1), num(r.error_ns / r.values as f64, 1)),
                Some(r) => {
                    let rate = r.units as f64 / r.ns * 1000.0;
                    format!("{} ± {}", num(rate, 0), num(rate * r.error_ns / r.ns, 0))
                }
                None => "n/a".into(),
            };
            let ratio = |op: &str| match (find(text, kind, op, "utf16", "steno"), find(text, kind, op, "utf16", "utf8")) {
                (Some(s), Some(u)) => format!("{:.2}", u.ns / s.ns),
                _ => "n/a".into(),
            };

            let _ = writeln!(
                md,
                "| {name} | {} | {} | {} | {} | {} | {} | {} | {} |",
                cell("encode", "native", "steno"),
                cell("encode", "utf16", "steno"),
                cell("encode", "utf16", "utf8"),
                ratio("encode"),
                cell("decode", "native", "steno"),
                cell("decode", "utf16", "steno"),
                cell("decode", "utf16", "utf8"),
                ratio("decode"),
            );
        }

        let _ = writeln!(md);
    }

    native_report(&criterion, &rustc, &mut md);

    let _ = writeln!(md, "## Caveats");
    let _ = writeln!(md);
    let _ = writeln!(md, "- The figures come from one machine, which may have been doing other work while they were measured; the confidence intervals show how much the measurements spread. They are indicative, not exact.");
    let _ = writeln!(
        md,
        "- Speed is a property of an implementation, not of the encoding. This one runs vector code (SSSE3 on x86-64, NEON on ARM64) with a scalar fallback; `STENO_SIMD=0` turns the vector code off for a run."
    );
    let _ = writeln!(
        md,
        "- In Rust, `&str` is already UTF-8, so the UTF-8 baseline is measured only in the UTF-16 form, where it is a real conversion."
    );
    let _ = writeln!(md, "- The output buffers are allocated once: the measurements are of the codecs, not of the allocator.");

    std::fs::write(workspace().join("BENCHMARKS.md"), md).expect("write BENCHMARKS.md");

    // The summary block of README.md: whole documents in the UTF-16 and str forms, and a link to the full report.
    let mut summary = String::new();
    let _ = writeln!(
        summary,
        "Millions of UTF-16 code units per second on whole documents, as Criterion measured them on {} ({}):",
        machine(),
        today()
    );
    let _ = writeln!(summary);
    let _ = writeln!(summary, "| Text | Encode str | Decode str | Encode UTF-16 | Decode UTF-16 |");
    let _ = writeln!(summary, "|---|--:|--:|--:|--:|");

    for &(text, name) in TEXTS {
        let rate = |op: &str, form: &str| match find(text, "document", op, form, "steno") {
            Some(r) => num(r.units as f64 / r.ns * 1000.0, 0),
            None => "n/a".into(),
        };

        let _ = writeln!(
            summary,
            "| {name} | {} | {} | {} | {} |",
            rate("encode", "native"),
            rate("decode", "native"),
            rate("encode", "utf16"),
            rate("decode", "utf16")
        );
    }

    let _ = writeln!(summary);
    let _ = write!(
        summary,
        "The figures depend on the machine and were measured while it may have been doing other work. [BENCHMARKS.md](BENCHMARKS.md) has the          16-character values, the spread of each figure and the ratio to the standard library's UTF-8 conversions."
    );

    let readme_path = workspace().join("README.md");
    let readme = std::fs::read_to_string(&readme_path).expect("read README.md");
    let (start, end) = (
        "<!-- benchmark-summary:start -->
",
        "<!-- benchmark-summary:end -->",
    );

    match (readme.find(start), readme.find(end)) {
        (Some(a), Some(b)) if a < b => {
            let updated = format!(
                "{}{start}{summary}
{}",
                &readme[..a],
                &readme[b..]
            );
            std::fs::write(&readme_path, updated).expect("write README.md");
        }
        _ => eprintln!("warning: README.md has no benchmark summary block; it was left as it was"),
    }

    println!("wrote bench/results.csv ({} rows), BENCHMARKS.md and the summary in README.md", rows.len());
}
