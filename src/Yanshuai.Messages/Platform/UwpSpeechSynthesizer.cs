// Platform/UwpSpeechSynthesizer.cs —— 系统语音合成
//
// 用 Windows.Media.SpeechSynthesis：合成到内存流，再用 MediaPlayer 播放。
// 这是 UWP 上唯一稳定的做法 —— 直接 SpeakTextAsync 的 API 早就没了，
// 而 MediaElement 在手机后台会被挂起，MediaPlayer 不会。
//
// 播放完成必须用事件等，不能"发出去就返回"：TtsService 依赖
// SpeakAsync 在播完时才返回，否则切段朗读会全部叠在一起。
// 用 TaskCompletionSource + MediaEnded/MediaFailed 两个事件收口，
// 并额外挂一个超时兜底 —— 播放器偶尔不发 MediaEnded（尤其被系统抢占音频焦点时）。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Platform
{
    public sealed class UwpSpeechSynthesizer : ISpeechSynthesizer, IAudioPlayback, IDisposable
    {
        private readonly ILogger _log;
        private readonly object _gate = new object();

        private SpeechSynthesizer _synth;
        private MediaPlayer _player;
        private TaskCompletionSource<bool> _playback;

        public UwpSpeechSynthesizer(ILogger log)
        {
            _log = log ?? NullLogger.Instance;
        }

        public bool IsAvailable
        {
            get
            {
                try
                {
                    // 设备上可能一个语音都没有（精简版系统镜像）
                    return SpeechSynthesizer.AllVoices != null && SpeechSynthesizer.AllVoices.Count > 0;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct)
        {
            var result = new List<string>();
            try
            {
                foreach (var voice in SpeechSynthesizer.AllVoices)
                {
                    result.Add(voice.DisplayName);
                }
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "tts", "列举音色失败：" + ex.GetType().Name);
            }

            IReadOnlyList<string> readOnly = result;
            return Task.FromResult(readOnly);
        }

        public async Task SpeakAsync(SpeechRequest request, CancellationToken ct)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Text)) return;

            var synth = EnsureSynth();

            // 语速与音量在 UWP 上是 0.5–6.0 与 0–1，越界会抛
            var rate = Math.Max(0.5, Math.Min(6.0, request.Rate));
            var volume = Math.Max(0.0, Math.Min(1.0, request.Volume));

            SpeechSynthesisStream stream;
            try
            {
                stream = await synth.SynthesizeTextToStreamAsync(request.Text).AsTask(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "tts", "合成失败：" + ex.GetType().Name);
                return;
            }

            if (stream == null) return;

            await PlayStreamAsync(stream, stream.ContentType, EstimateGuardMs(stream), volume, ct).ConfigureAwait(false);
        }

        /// <summary>播放朗读模型返回的整段音频（mp3 等），播完才返回。和系统语音共用一个播放器，互相打断。</summary>
        public async Task PlayAsync(byte[] audio, string contentType, double volume, CancellationToken ct)
        {
            if (audio == null || audio.Length == 0) return;
            var memory = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(memory.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(audio);
                await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
                await writer.FlushAsync().AsTask(ct).ConfigureAwait(false);
                writer.DetachStream();
            }
            memory.Seek(0);
            // 压缩音频按 32kbps（4KB/s）这一最低常见码率估时长，再留 3 倍余量
            int guardMs = (int)Math.Min(int.MaxValue, (long)audio.Length / 4096 * 1000 * 3);
            await PlayStreamAsync(memory, string.IsNullOrEmpty(contentType) ? "audio/mpeg" : contentType,
                guardMs, Math.Max(0.0, Math.Min(1.0, volume)), ct).ConfigureAwait(false);
        }

        private async Task PlayStreamAsync(Windows.Storage.Streams.IRandomAccessStream stream, string contentType, int estimatedMs, double volume, CancellationToken ct)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var player = EnsurePlayer();
            if (player == null) return;

            lock (_gate) _playback = completion;

            // MediaPlayer 的事件是 WinRT 的 TypedEventHandler，不是 EventHandler<object>；
            // 签名写错在 UWP 上编译不过（桌面上看不出来）。
            Windows.Foundation.TypedEventHandler<MediaPlayer, object> ended = null;
            Windows.Foundation.TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failed = null;

            ended = delegate { completion.TrySetResult(true); };
            failed = delegate { completion.TrySetResult(false); };

            player.MediaEnded += ended;
            player.MediaFailed += failed;

            try
            {
                player.Volume = volume;
                player.Source = MediaSource.CreateFromStream(stream, contentType);
                player.Play();

                // 兜底：按音频流大小估一个播放时长上限，播放器不发事件时不至于永远等下去
                int guardMs = Math.Max(4000, estimatedMs + 4000);
                var guard = Task.Delay(guardMs, ct);
                await Task.WhenAny(completion.Task, guard).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "tts", "播放失败：" + ex.GetType().Name);
            }
            finally
            {
                player.MediaEnded -= ended;
                player.MediaFailed -= failed;
                lock (_gate) _playback = null;
            }
        }

        /// <summary>
        /// 估算这段音频最长可能播多久，作为等待上限。
        /// 语音合成流一般是 16kHz / 16bit / 单声道，约 32KB/s；
        /// 这里按 8KB/s 反推时长再乘 3 倍余量 —— 宁可多等一会，
        /// 也不要因为估太短而把还没播完的句子切掉。
        /// </summary>
        private static int EstimateGuardMs(SpeechSynthesisStream stream)
        {
            try
            {
                if (stream == null || stream.Size == 0) return 0;
                return (int)Math.Min(int.MaxValue, stream.Size / 8192 * 1000 * 3);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public Task StopAsync()
        {
            TaskCompletionSource<bool> pending;
            lock (_gate) pending = _playback;

            try
            {
                var player = _player;
                if (player != null)
                {
                    player.Pause();
                    player.Source = null;
                }
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "tts", "停止失败：" + ex.GetType().Name);
            }

            if (pending != null) pending.TrySetResult(false);
            return Task.CompletedTask;
        }

        private SpeechSynthesizer EnsureSynth()
        {
            if (_synth == null)
            {
                _synth = new SpeechSynthesizer();

                // 优先挑中文语音；没有就用系统默认
                try
                {
                    foreach (var voice in SpeechSynthesizer.AllVoices)
                    {
                        if (voice.Language != null &&
                            voice.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                        {
                            _synth.Voice = voice;
                            break;
                        }
                    }
                }
                catch (Exception) { }
            }
            return _synth;
        }

        private MediaPlayer EnsurePlayer()
        {
            if (_player == null)
            {
                _player = new MediaPlayer();
                _player.AudioCategory = MediaPlayerAudioCategory.Speech;
            }
            return _player;
        }

        public void Dispose()
        {
            try
            {
                if (_player != null)
                {
                    _player.Dispose();
                    _player = null;
                }
            }
            catch (Exception) { }

            try
            {
                if (_synth != null)
                {
                    _synth.Dispose();
                    _synth = null;
                }
            }
            catch (Exception) { }
        }
    }
}
