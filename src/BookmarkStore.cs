// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;

namespace SeeMe
{
    /// <summary>
    /// 书签存储：最近文件列表的置顶收藏。独立于 FileHistory（历史滑出后书签仍保留）。
    /// 存储于 %LOCALAPPDATA%\SeeMe\bookmarks.json。
    /// 构造器可注入自定义存储路径（测试隔离用），默认走真实 LocalApplicationData。
    /// </summary>
    public class BookmarkStore : JsonListStore<string>, IBookmarkStore
    {
        private static string DefaultStoragePath => StoragePaths.Combine("bookmarks.json");

        public event Action? Changed;

        public BookmarkStore(string? storagePath = null) : base(storagePath ?? DefaultStoragePath, "BookmarkStore") { }

        public static BookmarkStore Load(string? storagePath = null)
        {
            var store = new BookmarkStore(storagePath);
            store.LoadFromDisk();
            return store;
        }

        /// <summary>合法条目：路径非空且未重复。</summary>
        protected override bool CanIngest(string p)
            => !string.IsNullOrEmpty(p) && !_items.Contains(p);

        public bool Contains(string path) =>
            !string.IsNullOrEmpty(path) && _items.Contains(path);

        /// <summary>切换书签状态；返回切换后是否已加入书签。</summary>
        public bool Toggle(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (_items.Contains(path)) { _items.Remove(path); Save(); Changed?.Invoke(); return false; }
            _items.Insert(0, path); Save(); Changed?.Invoke(); return true;
        }
    }
}
