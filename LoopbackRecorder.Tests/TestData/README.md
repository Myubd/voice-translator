# TestData フォルダ

`PipelineEndToEndTests.cs` が使う、実音声を使ったE2Eテスト用の音声ファイルを置く場所です。

## 前提条件

- WAVファイルを1つ以上、このフォルダに置いてください(ファイル名は自由)。
- サンプルレート・チャンネル数は自由です(本体アプリと同様、テスト側で自動的に16kHzモノラルへ
  変換してから検証します)。ただし3ch以上(5.1ch等)には対応していません。
- `.wav` は `.gitignore` で除外済みなので、ここに置いたファイルは誤ってコミットされません。

## 用意の仕方

### 方法A: PowerShellで音声合成(TTS)を使う(推奨・お手軽)

自分の声を録音する必要がなく、誰でも同じ手順で再現できます。

```powershell
Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SetOutputToWaveFile("C:\path\to\LoopbackRecorder.Tests\TestData\sample1.wav")
$synth.Speak("こんにちは、今日はいい天気ですね。")
Start-Sleep -Milliseconds 1500
$synth.Speak("それではゲームを始めましょう。")
$synth.Dispose()
```

### 方法B: 自分の声を録音する

Windows標準の「サウンドレコーダー」アプリ、または [Audacity](https://www.audacityteam.org/)(無料)で、
2〜4秒の発話を1秒以上の無音を挟んで3〜5個ほど収録してください。書き出し時のサンプルレート・
チャンネル数を気にする必要はありません(そのままエクスポートしてOK)。

## Whisperモデルについて

このテストは、`LoopbackRecorder/`(本体プロジェクトフォルダ)にある `ggml-*.bin` を自動的に探して使います。
普段アプリを動かすために既に配置しているモデルがあれば、追加の準備は不要です。
特定のモデルを指定したい場合は、環境変数 `E2E_WHISPER_MODEL_PATH` に絶対パスを設定してください。

Silero VADモデル(`silero_vad.onnx`)は小さいため本体プロジェクトフォルダに同梱済みで、準備不要です。

## 実行方法

```
cd LoopbackRecorder.Tests
dotnet test --filter "実音声ファイルからVADと文字起こしの結線が動作する"
```

音声ファイルまたはWhisperモデルが見つからない場合、このテストは何も検証せずに(常に成功扱いで)
終了します。コンソール出力に `[SKIP]` と表示されていないか確認してください。
