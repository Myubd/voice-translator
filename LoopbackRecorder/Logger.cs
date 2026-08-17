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

    // ログは1日単位でファイルが分かれるため急激な肥大化はしにくいが、放置すると際限なく増え続けるため、
    // 起動のたびにこの日数より古いログファイルを削除する(古い順に消えるだけで、直近の調査には影響しない)
    private const int LogRetentionDays = 30;
    private static bool _oldLogsCleaned = false;

    /// <summary>
    /// 保持期間(LogRetentionDays)より古いログファイルを削除する。
    /// アプリ起動後、最初のログ書き込み時に一度だけ実行する(毎回のログ出力のたびに
    /// ディレクトリを走査するのは無駄なため)。ここでの失敗もアプリ本体には影響させない。
    /// </summary>
    private static void CleanupOldLogsIfNeeded()
    {
        if (_oldLogsCleaned) return;
        _oldLogsCleaned = true;

        try
        {
            if (!Directory.Exists(LogDirectory)) return;

            var cutoff = DateTime.Now.AddDays(-LogRetentionDays);
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 古いログの削除に失敗しても致命的ではないため無視する
        }
    }

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
            CleanupOldLogsIfNeeded();

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
    ///
    /// LogRetentionDaysによる日単位の削除は「翌日以降」にしか効かないため、1日単位のファイルが
    /// 想定外に長時間(例: 配信中ずっと起動しっぱなし等)書き込まれ続けると、その日のファイル自体が
    /// 際限なく肥大化しうる。そのため、1ファイルあたりのサイズにも上限を設け、超えた場合は
    /// (ディスクを圧迫し続けないよう)その日はそれ以上書き込まないようにする。
    /// </summary>
    /// <param name="component">計測対象(例: "Latency", "Queue")</param>
    /// <param name="fields">key=value形式で記録したい値の一覧</param>
    // 1日分のmetricsログファイルの上限サイズ。通常の利用時間であればまず到達しないが、
    // 長時間起動しっぱなしでの際限のない肥大化を防ぐための安全弁。
    private const long MaxMetricsFileSizeBytes = 50L * 1024 * 1024; // 50MB
    private static string? _metricsSizeLimitWarnedForDate = null;

    public static void LogMetric(string component, params (string Key, object Value)[] fields)
    {
        try
        {
            CleanupOldLogsIfNeeded();

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append('\t').Append(component);
            foreach (var (key, value) in fields)
            {
                sb.Append('\t').Append(key).Append('=').Append(value);
            }

            var line = sb.ToString();
            var dateStamp = DateTime.Now.ToString("yyyyMMdd");
            var filePath = Path.Combine(LogDirectory, $"metrics-{dateStamp}.log");

            lock (WriteLock)
            {
                Directory.CreateDirectory(LogDirectory);

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Exists && fileInfo.Length > MaxMetricsFileSizeBytes)
                {
                    // 上限到達を知らせる行は、同じ日に何度も書き込んで無駄に消費しないよう1回だけ出す
                    if (_metricsSizeLimitWarnedForDate != dateStamp)
                    {
                        _metricsSizeLimitWarnedForDate = dateStamp;
                        var warnLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\tLogger\t-\t" +
                            $"本日分の計測ログが上限({MaxMetricsFileSizeBytes / (1024 * 1024)}MB)に達したため、以降の出力を停止します。";
                        File.AppendAllText(filePath, warnLine + Environment.NewLine, Encoding.UTF8);
                    }
                    return;
                }

                File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
