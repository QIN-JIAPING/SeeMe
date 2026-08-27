// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SeeMe
{
    /// <summary>一条用户笔记：用户自由输入的正文，与高亮标注完全解耦，随文件路径持久化。</summary>
    public sealed class NoteItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string File { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime Created { get; set; } = DateTime.Now;
        public DateTime Modified { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 用户笔记存储：按文件路径持久化用户输入笔记（独立于 HighlightStore）。
    /// 存储于 %LOCALAPPDATA%\SeeMe\notes.json，重启后笔记仍保留。
    /// </summary>
    public class NoteStore : INoteStore
    {
        private static readonly string StoragePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeeMe", "notes.json");

        private readonly List<NoteItem> _items = new();

        public IReadOnlyList<NoteItem> Items => _items;

        public event Action? Changed;

        public static NoteStore Load()
        {
            var store = new NoteStore();
            try
            {
                var dir = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                if (File.Exists(StoragePath))
                {
                    var json = File.ReadAllText(StoragePath);
                    var list = JsonSerializer.Deserialize<List<NoteItem>>(json);
                    if (list != null)
                    {
                        foreach (var it in list)
                        {
                            if (it != null && !string.IsNullOrEmpty(it.Id) && !string.IsNullOrEmpty(it.File)
                                && !store._items.Any(x => x.Id == it.Id))
                                store._items.Add(it);
                        }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SeeMe] NoteStore.Load: " + ex.Message); }
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
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SeeMe] NoteStore.Save: " + ex.Message); }
        }

        public IReadOnlyList<NoteItem> ForFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return Array.Empty<NoteItem>();
            return _items.Where(i => string.Equals(i.File, path, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.Modified).ToList();
        }

        public NoteItem Add(string file, string content)
        {
            var item = new NoteItem { File = file ?? "", Content = content ?? "" };
            _items.Insert(0, item);
            Save();
            Changed?.Invoke();
            return item;
        }

        public void Update(string id, string content)
        {
            var it = _items.FirstOrDefault(i => i.Id == id);
            if (it != null)
            {
                it.Content = content ?? "";
                it.Modified = DateTime.Now;
                Save();
                Changed?.Invoke();
            }
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
