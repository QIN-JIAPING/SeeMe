// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SeeMe
{
    /// <summary>一条高亮标注：对应文档中一段被选中的文本（含上下文与用户笔记）。</summary>
    public sealed class HighlightItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string File { get; set; } = "";
        public string Text { get; set; } = "";
        public string Context { get; set; } = "";
        public string Note { get; set; } = "";
        public DateTime Created { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 高亮标注存储：按文件路径持久化选区高亮，独立于 FileHistory / BookmarkStore。
    /// 存储于 %LOCALAPPDATA%\SeeMe\highlights.json，重启后标注仍保留。
    /// 构造器可注入自定义存储路径（测试隔离用），默认走真实 LocalApplicationData。
    /// </summary>
    public class HighlightStore : JsonListStore<HighlightItem>, IHighlightStore
    {
        private static string DefaultStoragePath => StoragePaths.Combine("highlights.json");

        public IReadOnlyList<HighlightItem> Items => _items;

        public event Action? Changed;

        public HighlightStore(string? storagePath = null) : base(storagePath ?? DefaultStoragePath, "HighlightStore") { }

        public static HighlightStore Load(string? storagePath = null)
        {
            var store = new HighlightStore(storagePath);
            store.LoadFromDisk();
            return store;
        }

        /// <summary>合法条目：Id/Text 非空且 Id 未重复。</summary>
        protected override bool CanIngest(HighlightItem it)
            => !string.IsNullOrEmpty(it.Id) && !string.IsNullOrEmpty(it.Text)
               && !_items.Any(x => x.Id == it.Id);

        public IReadOnlyList<HighlightItem> ForFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return Array.Empty<HighlightItem>();
            return _items.Where(i => string.Equals(i.File, path, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public void Add(HighlightItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Text) || string.IsNullOrEmpty(item.File)) return;
            _items.Insert(0, item);
            Save();
            Changed?.Invoke();
        }

        public void Remove(string id)
        {
            var it = _items.FirstOrDefault(i => i.Id == id);
            if (it != null)
            {
                _items.Remove(it);
                Save();
                Changed?.Invoke();
            }
        }

        public void ClearFile(string path)
        {
            var removed = _items.RemoveAll(i => string.Equals(i.File, path, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                Save();
                Changed?.Invoke();
            }
        }
    }
}
