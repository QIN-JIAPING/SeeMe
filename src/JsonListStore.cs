// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SeeMe
{
    /// <summary>
    /// JSON 列表持久化基类（FileHistory/HighlightStore/NoteStore/BookmarkStore 共用唯一来源）。
    /// LoadFromDisk：读盘逐条过 CanIngest 校验后入内存列表（文件缺失/损坏回退空、静默记日志）；
    /// Save：写盘自动建目录（同样静默）。序列化格式与历史版本兼容（List&lt;T&gt; + WriteIndented）。
    /// 子类只负责内存模型、业务操作与 CanIngest 校验。
    /// </summary>
    public abstract class JsonListStore<T>
    {
        private readonly string _storagePath;

        /// <summary>内存列表（子类业务操作的直接数据源）。</summary>
        protected readonly List<T> _items = new();

        protected JsonListStore(string? storagePath, string logTag)
        {
            _storagePath = storagePath
                ?? throw new ArgumentNullException(nameof(storagePath), "测试注入路径不可为 null（默认路径由子类解析）");
            LogTag = logTag;
        }

        /// <summary>日志标签（如 "FileHistory"）。</summary>
        protected string LogTag { get; }

        /// <summary>入盘条目校验钩子：返回 true 才加入内存列表（子类实现去重/合法性检查）。</summary>
        protected abstract bool CanIngest(T item);

        /// <summary>从磁盘加载列表；文件缺失/损坏时回退为当前内存内容（通常为空），不抛异常。</summary>
        protected void LoadFromDisk()
        {
            try
            {
                var dir = Path.GetDirectoryName(_storagePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (!File.Exists(_storagePath)) return;

                var json = File.ReadAllText(_storagePath);
                var list = JsonSerializer.Deserialize<List<T>>(json);
                if (list == null) return;

                foreach (var it in list)
                {
                    if (it != null && CanIngest(it))
                        _items.Add(it);
                }
            }
            catch (Exception ex)
            {
                // 读取失败 → 静默成空列表，用户会看到"历史/书签全没了"。必须留档。
                SeeMeLog.Error(LogTag + ".Load", ex);
            }
        }

        /// <summary>写盘（自动建目录）；失败静默记日志，不抛异常。</summary>
        protected void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_storagePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_storagePath,
                    JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                // 写入失败 → 用户的历史/书签/高亮/笔记没落盘，是数据丢失级问题。必须留档。
                SeeMeLog.Error(LogTag + ".Save", ex);
            }
        }
    }
}
