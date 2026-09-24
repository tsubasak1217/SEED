// ============================================================
//  DotnetJniLibraries.java — 同梱 .NET の JNI 初期化が要るネイティブライブラリを Java から読み込む（段階B）
//
//  【なぜ Java から読み込むのか】
//  CoreCLR の暗号ライブラリ（libSystem.Security.Cryptography.Native.Android.so。SHA256・RandomNumberGenerator・
//  TLS 等が使う）は、JNI_OnLoad で JavaVM を受け取り、自分用の Java クラス（net.dot.android.crypto.* ＝ パックの
//  .jar。SeedAndroid が APK の Java クラスへ入れる）を FindClass で探す。見つからなければ abort() する。
//  ネイティブのスレッドから JNI_OnLoad を呼ぶと、FindClass はシステムのクラスローダーで探すため APK のクラスが
//  見えず abort() になる。System.loadLibrary で読み込めば、JNI_OnLoad はアプリのクラスローダーの文脈で呼ばれる。
//
//  【CLR と同じ実体を使わせる】
//  CLR は dotnet-root の shared/Microsoft.NETCore.App/<版>/ から同じ .so を dlopen する。そこがシンボリックリンク
//  （dotnet_runtime.json の native_library_mode = "symlink"）なら実体は nativeLibraryDir の同じファイルで、bionic は
//  既に読み込み済み（＝ここで JNI_OnLoad 済み）の実体を返す。複製（copy）だと別の実体になり初期化されないので、
//  暗号 API は使えない（docs/android.md §17）。
//
//  【読み込むかどうか】
//  .jar のクラスが APK にあるとき（＝ CoreCLR の同梱 .NET を入れた APK）だけ読み込む。Mono の APK や .NET 無しの
//  APK ではクラスが無いので何もしない（読み込むと JNI_OnLoad が abort() するため）。
// ============================================================

package com.seedengine.runtime;

import android.util.Log;

/** 同梱 .NET の、JNI の初期化が要るネイティブライブラリの読み込み係。 */
final class DotnetJniLibraries {

    /** logcat のタグ（ネイティブ側と同じ）。 */
    private static final String LOG_TAG = "SEED";

    /** JNI の初期化が要るネイティブライブラリ 1 つ（名前と、その JNI_OnLoad が必ず探す Java クラス）。 */
    private static final class Library {
        /** System.loadLibrary に渡す名前（lib と .so を除く）。 */
        final String name;
        /** JNI_OnLoad が FindClass で探すクラス（APK にあるときだけ読み込む目印）。 */
        final String requiredClass;

        Library(String name, String requiredClass) {
            this.name = name;
            this.requiredClass = requiredClass;
        }
    }

    /** 読み込む対象（.NET 10 の Android 版 CoreCLR の暗号ライブラリ）。 */
    private static final Library[] LIBRARIES = {
        new Library("System.Security.Cryptography.Native.Android", "net.dot.android.crypto.DotnetProxyTrustManager"),
    };

    private DotnetJniLibraries() {
    }

    /**
     * 目印のクラスが APK にあるライブラリだけを読み込む（JNI_OnLoad がアプリのクラスローダーの文脈で走る）。
     * 失敗してもアプリは続ける（そのときスクリプトの暗号 API は使えない）。
     */
    static void loadAvailable() {
        for (Library library : LIBRARIES) {
            try {
                Class.forName(library.requiredClass);
            } catch (ClassNotFoundException e) {
                // CoreCLR の .jar を入れていない APK（Mono・.NET 無し）。読み込むと abort() するので読まない。
                Log.i(LOG_TAG, "[SEED DOTNET] Java: " + library.name + " は読み込みません（" + library.requiredClass + " が APK にありません）");
                continue;
            }
            try {
                System.loadLibrary(library.name);
                Log.i(LOG_TAG, "[SEED DOTNET] Java: " + library.name + " を読み込みました（JNI_OnLoad 済み）");
            } catch (UnsatisfiedLinkError e) {
                Log.w(LOG_TAG, "[SEED DOTNET] Java: " + library.name + " を読み込めませんでした（スクリプトの暗号 API は使えません）: " + e);
            }
        }
    }
}
