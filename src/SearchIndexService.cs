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
    /// 历史文件全文检索索引（Lucene.NET）：替代「>关键词」的逐文件线性扫描。
    /// 首次搜索时全量建索引（限量 MaxSearchBytes/文件，与旧行为一致），后续搜索 O(1) 查询；
    /// 用「路径:长度:修改时间」签名判断索引是否过期，过期自动重建。
    /// 线程安全：所有操作在内部锁内串行执行。
    /// </summary>
    public sealed class SearchIndexService
    {
        private static readonly LuceneVersion Ver = LuceneVersion.LUCENE_48;
        private readonly string _indexDir;
        private readonly object _lock = new();

        public SearchIndexService(string indexDir) => _indexDir = indexDir;

        /// <summary>搜索历史文件内容，返回命中的文件路径列表。pdfTextProvider 返回 PDF 文本层（无则 null）。</summary>
        public IReadOnlyList<string> Search(
            IReadOnlyList<string> files,
            Func<string, string?> pdfTextProvider,
            string keyword,
            out int indexedCount)
        {
            indexedCount = 0;
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(keyword)) return Array.Empty<string>();
                    Directory.CreateDirectory(_indexDir);

                    using var dir = FSDirectory.Open(_indexDir);
                    // StringField 不经过分词，analyzer 仅作为 IndexWriterConfig 占位
                    using var analyzer = new StandardAnalyzer(Ver);
                    var config = new IndexWriterConfig(Ver, analyzer)
                    {
                        OpenMode = OpenMode.CREATE_OR_APPEND
                    };

                    using var writer = new IndexWriter(dir, config);

                    // 签名比对：内容未变则跳过重建（只读索引查询）
                    var sig = BuildSignature(files, pdfTextProvider, out indexedCount);
                    var sigFile = Path.Combine(_indexDir, "sig.txt");
                    if (!File.Exists(sigFile) || File.ReadAllText(sigFile) != sig)
                    {
                        writer.DeleteAll();
                        IndexFiles(writer, files, pdfTextProvider);
                        writer.Commit();
                        try { File.WriteAllText(sigFile, sig); } catch { }
                    }

                    using var reader = DirectoryReader.Open(dir);
                    var searcher = new IndexSearcher(reader);
                    var q = new WildcardQuery(new Term("content", "*" + EscapeWildcard(keyword.ToLowerInvariant()) + "*"));
                    var top = searcher.Search(q, 500);
                    return top.ScoreDocs
                        .Select(sd => searcher.Doc(sd.Doc).Get("path"))
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList()!;
                }
                catch
                {
                    // 索引损坏/磁盘异常 → 静默降级为空结果，不影响 UI
                    return Array.Empty<string>();
                }
            }
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

        private void IndexFiles(IndexWriter writer, IReadOnlyList<string> files, Func<string, string?> pdfTextProvider)
        {
            foreach (var p in files)
            {
                try
                {
                    if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
                    string? content;
                    if (FileTypes.IsPdf(Path.GetExtension(p)))
                    {
                        content = pdfTextProvider(p);
                        if (string.IsNullOrEmpty(content)) continue;
                    }
                    else
                    {
                        var fi = new FileInfo(p);
                        if (fi.Length > Limits.MaxSearchBytes) continue; // 限量，与旧线性扫描一致
                        content = File.ReadAllText(p);
                    }
                    var doc = new Document
                    {
                        new StringField("path", p, Field.Store.YES),
                        new StringField("content", content.ToLowerInvariant(), Field.Store.NO)
                    };
                    writer.AddDocument(doc);
                }
                catch { /* 单个文件读取失败跳过 */ }
            }
        }

        /// <summary>构建索引签名：路径:长度:修改时间 拼接，任一变化即触发重建。</summary>
        private static string BuildSignature(IReadOnlyList<string> files, Func<string, string?> pdfTextProvider, out int indexedCount)
        {
            indexedCount = 0;
            var sb = new StringBuilder(512);
            foreach (var p in files)
            {
                try
                {
                    if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
                    var fi = new FileInfo(p);
                    sb.Append(p).Append(':').Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append('|');
                    if (fi.Length <= Limits.MaxSearchBytes || FileTypes.IsPdf(Path.GetExtension(p)))
                        indexedCount++;
                }
                catch { }
            }
            return sb.ToString();
        }
    }
}
