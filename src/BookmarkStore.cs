using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SeeMe
{
    /// <summary>
    /// 书签存储：最近文件列表的置顶收藏。独立于 FileHistory（历史滑出后书签仍保留）。
    /// 存储于 %LOCALAPPDATA%\SeeMe\bookmarks.json。
    /// </summary>
    public class BookmarkStore : IBookmarkStore
    {
        private static readonly string StoragePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeeMe", "bookmarks.json");

        private readonly List<string> _paths = new();

        /// <summary>书签路径列表（按添加顺序）。</summary>
        public IReadOnlyList<string> Paths => _paths;

        /// <summary>书签变化通知。</summary>
        public event Action? Changed;

        public static BookmarkStore Load()
        {
            var store = new BookmarkStore();
            try
            {
                var dir = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                if (File.Exists(StoragePath))
                {
                    var json = File.ReadAllText(StoragePath);
                    var list = JsonSerializer.Deserialize<List<string>>(json);
                    if (list != null)
                    {
                        foreach (var p in list)
                        {
                            if (!string.IsNullOrEmpty(p) && !store._paths.Contains(p))
                                store._paths.Add(p);
                        }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SeeMe] BookmarkStore.Load: " + ex.Message); }
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
                    JsonSerializer.Serialize(_paths, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SeeMe] BookmarkStore.Save: " + ex.Message); }
        }

        public bool Contains(string path) =>
            !string.IsNullOrEmpty(path) && _paths.Contains(path);

        /// <summary>切换书签状态；返回切换后是否已加入书签。</summary>
        public bool Toggle(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (_paths.Contains(path)) { _paths.Remove(path); Save(); Changed?.Invoke(); return false; }
            _paths.Insert(0, path); Save(); Changed?.Invoke(); return true;
        }

        public void Remove(string path)
        {
            if (!string.IsNullOrEmpty(path) && _paths.Remove(path)) { Save(); Changed?.Invoke(); }
        }
    }
}
