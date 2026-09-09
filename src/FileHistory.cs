// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SeeMe
{
    /// <summary>
    /// 最近文件历史：按打开顺序倒序保存，上限可调。
    /// 存储于 %LOCALAPPDATA%\SeeMe\history.json；只保留磁盘上仍存在的文件。
    /// 构造器可注入自定义存储路径（测试隔离用），默认走真实 LocalApplicationData。
    /// </summary>
    public class FileHistory : JsonListStore<string>, IFileHistory
    {
        private const int DefaultMaxEntries = 15;
        /// <summary>历史上限（可在设置中调整），改小会自动裁剪。</summary>
        public int MaxEntries { get; private set; } = DefaultMaxEntries;

        private static string DefaultStoragePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeeMe", "history.json");

        public IReadOnlyList<string> Entries => _items.AsReadOnly();

        public event Action? Changed;

        public FileHistory(string? storagePath = null) : base(storagePath ?? DefaultStoragePath, "FileHistory") { }

        public static FileHistory Load(string? storagePath = null)
        {
            var history = new FileHistory(storagePath);
            history.LoadFromDisk();
            return history;
        }

        /// <summary>合法条目：文件仍存在且未重复。</summary>
        protected override bool CanIngest(string p)
            => File.Exists(p) && !_items.Contains(p);

        public void Add(string path)
        {
            try
            {
                path = Path.GetFullPath(path);
            }
            catch { return; }

            _items.Remove(path);
            _items.Insert(0, path);

            while (_items.Count > MaxEntries)
                _items.RemoveAt(_items.Count - 1);

            Save();
            Changed?.Invoke();
        }

        public void Remove(string path)
        {
            try
            {
                path = Path.GetFullPath(path);
            }
            catch { return; }

            _items.Remove(path);
            Save();
            Changed?.Invoke();
        }

        public void Clear()
        {
            _items.Clear();
            Save();
            Changed?.Invoke();
        }

        /// <summary>设置历史上限并裁剪超出部分（设置页调用）。</summary>
        public void SetMaxEntries(int max)
        {
            if (max < 1) return;
            MaxEntries = max;
            var trimmed = false;
            while (_items.Count > MaxEntries)
            {
                _items.RemoveAt(_items.Count - 1);
                trimmed = true;
            }
            if (trimmed) Save();
            Changed?.Invoke();
        }
    }
}
