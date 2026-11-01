// Core/Memory/MemoryService.cs —— 记忆池
//
// 三层结构里的"档案池 + 可选全局池"这一层（会话摘要另存在 Conversation.Summary）。
// 旧项目同时维护 GlobalMemories / MemoryStore(向量库) / Conversation.MemoryItems 三份，
// 互不同步；这里只留两份（档案池 / 全局池），并把"向量"从条目里拆出去单独存，
// 换嵌入模型时可以直接丢掉向量段重建，文本一条不丢。
//
// 检索策略：优先向量（余弦），没有向量就退到 BM25。两者都算不出来时按重要性返回。
// 打分 = 相似度 × 0.8 + 重要性 × 0.2 —— 让"重要但不完全匹配"的记忆也有机会进来。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public static class MemoryCategories
    {
        public static string Label(MemoryCategory category)
        {
            switch (category)
            {
                case MemoryCategory.Preference: return "偏好";
                case MemoryCategory.Fact: return "事实";
                case MemoryCategory.Experience: return "经历";
                default: return "对话";
            }
        }

        /// <summary>抽取时给不同类别的默认重要性。</summary>
        public static double DefaultImportance(MemoryCategory category)
        {
            switch (category)
            {
                case MemoryCategory.Preference: return 0.8;
                case MemoryCategory.Fact: return 0.7;
                case MemoryCategory.Experience: return 0.65;
                default: return 0.6;
            }
        }

        public static MemoryCategory Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return MemoryCategory.Fact;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "preference":
                case "偏好":
                    return MemoryCategory.Preference;
                case "experience":
                case "经历":
                    return MemoryCategory.Experience;
                case "conversation":
                case "对话":
                    return MemoryCategory.Conversation;
                default:
                    return MemoryCategory.Fact;
            }
        }
    }

    public sealed class MemorySearchHit
    {
        public MemoryItem Item { get; set; }
        public double Score { get; set; }
        public string Via { get; set; } = "";
    }

    public sealed class PrunePlan
    {
        public List<MemoryItem> ToRemove { get; set; }
        public int KeepCount { get; set; }
        public string Reason { get; set; } = "";

        public PrunePlan()
        {
            ToRemove = new List<MemoryItem>();
        }
    }

    public sealed class MemoryService
    {
        private readonly IFileStore _store;
        private readonly ILogger _log;
        private readonly IClock _clock;
        private readonly IEmbedder _embedder;

        private MemoryFile _profile = new MemoryFile();
        private MemoryFile _global = new MemoryFile();
        private string _profileSlug = "";

        private Bm25Index _profileIndex;
        private Bm25Index _globalIndex;
        private bool _vectorIndexDirty = true;
        private bool _globalLoaded;
        private int _globalLoadAttempts;

        public MemoryService(IFileStore store, ILogger log, IClock clock, IEmbedder embedder)
        {
            _store = store;
            _log = log;
            _clock = clock ?? SystemClock.Instance;
            _embedder = embedder;
        }

        public string ProfileSlug { get { return _profileSlug; } }
        public int ProfileCount { get { return _profile.Items.Count; } }
        public int GlobalCount { get { return _global.Items.Count; } }

        public bool HasVectorIndex
        {
            get { return _embedder != null && _embedder.IsAvailable && !_vectorIndexDirty; }
        }

        public IReadOnlyList<MemoryItem> ProfileItems { get { return _profile.Items.AsReadOnly(); } }
        public IReadOnlyList<MemoryItem> GlobalItems { get { return _global.Items.AsReadOnly(); } }

        // ── 装载与保存 ───────────────────────────────────────────────────

        public async Task LoadAsync(string profileSlug)
        {
            _profileSlug = profileSlug ?? "";
            _profile = await JsonStore.TryReadAsync<MemoryFile>(
                _store, WorkspaceLayout.MemoryFile(_profileSlug), _log).ConfigureAwait(false)
                ?? new MemoryFile();

            if (_profile.Items == null) _profile.Items = new List<MemoryItem>();
            if (_profile.Vectors == null) _profile.Vectors = new Dictionary<string, float[]>();

            // 换了嵌入模型或维度不符：向量整段作废，文本保留
            if (_embedder != null && _embedder.IsAvailable &&
                (_profile.Dimensions != _embedder.Dimensions ||
                 !string.Equals(_profile.EmbedderId, EmbedderId(), StringComparison.Ordinal)))
            {
                _profile.Vectors.Clear();
                _profile.Dimensions = 0;
                _vectorIndexDirty = true;
            }

            InvalidateIndexes();
        }

        public async Task LoadGlobalAsync(bool force)
        {
            if (_globalLoaded && !force) return;
            if (!force && _globalLoadAttempts > 0) return;
            _globalLoadAttempts++;

            _global = await JsonStore.TryReadAsync<MemoryFile>(
                _store, WorkspaceLayout.SharedGlobalMemory, _log).ConfigureAwait(false)
                ?? new MemoryFile();

            if (_global.Items == null) _global.Items = new List<MemoryItem>();
            if (_global.Vectors == null) _global.Vectors = new Dictionary<string, float[]>();

            _globalLoaded = true;
            InvalidateIndexes();
        }

        public Task SaveAsync()
        {
            return AtomicFile.WriteAsync(_store, WorkspaceLayout.MemoryFile(_profileSlug), JsonStore.Serialize(_profile));
        }

        public async Task SaveGlobalAsync()
        {
            await LoadGlobalAsync(false).ConfigureAwait(false);
            await AtomicFile.WriteAsync(_store, WorkspaceLayout.SharedGlobalMemory, JsonStore.Serialize(_global))
                .ConfigureAwait(false);
        }

        private void InvalidateIndexes()
        {
            _profileIndex = null;
            _globalIndex = null;
            _vectorIndexDirty = true;
        }

        private string EmbedderId()
        {
            if (_embedder == null) return "";
            return "dims:" + _embedder.Dimensions.ToString(CultureInfo.InvariantCulture);
        }

        // ── 写入 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 加一条记忆。文本归一化后重复的会被合并（提重要性、刷新时间），不新增。
        /// </summary>
        public MemoryItem Add(
            string text, MemoryCategory category, double importance,
            string sourceConversationId, MemoryScope scope)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var trimmed = text.Trim();
            if (trimmed.Length < 3) return null;                 // 太短的没有信息量

            var file = scope == MemoryScope.Global ? _global : _profile;
            var key = TextUtil.Normalize(trimmed);

            foreach (var existing in file.Items)
            {
                if (string.Equals(existing.NormalizedKey, key, StringComparison.Ordinal))
                {
                    if (importance > existing.Importance) existing.Importance = importance;
                    existing.UpdatedAtUtc = _clock.UtcNow;
                    return existing;
                }
            }

            var item = new MemoryItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = trimmed,
                Category = category,
                Importance = Clamp(importance, 0, 1),
                Scope = scope,
                CreatedAtUtc = _clock.UtcNow,
                UpdatedAtUtc = _clock.UtcNow,
                SourceConversationId = sourceConversationId ?? ""
            };
            file.Items.Add(item);
            InvalidateIndexes();
            return item;
        }

        public bool Update(string id, MemoryScope scope, string text, MemoryCategory category, double importance)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("记忆内容不能为空。");
            var item = Find(id, scope);
            if (item == null) return false;
            item.Text = text.Trim();
            item.Category = category;
            item.Importance = Math.Max(0, Math.Min(1, importance));
            item.UpdatedAtUtc = _clock.UtcNow;
            var file = scope == MemoryScope.Global ? _global : _profile;
            file.Vectors.Remove(id);
            InvalidateIndexes();
            return true;
        }

        public void ResetVectors()
        {
            _profile.Vectors.Clear();
            _profile.EmbedderId = "";
            _profile.Dimensions = 0;
            InvalidateIndexes();
        }

        public bool Remove(string id, MemoryScope scope)
        {
            var file = scope == MemoryScope.Global ? _global : _profile;
            for (int i = 0; i < file.Items.Count; i++)
            {
                if (string.Equals(file.Items[i].Id, id, StringComparison.Ordinal))
                {
                    file.Items.RemoveAt(i);
                    file.Vectors.Remove(id);
                    InvalidateIndexes();
                    return true;
                }
            }
            return false;
        }

        public MemoryItem Find(string id, MemoryScope scope)
        {
            var file = scope == MemoryScope.Global ? _global : _profile;
            foreach (var item in file.Items)
            {
                if (string.Equals(item.Id, id, StringComparison.Ordinal)) return item;
            }
            return null;
        }

        // ── 检索 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 检索。优先向量，退化到 BM25。includeGlobal 为真时档案池与全局池一起搜。
        /// </summary>
        public async Task<List<MemorySearchHit>> SearchAsync(
            string query, int topK, bool includeGlobal, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<MemorySearchHit>();
            if (includeGlobal) await LoadGlobalAsync(false).ConfigureAwait(false);

            if (topK <= 0) topK = 5;

            float[] queryVector = null;
            if (_embedder != null && _embedder.IsAvailable)
            {
                try
                {
                    queryVector = await _embedder.EmbedAsync(query, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    queryVector = null;
                    if (_log != null) _log.Log(LogLevel.Warning, "memory", "查询向量计算失败，退到关键词检索");
                }
            }

            var hits = queryVector != null
                ? SearchByVector(queryVector, topK * 4, includeGlobal)
                : SearchByKeyword(query, topK * 4, includeGlobal);

            // 融合打分：相似度为主、重要性为辅
            var merged = new List<MemorySearchHit>();
            foreach (var hit in hits)
            {
                hit.Score = hit.Score * 0.8 + hit.Item.Importance * 0.2;
                merged.Add(hit);
            }

            merged.Sort(delegate (MemorySearchHit a, MemorySearchHit b) { return b.Score.CompareTo(a.Score); });
            if (merged.Count > topK) merged = merged.GetRange(0, topK);

            foreach (var hit in merged) hit.Item.AccessCount++;
            return merged;
        }

        /// <summary>同步版：只用关键词。给不方便 await 的调用点用。</summary>
        public List<MemorySearchHit> Search(string query, int topK, bool includeGlobal)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<MemorySearchHit>();
            if (topK <= 0) topK = 5;
            return SearchByKeyword(query, topK, includeGlobal);
        }

        private List<MemorySearchHit> SearchByVector(float[] queryVector, int limit, bool includeGlobal)
        {
            var hits = new List<MemorySearchHit>();
            CollectVectorHits(_profile, queryVector, hits);
            if (includeGlobal) CollectVectorHits(_global, queryVector, hits);

            hits.Sort(delegate (MemorySearchHit a, MemorySearchHit b) { return b.Score.CompareTo(a.Score); });
            if (limit > 0 && hits.Count > limit) hits = hits.GetRange(0, limit);
            return hits;
        }

        private void CollectVectorHits(MemoryFile file, float[] queryVector, List<MemorySearchHit> into)
        {
            foreach (var item in file.Items)
            {
                float[] vector;
                if (!file.Vectors.TryGetValue(item.Id, out vector)) continue;
                if (vector == null || vector.Length != queryVector.Length) continue;

                double similarity = Cosine(queryVector, vector);
                if (similarity < 0.05) continue;      // 明显不相关就丢

                into.Add(new MemorySearchHit { Item = item, Score = similarity, Via = "vector" });
            }
        }

        private List<MemorySearchHit> SearchByKeyword(string query, int limit, bool includeGlobal)
        {
            if (_profileIndex == null) _profileIndex = BuildIndex(_profile);
            var raw = _profileIndex.Search(query, limit);

            if (includeGlobal)
            {
                if (_globalIndex == null) _globalIndex = BuildIndex(_global);
                raw.AddRange(_globalIndex.Search(query, limit));
            }

            var hits = new List<MemorySearchHit>();
            double max = 1.0;
            foreach (var pair in raw)
            {
                if (pair.Value > max) max = pair.Value;
            }

            foreach (var pair in raw)
            {
                var item = FindById(pair.Key);
                if (item == null) continue;
                hits.Add(new MemorySearchHit
                {
                    Item = item,
                    Score = pair.Value / max,        // 归一到 0–1，好跟向量分统一口径
                    Via = "keyword"
                });
            }

            hits.Sort(delegate (MemorySearchHit a, MemorySearchHit b) { return b.Score.CompareTo(a.Score); });
            if (limit > 0 && hits.Count > limit) hits = hits.GetRange(0, limit);
            return hits;
        }

        private Bm25Index BuildIndex(MemoryFile file)
        {
            var index = new Bm25Index();
            foreach (var item in file.Items)
            {
                index.Add(item.Id, item.Text);
            }
            return index;
        }

        private MemoryItem FindById(string id)
        {
            foreach (var item in _profile.Items)
            {
                if (string.Equals(item.Id, id, StringComparison.Ordinal)) return item;
            }
            foreach (var item in _global.Items)
            {
                if (string.Equals(item.Id, id, StringComparison.Ordinal)) return item;
            }
            return null;
        }

        /// <summary>余弦相似度。维度不符或全零返回 -1（调用方据此丢弃）。</summary>
        public static double Cosine(float[] a, float[] b)
        {
            if (a == null || b == null) return -1;
            if (a.Length == 0 || a.Length != b.Length) return -1;

            double dot = 0, normA = 0, normB = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += (double)a[i] * b[i];
                normA += (double)a[i] * a[i];
                normB += (double)b[i] * b[i];
            }
            if (normA <= 0 || normB <= 0) return -1;
            return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        }

        // ── 上下文注入 ───────────────────────────────────────────────────

        /// <summary>
        /// 生成注入系统提示的记忆块。有用户输入时按相关性排；没有就按重要性。
        /// </summary>
        public async Task<string> BuildContextBlockAsync(
            string userInput, int maxItems, bool includeGlobal, CancellationToken ct)
        {
            if (maxItems <= 0) maxItems = 15;
            if (includeGlobal) await LoadGlobalAsync(false).ConfigureAwait(false);

            List<MemorySearchHit> hits;
            if (!string.IsNullOrWhiteSpace(userInput))
            {
                hits = await SearchAsync(userInput, maxItems, includeGlobal, ct).ConfigureAwait(false);
            }
            else
            {
                hits = new List<MemorySearchHit>();
                foreach (var item in _profile.Items) hits.Add(new MemorySearchHit { Item = item, Score = item.Importance });
                if (includeGlobal)
                {
                    foreach (var item in _global.Items) hits.Add(new MemorySearchHit { Item = item, Score = item.Importance });
                }
                hits.Sort(delegate (MemorySearchHit a, MemorySearchHit b) { return b.Score.CompareTo(a.Score); });
                if (hits.Count > 10) hits = hits.GetRange(0, 10);
            }

            if (hits.Count == 0) return "";

            var sb = new StringBuilder();
            sb.Append("【长期记忆】\n");
            foreach (var hit in hits)
            {
                sb.Append("- [").Append(MemoryCategories.Label(hit.Item.Category)).Append("] ");
                sb.Append(hit.Item.Text);
                if (hit.Score > 0)
                {
                    sb.Append("（相关度 ").Append(hit.Score.ToString("0.00", CultureInfo.InvariantCulture)).Append("）");
                }
                sb.Append('\n');
            }
            return sb.ToString().Trim();
        }

        // ── 剪枝 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 生成剪枝计划。评分 = 重要性 × 0.7 + 新近度加成（24 小时内 +0.3，7 天内 +0.15）。
        /// 先给计划再执行，用户能看到"将删除哪些"。
        /// </summary>
        public PrunePlan PlanPrune(int maxItems)
        {
            var plan = new PrunePlan();
            if (maxItems <= 0) maxItems = 100;

            int total = _profile.Items.Count;
            if (total <= maxItems)
            {
                plan.KeepCount = total;
                plan.Reason = "当前 " + total + " 条，未超过上限 " + maxItems + " 条，无需整理。";
                return plan;
            }

            int keep = (int)Math.Round(maxItems * 0.8);
            if (keep < 1) keep = 1;

            var scored = new List<KeyValuePair<MemoryItem, double>>();
            var now = _clock.UtcNow;
            foreach (var item in _profile.Items)
            {
                double recency = 0;
                var age = (now - item.UpdatedAtUtc).TotalDays;
                if (age <= 1) recency = 0.3;
                else if (age <= 7) recency = 0.15;

                scored.Add(new KeyValuePair<MemoryItem, double>(item, item.Importance * 0.7 + recency));
            }

            scored.Sort(delegate (KeyValuePair<MemoryItem, double> a, KeyValuePair<MemoryItem, double> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            for (int i = keep; i < scored.Count; i++)
            {
                plan.ToRemove.Add(scored[i].Key);
            }

            plan.KeepCount = keep;
            plan.Reason = total + " 条超过上限，将保留 " + keep + " 条，删除 " + plan.ToRemove.Count + " 条。";
            return plan;
        }

        public void ApplyPrune(PrunePlan plan)
        {
            if (plan == null || plan.ToRemove.Count == 0) return;
            foreach (var item in plan.ToRemove)
            {
                var found = Find(item.Id, MemoryScope.Profile);
                if (found != null) Remove(found.Id, MemoryScope.Profile);
            }
        }

        /// <summary>清理长期没用且不重要的条目（30 天未访问且重要性 ≤0.3）。</summary>
        public int CleanupStale(int staleDays)
        {
            if (staleDays <= 0) staleDays = 30;
            var cutoff = _clock.UtcNow.AddDays(-staleDays);
            var stale = new List<string>();

            foreach (var item in _profile.Items)
            {
                if (item.Importance <= 0.3 && item.AccessCount == 0 && item.UpdatedAtUtc < cutoff)
                {
                    stale.Add(item.Id);
                }
            }

            foreach (var id in stale)
            {
                Remove(id, MemoryScope.Profile);
            }
            return stale.Count;
        }

        // ── 向量索引 ─────────────────────────────────────────────────────

        /// <summary>
        /// 为缺少向量的条目补算。嵌入不可用时返回 -1 表示"没做"。
        /// 逐条算，中间可以被取消；重算期间不清空已有向量。
        /// </summary>
        public async Task<int> IndexVectorsAsync(CancellationToken ct)
        {
            if (_embedder == null || !_embedder.IsAvailable) return -1;

            int computed = 0;
            foreach (var item in _profile.Items)
            {
                ct.ThrowIfCancellationRequested();
                if (_profile.Vectors.ContainsKey(item.Id)) continue;

                try
                {
                    var vector = await _embedder.EmbedAsync(item.Text, ct).ConfigureAwait(false);
                    if (vector != null && vector.Length > 0)
                    {
                        _profile.Vectors[item.Id] = vector;
                        computed++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    // 单条失败不中断整批
                }
            }

            if (computed > 0)
            {
                _profile.EmbedderId = EmbedderId();
                _profile.Dimensions = _embedder.Dimensions;
                _vectorIndexDirty = false;
                await SaveAsync().ConfigureAwait(false);
            }
            return computed;
        }

        // ── 抽取 ─────────────────────────────────────────────────────────

        /// <summary>构造抽取记忆用的提示词。用副模型跑，便宜且快。</summary>
        public static string BuildExtractionPrompt(IReadOnlyList<Message> recentMessages, int maxItems)
        {
            var sb = new StringBuilder();
            sb.Append("从下面这段对话里提取值得长期记住的事实。只提取关于用户本人的稳定信息\n");
            sb.Append("（身份、偏好、习惯、明确说过的决定或处境），不要提取寒暄与临时内容。\n\n");
            sb.Append("每行输出一条，格式：类别|内容\n");
            sb.Append("类别只能是 preference / fact / experience 三者之一。\n");
            sb.Append("内容用一句话，不要标点结尾，不要编号。最多 ").Append(maxItems).Append(" 条。\n");
            sb.Append("没有值得记的就输出 NONE。\n\n");
            sb.Append("── 对话 ──\n");

            foreach (var message in recentMessages)
            {
                if (message == null || string.IsNullOrEmpty(message.Content)) continue;
                var role = message.IsUser ? "用户" : "助手";
                sb.Append(role).Append("：").Append(TextUtil.Truncate(message.Content, 300)).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>解析抽取结果。</summary>
        public static List<MemoryItem> ParseExtraction(string modelOutput, string sourceConversationId)
        {
            var items = new List<MemoryItem>();
            if (string.IsNullOrWhiteSpace(modelOutput)) return items;
            if (TextUtil.Contains(modelOutput, "NONE", StringComparison.OrdinalIgnoreCase) &&
                modelOutput.Trim().Length < 12)
            {
                return items;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var rawLine in modelOutput.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("-", StringComparison.Ordinal)) line = line.Substring(1).Trim();
                if (line.Length < 4) continue;

                var category = MemoryCategory.Fact;
                var text = line;

                int pipe = line.IndexOf('|');
                if (pipe > 0 && pipe < 20)
                {
                    category = MemoryCategories.Parse(line.Substring(0, pipe));
                    text = line.Substring(pipe + 1).Trim();
                }

                if (text.Length < 3) continue;
                if (text.Length > 500) text = text.Substring(0, 500);

                items.Add(new MemoryItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Text = text,
                    Category = category,
                    Importance = MemoryCategories.DefaultImportance(category),
                    Scope = MemoryScope.Profile,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    SourceConversationId = sourceConversationId ?? ""
                });
            }

            return items;
        }

        private static double Clamp(double value, double low, double high)
        {
            if (value < low) return low;
            if (value > high) return high;
            return value;
        }
    }
}
