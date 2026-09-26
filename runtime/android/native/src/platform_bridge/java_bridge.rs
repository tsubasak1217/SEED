// ============================================================
//  platform_bridge/java_bridge.rs — native → Java の呼び出し（SeedPlatform.invoke）の持ち物（W1-1。jni クレート 0.22）
//
//  【持つもの】（Java の SeedPlatform.nativeRegisterPlatformBridge(Class) が起動時に 1 回渡す）
//    ・JavaVM                  … 呼ぶスレッドを attach するため
//    ・SeedPlatform の GlobalRef … ネイティブのスレッドから FindClass するとシステムのクラスローダーになり APK のクラスが
//                                 見えない（docs/android.md §17.8）ので、Java から渡されたクラスを持ち続ける
//    ・invoke の static メソッド ID … 呼ぶたびに引かない（クラスを GlobalRef で持つ間は有効）
//
//  【呼び方】invoke は呼び出し元のスレッドを attach_current_thread で JVM につなぎ（android_main のスレッドは
//  GameActivity が既につないでいるので安い）、CallStaticObjectMethod で SeedPlatform.invoke(String, String, byte[]) を呼ぶ。
//  jni 0.22 の attach_current_thread は、呼ぶたびに JNI のローカルフレームを積んで閉じる（長く生きるスレッドで
//  ローカル参照が溜まらない）うえ、Java の例外を受け止めて消し Err にする（ネイティブ側へ例外を持ち越さない）。
//  Java の invoke は例外を投げない約束（{"ok":false,…} を返す）なので、Err は JNI そのものの失敗だけ。
// ============================================================

use std::sync::OnceLock;

use jni::errors::Error as JniError;
use jni::objects::{Global, JByteArray, JClass, JStaticMethodID, JValue};
use jni::signature::ReturnType;
use jni::vm::JavaVM;
use jni::{jni_sig, jni_str, Env};

/// Java の SeedPlatform の登録がまだ無い（古い APK・init の前）ときの理由の名前。
pub const ERROR_NOT_REGISTERED: &str = "java_bridge_not_registered";

/// Java の invoke が null を返したときの理由の名前（約束では起きない）。
const ERROR_NULL_REPLY: &str = "null_reply";

/// Java の返答が UTF-8 でないときの理由の名前（約束では起きない）。
const ERROR_REPLY_NOT_UTF8: &str = "reply_not_utf8";

/// JNI の呼び出しそのものが失敗したときの理由の接頭辞（後ろに jni クレートのエラーの説明が付く）。
const ERROR_JNI_PREFIX: &str = "jni_error";

/// 登録の結果。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RegisterOutcome {
    /// 初めて登録した。
    Registered,
    /// 既に登録があった（最初の登録のまま。同じプロセスで Activity が作り直されることは無い〈onDestroy でプロセスを終える〉ので、
    /// 起きるのは同じクラスの二重の init だけ）。
    AlreadyRegistered,
}

/// Java の SeedPlatform を呼ぶための持ち物（プロセスで 1 つ）。
struct JavaBridge {
    /// 呼ぶスレッドを attach する VM。
    vm: JavaVM,
    /// SeedPlatform のクラス（GlobalRef。プロセスの間ずっと持つ）。
    class: Global<JClass<'static>>,
    /// SeedPlatform.invoke(String, String, byte[]) → byte[] のメソッド ID。
    invoke_method: JStaticMethodID,
}

/// プロセスで 1 つの持ち物（nativeRegisterPlatformBridge が入れる）。
static JAVA_BRIDGE: OnceLock<JavaBridge> = OnceLock::new();

/// Java から渡された SeedPlatform のクラスを持つ（nativeRegisterPlatformBridge の中身）。
///
/// # 引数
/// * `env`   - JNI の呼び出しのスレッドの Env
/// * `class` - SeedPlatform のクラス（Java が SeedPlatform.class を渡す）
///
/// # 戻り値
/// 登録の結果。invoke のメソッドが見つからない等の JNI の失敗は Err（呼び出し側が例外を消してログに残す）
pub fn register(env: &mut Env, class: &JClass) -> Result<RegisterOutcome, JniError> {
    if JAVA_BRIDGE.get().is_some() {
        return Ok(RegisterOutcome::AlreadyRegistered);
    }
    let vm = env.get_java_vm()?;
    // 署名は Java の static byte[] invoke(String module, String method, byte[] jsonUtf8) と一致させる（コンパイル時に検査される）
    let invoke_method = env.get_static_method_id(
        class,
        jni_str!("invoke"),
        jni_sig!("(Ljava/lang/String;Ljava/lang/String;[B)[B"),
    )?;
    let class = env.new_global_ref(class)?;
    // 同時に 2 回呼ばれて負けた側の持ち物は捨てる（GlobalRef は今のスレッド〈attach 済み〉で消える）
    Ok(if JAVA_BRIDGE.set(JavaBridge { vm, class, invoke_method }).is_ok() {
        RegisterOutcome::Registered
    } else {
        RegisterOutcome::AlreadyRegistered
    })
}

/// Java の登録が済んでいるか。
pub fn is_registered() -> bool {
    JAVA_BRIDGE.get().is_some()
}

/// SeedPlatform.invoke を同期で呼び、返答の JSON を返す。
///
/// # 戻り値
/// 届いたら Ok(返答の JSON。失敗も {"ok":false,…} の形で入る)。
/// 登録が無い・JNI の失敗は Err(理由)
pub fn invoke(module: &str, method: &str, json: &str) -> Result<String, String> {
    let Some(bridge) = JAVA_BRIDGE.get() else {
        return Err(ERROR_NOT_REGISTERED.to_string());
    };
    let outcome: Result<Result<String, String>, JniError> = bridge.vm.attach_current_thread(|env| {
        let module = env.new_string(module)?;
        let method = env.new_string(method)?;
        let payload = env.byte_array_from_slice(json.as_bytes())?;
        let args = [
            JValue::from(&module).as_jni(),
            JValue::from(&method).as_jni(),
            JValue::from(&payload).as_jni(),
        ];
        // SAFETY: invoke_method は register で、このクラスの static メソッドを同じ署名
        // (String, String, byte[]) → byte[] で引いたもの。引数の数・型と戻り値の型（配列）は署名どおり。
        let reply = unsafe { env.call_static_method_unchecked(&bridge.class, bridge.invoke_method, ReturnType::Array, &args)? };
        let reply = reply.l()?;
        if reply.is_null() {
            return Ok(Err(ERROR_NULL_REPLY.to_string()));
        }
        let reply = env.cast_local::<JByteArray>(reply)?;
        let bytes = env.convert_byte_array(&reply)?;
        Ok(String::from_utf8(bytes).map_err(|_| ERROR_REPLY_NOT_UTF8.to_string()))
    });
    match outcome {
        Ok(result) => result,
        Err(err) => Err(format!("{ERROR_JNI_PREFIX}: {err}")),
    }
}
