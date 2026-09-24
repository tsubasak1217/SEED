// ============================================================
//  clr_host/heap_tagging.rs — ヒープポインタのタグ付けを止める（Android の CLR 起動前）
//
//  【なぜ要るのか】（docs/android.md §11.4）
//  arm64 の Android 11 以降、bionic（scudo）は malloc が返すポインタの最上位バイトにタグ（0xB4）を付ける
//  （TBI: Top-Byte-Ignore。メモリアクセスはタグを無視するが、ポインタの値そのものが変わる）。
//  CoreCLR / Mono はこのタグ付きポインタで coreclr_initialize 中に SIGSEGV（SEGV_MAPERR・0xb4000074…）になる。
//  x86_64 のエミュレータと、その ARM 変換では起きない。実機 Pixel 6a（Android 16）で確認した。
//
//  【対策は 2 つ入れる】
//    1. AndroidManifest.xml の <application android:allowNativeHeapPointerTagging="false">
//       … プロセスの開始時点からタグ付けされない（本命。API 30 以降）
//    2. ここ: hostfxr を読み込む直前の mallopt(M_BIONIC_SET_HEAP_TAGGING_LEVEL, M_HEAP_TAGGING_LEVEL_NONE)
//       … マニフェストの属性が無い APK でも（あるいは属性が効かない端末でも）CLR が起動できるようにする保険。
//          以後の malloc はタグ無しになる。一度無効にすると同じプロセスでは元に戻せない（bionic の仕様）。
//  bionic の malloc.h: 「いつでも・複数スレッドが動いていても呼んでよい」「API 31 以降」。
//  API 30 以前・未対応の構成では 0（失敗）が返るだけで害は無い（そのときはマニフェストの属性が効く）。
// ============================================================

use std::ffi::c_int;

/// mallopt のオプション: ヒープのタグ付けの段階を変える（bionic の malloc.h の M_BIONIC_SET_HEAP_TAGGING_LEVEL）。
const M_BIONIC_SET_HEAP_TAGGING_LEVEL: c_int = -204;

/// タグ付けの段階: 無し（bionic の malloc.h の M_HEAP_TAGGING_LEVEL_NONE）。
const M_HEAP_TAGGING_LEVEL_NONE: c_int = 0;

/// mallopt の成功を表す戻り値（bionic は成功で 1、失敗・未対応で 0 を返す）。
const MALLOPT_SUCCESS: c_int = 1;

unsafe extern "C" {
    /// bionic の mallopt（`int mallopt(int option, int value)`）。libc に常にある（API 26 以降）。
    fn mallopt(option: c_int, value: c_int) -> c_int;
}

/// CLR を起動する前にヒープポインタのタグ付けを止め、結果をログに残す。
pub(super) fn disable_before_clr() {
    // SAFETY: mallopt は引数の値だけを読む C 関数で、bionic の資料上いつでも・どのスレッドからでも呼んでよい。
    let result = unsafe { mallopt(M_BIONIC_SET_HEAP_TAGGING_LEVEL, M_HEAP_TAGGING_LEVEL_NONE) };
    if result == MALLOPT_SUCCESS {
        eprintln!("[SEED DOTNET] ヒープポインタのタグ付けを無効にしました（mallopt=1。マニフェストの allowNativeHeapPointerTagging=false と二重の対策）");
    } else {
        eprintln!(
            "[SEED DOTNET] mallopt(M_BIONIC_SET_HEAP_TAGGING_LEVEL) は {result} を返しました（API 30 以前・未対応の構成。\
             マニフェストの allowNativeHeapPointerTagging=false が効いていれば問題ありません）"
        );
    }
}
