using System;
using System.IO;
using System.Text;

/// <summary>
/// .envファイルを読み込み、環境変数として設定する簡易ローダー。
/// 既にOS側で環境変数が設定済みの場合はそちらを優先し、上書きしない。
/// </summary>
static class EnvLoader
{
    public static void Load(string path = ".env")
    {
        if (!File.Exists(path))
        {
            return;
        }

        // 日本語(WHISPER_PROMPT・参考コンテキスト等)を含む可能性があるため、
        // OS/エディタの既定エンコーディングに左右されないようUTF-8を明示指定する
        foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            int separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0) continue;

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            // 値の前後を無条件にTrim('"')すると、値自体が引用符で始まる/終わるだけの
            // ケース(例: 値の一部としての")でも意図せず消えてしまう。
            // ここでは「先頭と末尾が同じ引用符で、かつ2文字以上ある」場合のみ、
            // 囲み記号とみなして1組だけ取り除く。
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            // 保存側(AppSettings.SaveToEnv)で改行を\nとしてエスケープしている値は、
            // 読み込み時に元の改行へ戻す
            value = value.Replace("\\n", "\n");

            // OSの環境変数(setxで設定したもの等)がある場合はそちらを優先する
            if (Environment.GetEnvironmentVariable(key) == null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
