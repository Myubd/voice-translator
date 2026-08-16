using System;
using System.IO;
using System.Text;

/// <summary>
/// 診断用のシンプルなファイルロガー。
/// これまでは「catch { }」で例外を握りつぶしている箇所が多く、
/// リアルタイム処理を止めないという目的は理解できるものの、
/// ログ機能自体が無いため実際に何が起きたのか後から追跡できなかった。
/// このクラスは、その握りつぶす直前に最低限の診断情報(いつ/どこで/何が)を
/// ローカルファイルへ記録するためのもの。
///
/// 例外を投げない・呼び出し元をブロックしないことを優先している
/// (ログ出力自体が失敗してもアプリ本体には影響させない)。
/// </summary>
public static class Logger
{
    private static readonly object WriteLock = new object();

    // exeの場所を基準にlogsフォルダへ出力する(AudioPipelineのモデルパス解決と同じ考え方)
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>
    /// 診断ログを1行追記する。
    /// </summary>
    /// <param name="component">発生箇所(例: "AudioPipeline", "DeepL", "SettingsWindow")</param>
    /// <param name="message">人間が読める短い説明</param>
    /// <param name="ex">関連する例外があれば渡す(型・メッセージ・スタックトレースを記録する)</param>
    public static void Log(string component, string message, Exception? ex = null)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append('\t').Append(component);
            sb.Append('\t').Append(ex?.GetType().FullName ?? "-");
            sb.Append('\t').Append(message);

            if (ex != null)
            {
                sb.Append(Environment.NewLine).Append(ex);
            }

            var line = sb.ToString();
            var filePath = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");

            // File.AppendAllTextはスレッドセーフではないため、複数ワーカー(Whisper/翻訳/UI)から
            // 同時に呼ばれても壊れないようロックする。呼び出し頻度は例外発生時のみなので
            // 音声処理のリアルタイム性への影響は無視できる。
            lock (WriteLock)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // ロギング自体が失敗しても、それが原因でアプリの動作に影響してはいけないため無視する
        }
    }

    /// <summary>
    /// 処理時間・キュー長・drop数などの計測値を構造化して記録する(診断・チューニング用)。
    /// Log()は「何か問題が起きた時」に例外とともに記録する想定だが、こちらは正常系でも
    /// 継続的に出力し、後から「どこで遅延が発生しているか」を分析できるようにする
    /// (Whisper処理時間・翻訳処理時間・キュー長・drop数・累積遅延などをkey=value形式で残す)。
    /// 呼び出し頻度が高くなりうるため、こちらもLog()同様に例外は握りつぶし、アプリ本体には影響させない。
    /// </summary>
    /// <param name="component">計測対象(例: "Latency", "Queue")</param>
    /// <param name="fields">key=value形式で記録したい値の一覧</param>
    public static void LogMetric(string component, params (string Key, object Value)[] fields)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append('\t').Append(component);
            foreach (var (key, value) in fields)
            {
                sb.Append('\t').Append(key).Append('=').Append(value);
            }

            var line = sb.ToString();
            var filePath = Path.Combine(LogDirectory, $"metrics-{DateTime.Now:yyyyMMdd}.log");

            lock (WriteLock)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
