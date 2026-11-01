// Core/Speech/TtsService.cs —— 朗读服务
//
// 这一层负责的是**排队与打断**，不是发声：
//   · 把长回复切成段，一段一段交给引擎（引擎对超长文本的处理各家不一，切段最稳）
//   · 同一时刻只允许一个朗读任务；新的一次朗读会打断旧的
//   · 用户按停止时立刻停，不等当前段播完
//
// 打断语义很容易写错：旧实现在"新朗读开始时"停旧的，但如果旧任务正好在
// await 引擎返回，停完它还会继续播下一段。所以这里用 generation 计数 ——
// 每次新朗读让计数加一，正在跑的任务发现计数变了就自己退出。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class TtsSettings
    {
        public double Rate { get; set; } = 1.0;
        public double Volume { get; set; } = 1.0;
        public string Voice { get; set; } = "";
        /// <summary>是否朗读代码块。默认不读 —— 读出来是噪音。</summary>
        public bool ReadCode { get; set; }
        public int MaxChunkChars { get; set; } = SpeechText.DefaultMaxChunkChars;
        /// <summary>一次朗读最多读多少字，防止误触后停不下来。</summary>
        public int MaxTotalChars { get; set; } = 8000;

        /// <summary>非空时用朗读模型；失败会退回系统语音。</summary>
        public CloudSpeechTarget Cloud { get; set; }

        /// <summary>选了朗读模型却用不了的原因（Provider 已删等）。非空时直接走系统语音并提示。</summary>
        public string CloudProblem { get; set; } = "";

        /// <summary>按全局设置组装：语速音量、系统音色，以及朗读模型（如果选了）。</summary>
        public static TtsSettings From(Workspace workspace)
        {
            return From(workspace.Settings, workspace.ApiProfiles, workspace.GetApiKey);
        }

        /// <summary>用一份（可能还没保存的）设置组装，设置页"试听"用。</summary>
        public static TtsSettings From(GlobalSettings s, ApiProfileFile providers, Func<ApiProfile, string> keys)
        {
            var result = new TtsSettings { Rate = s.SpeechRate, Volume = s.SpeechVolume, Voice = s.SpeechVoice };
            if (s.SpeechEngine != SpeechEngines.Model) return result;
            var api = ModelSelection.ResolveRef(providers, s.SpeechModel);
            if (api == null)
            {
                result.CloudProblem = s.SpeechModel != null && s.SpeechModel.IsSet ? "朗读模型所在的 Provider 已删除。" : "还没有选择朗读模型。";
                return result;
            }
            var unsupported = CloudSpeech.Unsupported(api);
            if (unsupported.Length > 0) { result.CloudProblem = unsupported; return result; }
            result.Cloud = new CloudSpeechTarget { Api = api, ApiKey = keys != null ? keys(api) ?? "" : "", Voice = s.SpeechModelVoice };
            return result;
        }
    }

    public sealed class TtsService
    {
        private readonly ISpeechSynthesizer _synth;
        private readonly ILogger _log;
        private readonly IHttpTransport _http;
        private readonly IAudioPlayback _audio;

        private readonly object _gate = new object();
        private int _generation;
        private Task _current = Task.CompletedTask;

        public TtsService(ISpeechSynthesizer synth, ILogger log, IHttpTransport http = null, IAudioPlayback audio = null)
        {
            _synth = synth ?? NullSpeechSynthesizer.Instance;
            _log = log ?? NullLogger.Instance;
            _http = http;
            _audio = audio;
        }

        /// <summary>系统语音是否可用。</summary>
        public bool IsAvailable { get { return _synth.IsAvailable; } }

        /// <summary>按这组设置能不能出声：朗读模型可用，或系统语音可用。</summary>
        public bool CanSpeak(TtsSettings settings)
        {
            return CloudReady(settings) || _synth.IsAvailable;
        }

        private bool CloudReady(TtsSettings settings)
        {
            return settings != null && settings.Cloud != null && _http != null && _audio != null;
        }

        /// <summary>朗读模型失败、改用系统语音时触发，参数是原因。供界面提示。</summary>
        public event Action<string> Fallback;

        /// <summary>朗读进度：第几段、共几段。供界面显示。</summary>
        public event Action<int, int> Progress;

        public event Action Completed;

        public bool IsSpeaking
        {
            get
            {
                lock (_gate) return !_current.IsCompleted;
            }
        }

        /// <summary>
        /// 朗读一段 Markdown。会打断正在进行的朗读。
        /// </summary>
        public async Task SpeakAsync(string markdown, TtsSettings settings, CancellationToken ct)
        {
            settings = settings ?? new TtsSettings();
            if (!CanSpeak(settings)) return;
            if (string.IsNullOrWhiteSpace(markdown)) return;

            var chunks = SpeechText.Prepare(markdown, settings.ReadCode, settings.MaxChunkChars);
            if (chunks.Count == 0) return;

            // 总量保护
            var selected = new List<SpeechChunk>();
            int total = 0;
            foreach (var chunk in chunks)
            {
                if (total + chunk.Text.Length > settings.MaxTotalChars) break;
                selected.Add(chunk);
                total += chunk.Text.Length;
            }
            if (selected.Count == 0) return;

            int myGeneration;
            lock (_gate)
            {
                _generation++;
                myGeneration = _generation;
            }

            // 打断上一次：先让它自己发现代数变了，再停引擎
            await StopEnginesAsync().ConfigureAwait(false);

            var task = RunAsync(selected, settings, myGeneration, ct);
            lock (_gate) _current = task;

            await task.ConfigureAwait(false);
        }

        private async Task RunAsync(List<SpeechChunk> chunks, TtsSettings settings, int generation, CancellationToken ct)
        {
            int start = 0;
            if (CloudReady(settings))
            {
                start = await RunCloudAsync(chunks, settings, generation, ct).ConfigureAwait(false);
                if (start < 0) return;
            }
            else if (!string.IsNullOrEmpty(settings.CloudProblem))
            {
                RaiseFallback(settings.CloudProblem);
            }

            if (start < chunks.Count)
            {
                if (!_synth.IsAvailable) return;
                if (!await RunSystemAsync(chunks, start, settings, generation, ct).ConfigureAwait(false)) return;
            }

            if (IsCurrent(generation))
            {
                var handler = Completed;
                if (handler != null) handler();
            }
        }

        /// <summary>
        /// 朗读模型：播第 i 段时已经在合成第 i+1 段，段与段之间不再等网络。
        /// 返回值：-1 = 被打断 / 取消；chunks.Count = 全部读完；其他 = 从这一段起改用系统语音。
        /// </summary>
        private async Task<int> RunCloudAsync(List<SpeechChunk> chunks, TtsSettings settings, int generation, CancellationToken ct)
        {
            Task<CloudSpeechResult> next = Fetch(settings, chunks[0].Text, ct);
            for (int i = 0; i < chunks.Count; i++)
            {
                if (!IsCurrent(generation) || ct.IsCancellationRequested) return -1;

                CloudSpeechResult audio;
                try { audio = await next.ConfigureAwait(false); }
                catch (OperationCanceledException) { return -1; }
                if (!IsCurrent(generation)) return -1;

                if (!audio.Ok)
                {
                    _log.Log(LogLevel.Warning, "tts", "朗读模型失败：" + audio.Error);
                    RaiseFallback(audio.Error);
                    return i;
                }

                next = i + 1 < chunks.Count ? Fetch(settings, chunks[i + 1].Text, ct) : null;

                var handler = Progress;
                if (handler != null) handler(i + 1, chunks.Count);

                try
                {
                    await _audio.PlayAsync(audio.Audio, audio.ContentType, settings.Volume, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return -1;
                }
                catch (Exception ex)
                {
                    _log.Log(LogLevel.Warning, "tts", "播放朗读模型音频失败：" + ex.GetType().Name);
                    RaiseFallback("这台设备播放不了朗读模型返回的音频。");
                    return i;
                }
            }
            return chunks.Count;
        }

        private Task<CloudSpeechResult> Fetch(TtsSettings settings, string text, CancellationToken ct)
        {
            return CloudSpeech.SynthesizeAsync(_http, settings.Cloud, text, settings.Rate, ct);
        }

        private void RaiseFallback(string reason)
        {
            var handler = Fallback;
            if (handler != null) handler(reason ?? "");
        }

        /// <summary>系统语音从 start 段读到底。被打断返回 false。</summary>
        private async Task<bool> RunSystemAsync(List<SpeechChunk> chunks, int start, TtsSettings settings, int generation, CancellationToken ct)
        {
            for (int i = start; i < chunks.Count; i++)
            {
                if (!IsCurrent(generation)) return false;
                if (ct.IsCancellationRequested) return false;

                var handler = Progress;
                if (handler != null) handler(i + 1, chunks.Count);

                try
                {
                    await _synth.SpeakAsync(new SpeechRequest
                    {
                        Text = chunks[i].Text,
                        Rate = settings.Rate,
                        Volume = settings.Volume,
                        Voice = settings.Voice,
                        Language = "zh-CN"
                    }, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    _log.Log(LogLevel.Warning, "tts", "朗读失败：" + ex.GetType().Name);
                    return false;
                }
            }
            return true;
        }

        /// <summary>停止朗读。代数加一让正在跑的任务自行退出，再停引擎。</summary>
        public async Task StopAsync()
        {
            lock (_gate) _generation++;
            await StopEnginesAsync().ConfigureAwait(false);
        }

        private async Task StopEnginesAsync()
        {
            try
            {
                await _synth.StopAsync().ConfigureAwait(false);
                if (_audio != null) await _audio.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "tts", "停止朗读失败：" + ex.GetType().Name);
            }
        }

        private bool IsCurrent(int generation)
        {
            lock (_gate) return _generation == generation;
        }
    }
}
