// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Directory = System.IO.Directory;

namespace SeeMe
{
    /// <summary>
    /// 历史文件全文检索索引（Lucene.NET）：替代「&gt;关键词」的逐文件线性扫描。
    ///
    /// <para><b>查询方式</b>：<c>content</c> 是 <see cref="StringField"/>（整体不入分词器，
    /// 全文即一个 term），配合 <see cref="WildcardQuery"/> 的 <c>*kw*</c> 实现
    /// <b>大小写不敏感的子串匹配</b>。这样中文无需分词即可命中 ——
    /// 用 <c>StandardAnalyzer</c> 做正经分词反而会把无空格的中文整句当成一个 term。</para>
    ///
    /// <para><b>增量更新</b>：每个文档带一个 <c>sig</c>（长度 + 修改时间）。
    /// 搜索时先读出索引内已有签名，只对<b>新增 / 变化</b>的文件取内容重建，
    /// 消失的文件删除 —— 不再是"签名一变就 <c>DeleteAll</c> 全量重建"。
    /// 这对 PDF 尤其重要：旧实现每次历史变动都要把所有 PDF 的文本层重新提取一遍。</para>
    ///
    /// <para><b>结果片段</b>：命中文件里截取关键词上下文一起返回。之前只返回路径，
    /// 用户看到一排文件名却不知道命中了什么。</para>
    ///
    /// <para><b>线程安全</b>：所有操作在内部锁内串行执行。</para>
    /// </summary>
    public sealed class SearchIndexService
    {
        private static readonly LuceneVersion Ver = LuceneVersion.LUCENE_48;

        /// <summary>单次搜索返回的命中上限（历史文件本身有 10–50 条上限，这里只是兜底）。</summary>
        private const int MaxHits = 200;

        /// <summary>片段中关键词前后各保留的字符数。</summary>
        private const int SnippetContextChars = 40;

        private readonly string _indexDir;
        private readonly object _lock = new();

        public SearchIndexService(string indexDir) => _indexDir = indexDir;

        /// <summary>一次搜索的结果：命中（含片段）与本次索引动作的统计。</summary>
        public sealed class Outcome
        {
            /// <summary>命中的文件与上下文片段（按路径排序，顺序稳定）。</summary>
            public IReadOnlyList<SearchHit> Hits { get; init; } = Array.Empty<SearchHit>();

            /// <summary>索引中当前的文档总数。</summary>
            public int IndexedFiles { get; init; }

            /// <summary>本次搜索中新增或重建索引的文件数。</summary>
            public int Updated { get; init; }

            /// <summary>本次搜索中从索引移除的文件数（已删除或不再在历史中）。</summary>
            public int Removed { get; init; }
        }

        /// <summary>一条命中：文件路径 + 关键词上下文片段（片段可能为空串）。</summary>
        public sealed record SearchHit(string Path, string Snippet);

        /// <summary>
        /// 搜索历史文件内容。索引不存在时自动全量建立；已存在时只增量更新变化的文件。
        /// </summary>
        /// <param name="files">要纳入索引的文件（通常是历史记录）。</param>
        /// <param name="textProvider">PDF 文本层提供者；返回 null 表示尚未提取（该文件本次跳过，下次重试）。</param>
        /// <param name="keyword">关键词（调用方已转小写；匹配本身大小写不敏感）。</param>
        public Outcome Search(
            IReadOnlyList<string> files,
            Func<string, string?> textProvider,
            string keyword)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(keyword))
                        return new Outcome();

                    Directory.CreateDirectory(_indexDir);

                    using var dir = FSDirectory.Open(_indexDir);
                    // StringField 不经过分词，analyzer 仅作为 IndexWriterConfig 占位
                    using var analyzer = new StandardAnalyzer(Ver);
                    var config = new IndexWriterConfig(Ver, analyzer)
                    {
                        OpenMode = OpenMode.CREATE_OR_APPEND
                    };
                    using var writer = new IndexWriter(dir, config);

                    var existing = ReadSignatures(dir);
                    var (updated, removed) = UpdateIndex(writer, existing, files, textProvider);

                    using var reader = DirectoryReader.Open(dir);
                    var searcher = new IndexSearcher(reader);
                    var q = new WildcardQuery(new Term("content", "*" + EscapeWildcard(keyword) + "*"));
                    var top = searcher.Search(q, MaxHits);

                    // Distinct + 排序：Lucene 的命中顺序对纯通配查询没有语义，排一下让列表稳定
                    var paths = top.ScoreDocs
                        .Select(sd => searcher.Doc(sd.Doc).Get("path"))
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var hits = paths
                        .Select(p => new SearchHit(p!, BuildSnippet(ReadText(p!, textProvider), keyword)))
                        .ToList();

                    return new Outcome
                    {
                        Hits = hits,
                        IndexedFiles = reader.NumDocs,
                        Updated = updated,
                        Removed = removed,
                    };
                }
                catch (Exception ex)
                {
                    // 索引损坏/磁盘异常 → 降级为空结果，不影响 UI。留档便于定位"搜索总是没结果"。
                    SeeMeLog.Error("SearchIndexService.Search", ex);
                    return new Outcome();
                }
            }
        }

        // ──────────────── 增量索引 ────────────────

        /// <summary>
        /// 只维护索引、不做查询：把索引对齐到 <paramref name="files"/>（设置页「重建」用，
        /// 也是把"建索引"与"查关键词"两件事解耦的入口）。
        /// </summary>
        public Outcome Sync(IReadOnlyList<string> files, Func<string, string?> textProvider)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(_indexDir);
                    using var dir = FSDirectory.Open(_indexDir);
                    using var analyzer = new StandardAnalyzer(Ver);
                    var config = new IndexWriterConfig(Ver, analyzer)
                    {
                        OpenMode = OpenMode.CREATE_OR_APPEND
                    };
                    using var writer = new IndexWriter(dir, config);

                    var existing = ReadSignatures(dir);
                    var (updated, removed) = UpdateIndex(writer, existing, files, textProvider);

                    int count;
                    using (var reader = DirectoryReader.Open(dir)) count = reader.NumDocs;

                    return new Outcome
                    {
                        Hits = Array.Empty<SearchHit>(),
                        IndexedFiles = count,
                        Updated = updated,
                        Removed = removed,
                    };
                }
                catch (Exception ex)
                {
                    SeeMeLog.Error("SearchIndexService.Sync", ex);
                    return new Outcome();
                }
            }
        }

        /// <summary>读出索引内每个文档的签名（路径 → sig），用于判断哪些文件需要重建。</summary>
        private static Dictionary<string, string> ReadSignatures(Lucene.Net.Store.Directory dir)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!DirectoryReader.IndexExists(dir)) return map;

            using var reader = DirectoryReader.Open(dir);
            var searcher = new IndexSearcher(reader);
            var all = searcher.Search(new MatchAllDocsQuery(), Math.Max(1, reader.NumDocs));
            foreach (var sd in all.ScoreDocs)
            {
                var d = searcher.Doc(sd.Doc);
                var p = d.Get("path");
                if (!string.IsNullOrEmpty(p)) map[p!] = d.Get("sig") ?? "";
            }
            return map;
        }

        /// <summary>
        /// 把索引对齐到 <paramref name="files"/>：删除已消失的、重建新增或变化的。
        /// 返回（新增/重建数，删除数）。
        /// </summary>
        private static (int Updated, int Removed) UpdateIndex(
            IndexWriter writer,
            Dictionary<string, string> existing,
            IReadOnlyList<string> files,
            Func<string, string?> textProvider)
        {
            // 期望集合：只做 FileInfo 级探测（长度 + 修改时间），**不读文件内容**
            var wanted = new List<(string Path, string Sig)>();
            foreach (var p in files)
            {
                if (string.IsNullOrEmpty(p)) continue;
                try
                {
                    var fi = new FileInfo(p);
                    if (!fi.Exists) continue;
                    // 非 PDF 限长（与旧的线性扫描口径一致）；PDF 走文本层，长度天然受限
                    if (!FileTypes.IsPdf(Path.GetExtension(p)) && fi.Length > Limits.MaxSearchBytes) continue;
                    wanted.Add((p, fi.Length + ":" + fi.LastWriteTimeUtc.Ticks));
                }
                catch { /* 单个文件探测失败跳过 */ }
            }

            var wantedPaths = new HashSet<string>(wanted.Select(w => w.Path), StringComparer.OrdinalIgnoreCase);

            var removed = 0;
            foreach (var stale in existing.Keys.Where(k => !wantedPaths.Contains(k)).ToList())
            {
                try { writer.DeleteDocuments(new Term("path", stale)); removed++; }
                catch { }
            }

            var updated = 0;
            foreach (var (path, sig) in wanted)
            {
                // 签名一致 → 内容未变，跳过（这是"增量"的关键：不再无脑重读所有文件）
                if (existing.TryGetValue(path, out var oldSig) && oldSig == sig) continue;

                string? content;
                if (FileTypes.IsPdf(Path.GetExtension(path)))
                {
                    content = textProvider(path);
                    // 文本层尚未提取（该 PDF 还没被打开过）→ 本次不索引，下次搜索再试
                    if (string.IsNullOrEmpty(content)) continue;
                }
                else
                {
                    try { content = File.ReadAllText(path); }
                    catch { continue; }
                }

                var doc = new Document
                {
                    new StringField("path", path, Field.Store.YES),
                    new StringField("content", content.ToLowerInvariant(), Field.Store.NO),
                    new StringField("sig", sig, Field.Store.YES),
                };
                // UpdateDocument = 先按 path 删旧再写入，天然幂等
                writer.UpdateDocument(new Term("path", path), doc);
                updated++;
            }

            if (updated > 0 || removed > 0) writer.Commit();
            return (updated, removed);
        }

        // ──────────────── 片段 ────────────────

        /// <summary>读取命中文件的正文用于截片段（PDF 走缓存文本层）。失败返回 null。</summary>
        private static string? ReadText(string path, Func<string, string?> textProvider)
        {
            try
            {
                return FileTypes.IsPdf(Path.GetExtension(path))
                    ? textProvider(path)
                    : File.ReadAllText(path);
            }
            catch { return null; }
        }

        /// <summary>截取关键词上下文；未命中返回空串。</summary>
        internal static string BuildSnippet(string? content, string keyword)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(keyword)) return "";
            var idx = content!.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";

            var start = Math.Max(0, idx - SnippetContextChars);
            var end = Math.Min(content.Length, idx + keyword.Length + SnippetContextChars);
            var sb = new StringBuilder(end - start + 4);
            if (start > 0) sb.Append('…');
            sb.Append(CollapseWhitespace(content.Substring(start, end - start)));
            if (end < content.Length) sb.Append('…');
            return sb.ToString();
        }

        /// <summary>
        /// 把片段内的换行与连续空白压成单个空格。
        /// 列表项是单行显示，原样带换行会把卡片布局撑破（Markdown 正文换行极密集）。
        /// </summary>
        private static string CollapseWhitespace(string s)
        {
            var sb = new StringBuilder(s.Length);
            var lastWasSpace = false;
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!lastWasSpace) sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(ch);
                    lastWasSpace = false;
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>把通配符特殊字符转义，避免用户输入 * ? \ 破坏查询语法。</summary>
        private static string EscapeWildcard(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                if (c == '*' || c == '?' || c == '\\')
                    sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ──────────────── 索引管理（设置页用） ────────────────

        /// <summary>索引目录的绝对路径（设置页展示用）。</summary>
        public string IndexDirectory => _indexDir;

        /// <summary>索引中的文档数；索引不存在或损坏返回 0。</summary>
        public int DocumentCount()
        {
            lock (_lock)
            {
                try
                {
                    if (!Directory.Exists(_indexDir)) return 0;
                    using var dir = FSDirectory.Open(_indexDir);
                    if (!DirectoryReader.IndexExists(dir)) return 0;
                    using var reader = DirectoryReader.Open(dir);
                    return reader.NumDocs;
                }
                catch (Exception ex)
                {
                    SeeMeLog.Error("SearchIndexService.DocumentCount", ex);
                    return 0;
                }
            }
        }

        /// <summary>索引占用的磁盘字节数。</summary>
        public long SizeBytes()
        {
            try
            {
                if (!Directory.Exists(_indexDir)) return 0;
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(_indexDir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                return total;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 清空索引：直接删掉索引目录。
        ///
        /// <para><b>为什么用删目录而不是 <c>IndexWriter.DeleteAll()</c></b>：
        /// <c>DeleteAll</c> 只是把文档标记为已删除，段文件仍在磁盘上占着空间
        /// （要到下一次 merge 才回收）—— 用户点「清空索引」看到的占用几乎不变，会以为没生效。
        /// 本类的所有操作都是"用完即关"，进程内不长期持有目录句柄，删除是安全的。</para>
        /// </summary>
        /// <returns>成功返回 true；失败返回 false（调用方提示用户）。</returns>
        public bool Clear()
        {
            lock (_lock)
            {
                try
                {
                    if (Directory.Exists(_indexDir)) Directory.Delete(_indexDir, recursive: true);
                    return true;
                }
                catch (Exception ex)
                {
                    SeeMeLog.Error("SearchIndexService.Clear", ex);
                    return false;
                }
            }
        }
    }
}
