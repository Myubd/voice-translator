# Voice Translator

ゲーム内で外国人が話す音声(Discordのボイスチャットやゲーム音声)をリアルタイムで文字起こし・翻訳するWindows用デスクトップアプリです。

音声を検出 → Whisperで文字起こし → 翻訳(DeepL API またはローカルAI/Ollama)→ 画面上に表示、という流れをリアルタイムで行います。

## 主な機能

- WASAPIループバックキャプチャによる音声取得(VB-CABLEやSteelSeries Sonar等の仮想オーディオデバイスに対応)
- RMSベースの簡易VAD(発話区間検出)
- [Whisper.net](https://github.com/sandrohanea/whisper.net)によるオフライン文字起こし(自動言語検出)
- 翻訳バックエンドを切り替え可能
  - [DeepL API](https://www.deepl.com/pro-api)(クラウド、高精度)
  - [Ollama](https://ollama.com)(ローカルAI、APIキー不要・完全オフライン)
- 原文/訳文を分けて表示するメイン画面
- ゲーム画面に重ねて表示できる、ドラッグ移動・リサイズ可能な半透明オーバーレイ(訳文のみ表示)
- `.env`ファイルによる設定管理

## 動作環境

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download)

## セットアップ

### 1. リポジトリを取得

```powershell
git clone https://github.com/Myubd/voice-translator.git
cd voice-translator
```

### 2. Whisperモデルをダウンロード

[huggingface.co/ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp/tree/main)から `ggml-base.bin` をダウンロードし、プロジェクトのルートフォルダ(`.csproj`と同じ階層)に配置してください。

より高精度な認識が必要な場合は `ggml-small.bin` 等も利用できます(設定画面から切り替え可能)。

### 3. `.env`ファイルを作成

`.env.example` を `.env` にコピーし、内容を編集します。

```powershell
copy .env.example .env
```

```env
# 翻訳バックエンド: "deepl" または "ollama"
TRANSLATION_BACKEND=deepl

# DeepLを使う場合のAPIキー(無料プランは末尾に :fx が付く)
DEEPL_API_KEY=

# Ollama(ローカルAI)を使う場合に使用するモデル名
OLLAMA_MODEL=llama3.1

# Ollamaのエンドポイント(通常は変更不要)
OLLAMA_ENDPOINT=http://localhost:11434
```

DeepLを使う場合は[deepl.com/pro-api](https://www.deepl.com/pro-api)で無料APIキーを取得してください。

Ollama(ローカルAI)を使う場合は、[ollama.com](https://ollama.com)をインストールし、任意のモデルを取得しておいてください。

```powershell
ollama pull llama3.1
```

### 4. ビルド・実行

```powershell
dotnet restore
dotnet run
```

## 使い方

1. アプリを起動し、「設定」から音声デバイス・翻訳バックエンド・VAD感度・Whisperモデルを設定して保存
2. 「開始」ボタンで認識を開始
3. 左ペインに原文、右ペインに訳文がリアルタイムで表示される
4. 「オーバーレイ表示」で、ゲーム画面に重ねられる半透明の訳文ウィンドウを表示/非表示できる(ドラッグで移動、端をつまんでリサイズ可能)
5. 「クリア」で表示内容をリセット

## 音声デバイスについて

ゲーム音声やDiscordのボイスチャットをキャプチャするには、仮想オーディオデバイス(例: [VB-CABLE](https://vb-audio.com/Cable/))を経由させる必要があります。SteelSeries Sonar等のオーディオミキサーソフトを使っている場合、チャンネルごとの出力先をVB-CABLEに向けることで、特定の音声(チャットのみ等)を分離してキャプチャできます。

設定画面の「音声デバイス」では、デバイス名に含まれるキーワード(例: `CABLE`)で対象デバイスを指定します。

## 技術スタック

- C# / .NET 8 / WPF
- [NAudio](https://github.com/naudio/NAudio) — 音声キャプチャ・リサンプリング
- [Whisper.net](https://github.com/sandrohanea/whisper.net) — 音声認識(whisper.cppのC#バインディング)
- [DeepL API](https://www.deepl.com/pro-api) / [Ollama](https://ollama.com) — 翻訳

## 既知の制限

- ノイズが多い・早口な音声(ゲーム実況等)では、Whisperが稀に無関係な言語のテキストを生成すること(ハルシネーション)がある
- 一部の仮想オーディオデバイス(SteelSeries Sonarの仮想チャンネル等)はWASAPIループバックキャプチャと相性が悪く、無音として扱われる場合がある(VB-CABLE経由での利用を推奨)
- デバイスが他プロセスと競合し、起動時にエラーになることがある(自動リトライあり)
