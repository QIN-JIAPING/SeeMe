// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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
    /// </summary>
    public class HighlightStore : IHighlightStore
    {
        private static readonly string StoragePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeeMe", "highlights.json");

        private readonly List<HighlightItem> _items = new();

        public IReadOnlyList<HighlightItem> Items => _items;

        public event Action? Changed;

        public static HighlightStore Load()
        {
            var store = new HighlightStore();
            try
            {
                var dir = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                if (File.Exists(StoragePath))
                {
                    var json = File.ReadAllText(StoragePath);
                    var list = JsonSerializer.Deserialize<List<HighlightItem>>(json);
                    if (list != null)
                    {
                        foreach (var it in list)
                        {
                            if (it != null && !string.IsNullOrEmpty(it.Id) && !string.IsNullOrEmpty(it.Text)
                                && !store._items.Any(x => x.Id == it.Id))
                                store._items.Add(it);
                        }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SeeMe] HighlightStore.Load: " + ex.Message); }
            return store;
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(StoragePath,
                    JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SeeMe] HighlightStore.Save: " + ex.Message); }
        }

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

        public void SetNote(string id, string note)
        {
            var it = _items.FirstOrDefault(i => i.Id == id);
            if (it != null)
            {
                it.Note = note ?? "";
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
