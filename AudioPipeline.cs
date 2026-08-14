using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

/// <summary>
/// 音声キャプチャ→VAD→文字起こし→翻訳、という一連の処理を担当するクラス。
/// UI(WPF)には依存せず、結果はイベントで通知する。
/// </summary>
public class AudioPipeline
{
    // ==== VAD(発話区間検出)まわりのパラメータ ====
    const int SampleRate = 16000;
    const int ChunkSamples = 480;          // 16kHzで30ms分
    const int SilenceChunksToEndSpeech = 17; // 約500ms無音が続いたら発話終了とみなす
    const int MinSpeechChunks = 25;        // 約750ms未満の短い音は雑音として破棄(短すぎると言語判定を誤りやすいため長めに)
    const int MaxSpeechChunks = 500;       // 約15秒で強制的に区切る

    public float EnergyThreshold { get; set; } = 0.015f;

    public event Action<string>? OriginalTextReceived;
    public event Action<string>? TranslatedTextReceived;
    public event Action<string>? StatusChanged;

    private readonly HttpClient _httpClient = new HttpClient();
    private WhisperProcessor? _processor;
    private ITranslationService? _translationService;

    private readonly object _dedupLock = new object();
    private string? _lastGlobalText;
    private DateTime _lastGlobalTime = DateTime.MinValue;

    /// <summary>利用可能な出力(ループバック対象)デバイス名の一覧を取得する</summary>
    public static List<string> GetAvailableDeviceNames()
    {
        var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => d.FriendlyName)
            .ToList();
    }

    public void ConfigureTranslation(ITranslationService? service)
    {
        _translationService = service;
    }

    public async Task RunAsync(string deviceKeyword, string modelPath, CancellationToken cancellationToken)
    {
        var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var target = devices.FirstOrDefault(
            d => d.FriendlyName.Contains(deviceKeyword, StringComparison.OrdinalIgnoreCase));

        if (target == null)
        {
            StatusChanged?.Invoke($"'{deviceKeyword}' を含むデバイスが見つかりませんでした。");
            return;
        }

        if (!File.Exists(modelPath))
        {
            StatusChanged?.Invoke($"モデルファイルが見つかりません: {modelPath}");
            return;
        }

        using var whisperFactory = WhisperFactory.FromPath(modelPath);
        var processor = whisperFactory.CreateBuilder()
            .WithLanguage("auto")
            .Build();
        _processor = processor;

        using var capture = new WasapiLoopbackCapture(target);
        var bufferedProvider = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferLength = capture.WaveFormat.AverageBytesPerSecond * 5,
            DiscardOnBufferOverflow = true
        };

        ISampleProvider sampleProvider = bufferedProvider.ToSampleProvider();
        if (capture.WaveFormat.Channels == 2)
        {
            sampleProvider = new StereoToMonoSampleProvider(sampleProvider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
        }
        var resampler = new WdlResamplingSampleProvider(sampleProvider, SampleRate);

        capture.DataAvailable += (s, e) =>
        {
            bufferedProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };

        // デバイスが他プロセス(Sonar等)と一時的に競合してエラーになることがあるため、
        // 数回リトライしてから諦める(Sonarを手動再起動しなくても自然に復帰することが多い)
        const int maxRetries = 5;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                capture.StartRecording();
                break;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < maxRetries)
            {
                StatusChanged?.Invoke($"デバイスが使用中のためリトライ中... ({attempt}/{maxRetries})");
                await Task.Delay(1000, cancellationToken);
            }
        }
        StatusChanged?.Invoke($"認識中: {target.FriendlyName}");

        var readBuffer = new float[ChunkSamples];
        var speechBuffer = new List<float>();
        int silenceChunkCount = 0;
        bool inSpeech = false;

        // バックグラウンドで実行中の文字起こしタスクを追跡する。
        // 停止時にこれらが終わるのを待ってからWhisperプロセッサを破棄しないと、
        // 「処理中にDisposeしようとした」エラーになる。
        var pendingTranscriptions = new List<Task>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (bufferedProvider.BufferedDuration.TotalMilliseconds < 40)
                {
                    await Task.Delay(5, cancellationToken);
                }

                int read = resampler.Read(readBuffer, 0, ChunkSamples);
                if (read == 0)
                {
                    await Task.Delay(10, cancellationToken);
                    continue;
                }

                float rms = ComputeRms(readBuffer, read);
                bool isSpeechChunk = rms > EnergyThreshold;

                if (isSpeechChunk)
                {
                    if (!inSpeech)
                    {
                        inSpeech = true;
                        speechBuffer.Clear();
                    }
                    speechBuffer.AddRange(readBuffer.Take(read));
                    silenceChunkCount = 0;
                }
                else if (inSpeech)
                {
                    speechBuffer.AddRange(readBuffer.Take(read));
                    silenceChunkCount++;

                    bool silenceLongEnough = silenceChunkCount >= SilenceChunksToEndSpeech;
                    bool tooLong = speechBuffer.Count / ChunkSamples >= MaxSpeechChunks;

                    if (silenceLongEnough || tooLong)
                    {
                        inSpeech = false;
                        if (speechBuffer.Count / ChunkSamples >= MinSpeechChunks)
                        {
                            var segment = speechBuffer.ToArray();
                            var task = TranscribeSegmentAsync(segment);
                            pendingTranscriptions.Add(task);
                            pendingTranscriptions.RemoveAll(t => t.IsCompleted);
                        }
                        speechBuffer.Clear();
                    }
                }
            }
        }
        finally
        {
            capture.StopRecording();

            // 実行中の文字起こし処理が残っている状態でプロセッサを破棄すると
            // 「Cannot dispose while processing」エラーになるため、完了を待つ
            try
            {
                await Task.WhenAll(pendingTranscriptions);
            }
            catch
            {
                // 個々のタスクの例外はここでは無視する(処理自体は継続不要なため)
            }

            await processor.DisposeAsync();
            _processor = null;

            StatusChanged?.Invoke("停止しました");
        }
    }

    private static float ComputeRms(float[] buffer, int count)
    {
        double sumSquares = 0;
        for (int i = 0; i < count; i++) sumSquares += buffer[i] * buffer[i];
        return (float)Math.Sqrt(sumSquares / count);
    }

    private async Task TranscribeSegmentAsync(float[] samples)
    {
        if (_processor == null) return;

        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(ms), new WaveFormat(SampleRate, 1)))
        {
            foreach (var sample in samples)
            {
                writer.WriteSample(sample);
            }
        }
        ms.Position = 0;

        string? lastText = null;
        await foreach (var result in _processor.ProcessAsync(ms))
        {
            var text = result.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Whisperが無音・非音声区間で出す典型的なハルシネーション
            // (例: [BLANK_AUDIO], [MUSIC], [Silence] など、文全体が角括弧/丸括弧で囲まれたタグ)を無視する
            if (Regex.IsMatch(text, @"^[\[\(].*[\]\)]$"))
            {
                continue;
            }

            if (text == lastText) continue;
            lastText = text;

            bool isDuplicate;
            lock (_dedupLock)
            {
                isDuplicate = text == _lastGlobalText && (DateTime.Now - _lastGlobalTime) < TimeSpan.FromSeconds(10);
                if (!isDuplicate)
                {
                    _lastGlobalText = text;
                    _lastGlobalTime = DateTime.Now;
                }
            }
            if (isDuplicate) continue;

            OriginalTextReceived?.Invoke(text);

            if (_translationService != null)
            {
                var translated = await _translationService.TranslateAsync(text);
                if (translated != null)
                {
                    TranslatedTextReceived?.Invoke(translated);
                }
            }
        }
    }
}

/// <summary>
/// WaveFileWriterがDispose時に内部のMemoryStreamまで閉じてしまわないようにするためのラッパー。
/// </summary>
class IgnoreDisposeStream : Stream
{
    private readonly Stream _inner;
    public IgnoreDisposeStream(Stream inner) => _inner = inner;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        // 内部のMemoryStreamは意図的に閉じない
    }
}
