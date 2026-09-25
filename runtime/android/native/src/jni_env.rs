// ============================================================
//  jni_env.rs — JNIEnv の関数表から、使う関数だけを番号で呼ぶ（jni クレートを足さない）
//
//  【なぜ番号で呼ぶか】
//  jni_exports.rs は依存クレートを増やさない方針で、JNIEnv* を生ポインタのまま受け取っている。起動オプション
//  （MainActivity.nativeSetLaunchOptions の byte[]。段階C-3）を読むには JNIEnv の関数を呼ぶ必要があるが、
//  使うのは 4 つだけなので、関数表（C の struct JNINativeInterface）のその位置だけを読む。
//  位置は JNI の仕様で固定（JNI 1.1 から変わっていない。後ろへ足されるだけ）。NDK r28 の
//  sysroot/usr/include/jni.h の struct JNINativeInterface の並びで数えて確かめた（先頭の reserved0 を 0 番）:
//    ExceptionClear = 17 / GetArrayLength = 171 / GetByteArrayRegion = 200 / ExceptionCheck = 228
//
//  【JNIEnv の形】
//  C の JNIEnv は「関数表へのポインタ」。JNI 関数が受け取る JNIEnv* はそのポインタへのポインタなので、
//  1 回たどると関数表の先頭（関数ポインタの並び）になる。
// ============================================================

use std::ffi::c_void;

/// 関数表での ExceptionClear の位置。
const EXCEPTION_CLEAR_INDEX: usize = 17;

/// 関数表での GetArrayLength の位置。
const GET_ARRAY_LENGTH_INDEX: usize = 171;

/// 関数表での GetByteArrayRegion の位置。
const GET_BYTE_ARRAY_REGION_INDEX: usize = 200;

/// 関数表での ExceptionCheck の位置。
const EXCEPTION_CHECK_INDEX: usize = 228;

/// jboolean の false（JNI の jboolean は符号なし 8 ビット）。
const JNI_FALSE: u8 = 0;

/// 配列の先頭の添字。
const ARRAY_START: i32 = 0;

/// `jsize GetArrayLength(JNIEnv*, jarray)`。
type GetArrayLengthFn = unsafe extern "system" fn(env: *mut c_void, array: *mut c_void) -> i32;

/// `void GetByteArrayRegion(JNIEnv*, jbyteArray, jsize start, jsize len, jbyte* buf)`。
type GetByteArrayRegionFn = unsafe extern "system" fn(env: *mut c_void, array: *mut c_void, start: i32, len: i32, buf: *mut i8);

/// `jboolean ExceptionCheck(JNIEnv*)`。
type ExceptionCheckFn = unsafe extern "system" fn(env: *mut c_void) -> u8;

/// `void ExceptionClear(JNIEnv*)`。
type ExceptionClearFn = unsafe extern "system" fn(env: *mut c_void);

/// 関数表の `index` 番目の関数ポインタを取り出す。
///
/// # Safety
/// `env` は JNI がこのスレッドへ渡した有効な JNIEnv*（null でない）であること。
unsafe fn function(env: *mut c_void, index: usize) -> *const c_void {
    // SAFETY: 呼び出し側の約束により env は有効な JNIEnv*。*env は関数表の先頭（関数ポインタの配列）で、
    // index は JNI の仕様で決まった範囲内（上の定数。表は 233 個）。
    unsafe {
        let table = *(env as *const *const *const c_void);
        *table.add(index)
    }
}

/// Java の `byte[]` の中身を写し取る（null・読み取りに失敗したら None。例外が起きていたら消して None）。
///
/// # Safety
/// `env` は JNI がこのスレッドへ渡した有効な JNIEnv*、`array` は同じ呼び出しで渡された `byte[]` の参照（null 可）であること。
pub unsafe fn read_byte_array(env: *mut c_void, array: *mut c_void) -> Option<Vec<u8>> {
    if env.is_null() || array.is_null() {
        return None;
    }
    // SAFETY: 関数表の位置は上の定数（jni.h で確かめた並び）。型は jni.h の宣言と同じ形。
    unsafe {
        let get_length: GetArrayLengthFn = std::mem::transmute(function(env, GET_ARRAY_LENGTH_INDEX));
        let length = get_length(env, array);
        let mut bytes = vec![0u8; usize::try_from(length).ok()?];
        if length > 0 {
            let get_region: GetByteArrayRegionFn = std::mem::transmute(function(env, GET_BYTE_ARRAY_REGION_INDEX));
            get_region(env, array, ARRAY_START, length, bytes.as_mut_ptr().cast::<i8>());
        }
        // 範囲の誤り等で Java の例外が起きていたら、Java 側（onCreate）へ持ち越さないよう消す
        let exception_check: ExceptionCheckFn = std::mem::transmute(function(env, EXCEPTION_CHECK_INDEX));
        if exception_check(env) != JNI_FALSE {
            let exception_clear: ExceptionClearFn = std::mem::transmute(function(env, EXCEPTION_CLEAR_INDEX));
            exception_clear(env);
            return None;
        }
        Some(bytes)
    }
}
