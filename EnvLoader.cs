using System;
using System.IO;

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

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            int separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0) continue;

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim().Trim('"');

            // OSの環境変数(setxで設定したもの等)がある場合はそちらを優先する
            if (Environment.GetEnvironmentVariable(key) == null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
