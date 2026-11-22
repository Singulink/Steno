//! ICU4C's converters (SCSU and BOCU-1 among them), loaded at run time, so that the benchmark builds and runs where ICU is missing.
//!
//! On Windows, ICU is the system `icu.dll` (Windows 10 1903 and later), whose exports have no version suffix. On Linux, the functions of
//! `libicuuc.so.NN` carry the major version as a suffix (`ucnv_open_74`): loading them would need `dlopen` and the suffix, which this
//! module does not do yet; there, `Icu::load` returns `None`.

use std::ffi::{c_char, c_void, CString};

type UErrorCode = i32;
type OpenFn = unsafe extern "C" fn(*const c_char, *mut UErrorCode) -> *mut c_void;
type CloseFn = unsafe extern "C" fn(*mut c_void);
type FromUCharsFn = unsafe extern "C" fn(*mut c_void, *mut u8, i32, *const u16, i32, *mut UErrorCode) -> i32;
type ToUCharsFn = unsafe extern "C" fn(*mut c_void, *mut u16, i32, *const u8, i32, *mut UErrorCode) -> i32;
type GetVersionFn = unsafe extern "C" fn(*mut u8);

/// The functions of ICU's C API that the benchmark calls.
#[derive(Clone, Copy)]
pub struct Icu {
    open: OpenFn,
    close: CloseFn,
    from_uchars: FromUCharsFn,
    to_uchars: ToUCharsFn,
    get_version: GetVersionFn,
}

#[cfg(windows)]
#[link(name = "kernel32")]
extern "system" {
    fn LoadLibraryA(name: *const c_char) -> *mut c_void;
    fn GetProcAddress(module: *mut c_void, name: *const c_char) -> *mut c_void;
}

impl Icu {
    /// Loads ICU, or returns `None` if it is not there.
    #[cfg(windows)]
    pub fn load() -> Option<Icu> {
        // SAFETY: the names are NUL-terminated strings, and each pointer that `GetProcAddress` returns is cast to the type of the C
        // function of that name in ICU's C API (`ucnv.h`, `uversion.h`).
        unsafe {
            let module = LoadLibraryA(c"icu.dll".as_ptr());

            if module.is_null() {
                return None;
            }

            let get = |name: &std::ffi::CStr| {
                let f = GetProcAddress(module, name.as_ptr());
                (!f.is_null()).then_some(f)
            };

            Some(Icu {
                open: std::mem::transmute::<*mut c_void, OpenFn>(get(c"ucnv_open")?),
                close: std::mem::transmute::<*mut c_void, CloseFn>(get(c"ucnv_close")?),
                from_uchars: std::mem::transmute::<*mut c_void, FromUCharsFn>(get(c"ucnv_fromUChars")?),
                to_uchars: std::mem::transmute::<*mut c_void, ToUCharsFn>(get(c"ucnv_toUChars")?),
                get_version: std::mem::transmute::<*mut c_void, GetVersionFn>(get(c"u_getVersion")?),
            })
        }
    }

    /// ICU is loaded only on Windows for now (see the module documentation).
    #[cfg(not(windows))]
    pub fn load() -> Option<Icu> {
        None
    }

    /// ICU's version, such as `72.1`.
    pub fn version(&self) -> String {
        let mut v = [0u8; 4];
        // SAFETY: `u_getVersion` writes the four bytes of a `UVersionInfo`.
        unsafe { (self.get_version)(v.as_mut_ptr()) };
        format!("{}.{}", v[0], v[1])
    }

    /// Opens a converter by name (`"SCSU"`, `"BOCU-1"`).
    pub fn converter(&self, name: &str) -> Option<Converter> {
        let name = CString::new(name).ok()?;
        let mut error = 0;
        // SAFETY: `name` is a NUL-terminated string and `error` a `UErrorCode` that ICU writes.
        let handle = unsafe { (self.open)(name.as_ptr(), &mut error) };
        (error <= 0 && !handle.is_null()).then_some(Converter { icu: *self, handle })
    }
}

/// An open ICU converter. `ucnv_fromUChars` and `ucnv_toUChars` reset it at the start of each call, so each call converts one whole value.
pub struct Converter {
    icu: Icu,
    handle: *mut c_void,
}

impl Converter {
    /// Converts UTF-16 to the converter's bytes, and returns their number.
    #[inline]
    pub fn encode(&mut self, src: &[u16], dst: &mut [u8]) -> usize {
        let mut error = 0;
        // SAFETY: the pointers and lengths describe live slices; ICU writes at most `dst.len()` bytes.
        let n =
            unsafe { (self.icu.from_uchars)(self.handle, dst.as_mut_ptr(), dst.len() as i32, src.as_ptr(), src.len() as i32, &mut error) };
        assert!(error <= 0, "ICU encode failed ({error})");
        n as usize
    }

    /// Converts the converter's bytes to UTF-16, and returns the number of code units.
    #[inline]
    pub fn decode(&mut self, src: &[u8], dst: &mut [u16]) -> usize {
        let mut error = 0;
        // SAFETY: as in `encode`.
        let n =
            unsafe { (self.icu.to_uchars)(self.handle, dst.as_mut_ptr(), dst.len() as i32, src.as_ptr(), src.len() as i32, &mut error) };
        assert!(error <= 0, "ICU decode failed ({error})");
        n as usize
    }
}

impl Drop for Converter {
    fn drop(&mut self) {
        // SAFETY: the handle came from `ucnv_open` and is closed once.
        unsafe { (self.icu.close)(self.handle) };
    }
}
