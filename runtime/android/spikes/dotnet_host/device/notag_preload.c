/*
 * notag_preload.c - 実機検証用の LD_PRELOAD シム（スパイク本体 spike_host は変更しない）。
 *
 * arm64 の Android 11 以降、bionic はネイティブ実行ファイルのヒープポインタの上位バイトに
 * 固定タグ 0xB4 を付ける（TBI: Top-Byte-Ignore）。MTE の無い端末では環境変数でも無効化できない。
 * CoreCLR / Mono がこのタグ付きポインタで SEGV_MAPERR を起こすかを確かめるため、
 * プロセス開始直後（main より前）にタグ付けを NONE へ下げる。
 *
 * アプリ（APK）では AndroidManifest の android:allowNativeHeapPointerTagging="false" が同じ効果を持つ。
 * 本番ホストで行う場合は、hostfxr を読み込む前に同じ mallopt を 1 回呼べばよい。
 */
#include <android/log.h>
#include <malloc.h>

/* logcat のタグ（spike_host の C# 側と揃える） */
#define NOTAG_LOG_TAG "SEEDSpike"

/* main より前に 1 回だけ走る。以後の malloc はタグ無しポインタを返す（既存のタグ付きポインタは free 時に外される） */
__attribute__((constructor)) static void disable_heap_pointer_tagging(void)
{
    int result = mallopt(M_BIONIC_SET_HEAP_TAGGING_LEVEL, M_HEAP_TAGGING_LEVEL_NONE);
    __android_log_print(ANDROID_LOG_INFO, NOTAG_LOG_TAG,
                        "notag_preload: mallopt(M_BIONIC_SET_HEAP_TAGGING_LEVEL, NONE) = %d", result);
}
