<#
.SYNOPSIS
    VAD比較テスト(VadWindowingComparisonTests)・E2Eテスト(PipelineEndToEndTests)用の
    テスト音声を、複数の条件(通常/小声/早口/ノイズ/BGM混入/英語)で生成する。

.DESCRIPTION
    レビュー指摘: VAD窓方式の実測比較(docs/vad-windowing-comparison.md)が
    「日本語TTS・明瞭な発話・1ファイルのみ」で行われており、ゲーム実況という用途を考えると
    小声・早口・BGM混入・ノイズ環境での検証が不足している、という点への対応。

    Windows標準のSystem.Speech(TTS)のみを使い、追加の音声素材やライセンスに一切依存しない。
    生成される音声はすべて合成音声(+ このスクリプトが生成する合成ノイズ/合成トーン)であり、
    個人の発話や著作物を含まないため、そのままリポジトリにコミットしてよい
    (.gitignoreの `!LoopbackRecorder.Tests/TestData/*.wav` 例外により追跡対象)。

.NOTES
    実行環境: Windows PowerShell 5.1以降(System.Speechが利用可能なこと)。
    このスクリプト自体はテストのビルド・実行には含まれない、開発者がローカルで
    手動実行するための補助スクリプト。

.EXAMPLE
    cd LoopbackRecorder.Tests\TestData
    .\generate-test-audio.ps1
#>

[CmdletBinding()]
param(
    # 生成先ディレクトリ(既定はこのスクリプト自身の場所 = TestData直下)
    [string]$OutputDir = $PSScriptRoot
)

Add-Type -AssemblyName System.Speech

# ============================================================================
# 共通: 16bit PCM/16kHz/モノラルのWAVファイルをその場で読み書きするための
# 最小限のヘルパー(NAudio等の追加パッケージに依存させないため自前実装)。
# ============================================================================
Add-Type -TypeDefinition @"
using System;
using System.IO;

public static class WavHelper
{
    // 16bit PCM モノラルWAVを読み込み、-1.0〜1.0のfloat配列とサンプルレートを返す。
    public static float[] ReadMonoPcm16(string path, out int sampleRate)
    {
        // Windows PowerShell 5.1のAdd-Type既定コンパイラはC# 8の"using宣言"
        // (using var x = ...;)に対応していないため、従来のusing(){}ブロックで書く。
        using (FileStream fs = File.OpenRead(path))
        using (BinaryReader br = new BinaryReader(fs))
        {
            // RIFFヘッダ
            br.ReadBytes(4); // "RIFF"
            br.ReadInt32();  // ChunkSize
            br.ReadBytes(4); // "WAVE"

            sampleRate = 16000;
            short channels = 1;
            short bitsPerSample = 16;
            byte[] data = null;

            while (fs.Position < fs.Length)
            {
                string chunkId = new string(br.ReadChars(4));
                int chunkSize = br.ReadInt32();
                if (chunkId == "fmt ")
                {
                    br.ReadInt16(); // AudioFormat
                    channels = br.ReadInt16();
                    sampleRate = br.ReadInt32();
                    br.ReadInt32(); // ByteRate
                    br.ReadInt16(); // BlockAlign
                    bitsPerSample = br.ReadInt16();
                    int remaining = chunkSize - 16;
                    if (remaining > 0) br.ReadBytes(remaining);
                }
                else if (chunkId == "data")
                {
                    data = br.ReadBytes(chunkSize);
                }
                else
                {
                    br.ReadBytes(chunkSize);
                }
            }

            if (data == null) throw new InvalidDataException("data chunkが見つかりません: " + path);
            if (bitsPerSample != 16) throw new NotSupportedException("16bit PCM以外は未対応です: " + path);

            int sampleCount = data.Length / 2 / channels;
            float[] samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                // channelsが2以上の場合は先頭チャンネルのみ採用(このスクリプトではモノラルのみ生成するため通常は不要)
                short s = BitConverter.ToInt16(data, i * 2 * channels);
                samples[i] = s / 32768f;
            }
            return samples;
        }
    }

    // -1.0〜1.0のfloat配列を16bit PCM/モノラルWAVとして書き出す。
    public static void WriteMonoPcm16(string path, float[] samples, int sampleRate)
    {
        using (FileStream fs = File.Create(path))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            int byteRate = sampleRate * 2;
            int dataSize = samples.Length * 2;

            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataSize);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);  // PCM
            bw.Write((short)1);  // channels = 1
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write((short)2);  // block align
            bw.Write((short)16); // bits per sample

            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(dataSize);

            for (int i = 0; i < samples.Length; i++)
            {
                float clamped = Math.Max(-1f, Math.Min(1f, samples[i]));
                bw.Write((short)(clamped * 32767f));
            }
        }
    }
}
"@

function New-TtsWav {
    <#
        System.Speechで音声を合成し、指定パスにWAVとして書き出す。
        $Rate: -10(遅い)〜10(速い)、$Volume: 0〜100。
    #>
    param(
        [string]$Path,
        [string[]]$Lines,
        [int]$Rate = 0,
        [int]$Volume = 100,
        [int]$PauseMs = 800
    )

    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $synth.Rate = $Rate
        $synth.Volume = $Volume
        $synth.SetOutputToWaveFile($Path)
        foreach ($line in $Lines) {
            $synth.Speak($line)
            Start-Sleep -Milliseconds $PauseMs
        }
    }
    finally {
        $synth.Dispose()
    }
}

function Add-SyntheticNoise {
    <#
        既存のWAVファイルに合成ノイズ(ホワイトノイズ or 低音の疑似BGM)を混ぜて上書き保存する。
        $NoiseGainは元音声に対するノイズの相対振幅(0.0〜1.0程度を想定)。
    #>
    param(
        [string]$Path,
        [ValidateSet("white", "hum")]
        [string]$NoiseType,
        [double]$NoiseGain
    )

    [int]$sampleRate = 0
    $samples = [WavHelper]::ReadMonoPcm16($Path, [ref]$sampleRate)

    $rng = New-Object System.Random(42) # 再現性のため固定シード
    $mixed = New-Object float[] $samples.Length

    if ($NoiseType -eq "white") {
        # ノイズの多いマイク環境を想定したホワイトノイズ
        for ($i = 0; $i -lt $samples.Length; $i++) {
            $noise = ($rng.NextDouble() * 2.0 - 1.0)
            $mixed[$i] = [float]($samples[$i] + $noise * $NoiseGain)
        }
    }
    else {
        # ゲームBGMを想定した、複数の低周波トーンを重ねた疑似メロディ
        $freqs = @(110.0, 146.83, 164.81) # A2, D3, E3 相当(ゲームBGM風の低音)
        for ($i = 0; $i -lt $samples.Length; $i++) {
            $t = $i / [double]$sampleRate
            $tone = 0.0
            foreach ($f in $freqs) {
                $tone += [Math]::Sin(2.0 * [Math]::PI * $f * $t)
            }
            $tone = $tone / $freqs.Length
            $mixed[$i] = [float]($samples[$i] + $tone * $NoiseGain)
        }
    }

    [WavHelper]::WriteMonoPcm16($Path, $mixed, $sampleRate)
}

# ============================================================================
# 生成する条件一覧
# ============================================================================
$lines = @(
    "こんにちは、今日はいい天気ですね。",
    "それではゲームを始めましょう。"
)

Write-Host "出力先: $OutputDir"

# 1. 通常(既存sample1.wavと同条件。既にある場合は上書きしない)
$normalPath = Join-Path $OutputDir "sample1.wav"
if (-not (Test-Path $normalPath)) {
    Write-Host "[1/6] 通常: sample1.wav"
    New-TtsWav -Path $normalPath -Lines $lines -Rate 0 -Volume 100
}
else {
    Write-Host "[1/6] 通常: sample1.wav は既に存在するためスキップ"
}

# 2. 小声(Volumeを下げる。ゲーム実況でマイクから距離がある場合を想定)
Write-Host "[2/6] 小声: sample2_quiet.wav"
$quietPath = Join-Path $OutputDir "sample2_quiet.wav"
New-TtsWav -Path $quietPath -Lines $lines -Rate 0 -Volume 30

# 3. 早口(Rateを上げる)
Write-Host "[3/6] 早口: sample3_fast.wav"
$fastPath = Join-Path $OutputDir "sample3_fast.wav"
New-TtsWav -Path $fastPath -Lines $lines -Rate 6 -Volume 100

# 4. ノイズあり(通常速度・音量のTTSにホワイトノイズを合成)
Write-Host "[4/6] ノイズあり: sample4_noisy.wav"
$noisyPath = Join-Path $OutputDir "sample4_noisy.wav"
New-TtsWav -Path $noisyPath -Lines $lines -Rate 0 -Volume 100
Add-SyntheticNoise -Path $noisyPath -NoiseType "white" -NoiseGain 0.05

# 5. ゲームBGM混入(通常速度・音量のTTSに疑似BGMを合成)
Write-Host "[5/6] BGM混入: sample5_bgm.wav"
$bgmPath = Join-Path $OutputDir "sample5_bgm.wav"
New-TtsWav -Path $bgmPath -Lines $lines -Rate 0 -Volume 100
Add-SyntheticNoise -Path $bgmPath -NoiseType "hum" -NoiseGain 0.08

# 6. 英語(英語音声が利用可能な場合のみ生成。無ければスキップして警告)
Write-Host "[6/6] 英語: sample6_english.wav"
$englishPath = Join-Path $OutputDir "sample6_english.wav"
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$hasEnglishVoice = $synth.GetInstalledVoices() | Where-Object {
    $_.VoiceInfo.Culture.TwoLetterISOLanguageName -eq "en"
} | Select-Object -First 1
$synth.Dispose()

if ($hasEnglishVoice) {
    $englishLines = @(
        "Hello, it's a nice day today.",
        "Let's start the game."
    )
    $synth2 = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $synth2.SelectVoice($hasEnglishVoice.VoiceInfo.Name)
        $synth2.SetOutputToWaveFile($englishPath)
        foreach ($line in $englishLines) {
            $synth2.Speak($line)
            Start-Sleep -Milliseconds 800
        }
    }
    finally {
        $synth2.Dispose()
    }
}
else {
    Write-Warning "英語音声(en-*)がこのWindows環境にインストールされていないため sample6_english.wav の生成をスキップしました。設定アプリの「時刻と言語」→「音声認識」から英語の音声を追加すると生成できます。"
}

Write-Host ""
Write-Host "生成完了。以下のコマンドで実行できます:"
Write-Host "  cd LoopbackRecorder.Tests"
Write-Host "  dotnet test --filter `"アプリ既定方式と公式ウィンドウ方式`""
Write-Host ""
Write-Host "生成された音声は合成音声(+合成ノイズ)のみで個人情報を含まないため、"
Write-Host "そのままコミットして構いません(.gitignoreの例外設定により追跡対象になります)。"
