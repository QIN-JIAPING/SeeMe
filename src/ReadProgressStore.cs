// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SeeMe
{
    /// <summary>
    /// 单份文档的阅读进度。
    ///
    /// <para><b>为什么同时存 <see cref="Percent"/> 与 <see cref="MaxScrollY"/></b>：
    /// 两者服务不同目的 —— <c>Percent</c> 是给人看的（"读到 63%"、进度条），
    /// <c>MaxScrollY</c> 是给机器用的（下次打开精确还原滚动位置，像素级）。
    /// 只存百分比会丢精度（窗口大小变了百分比对应不到同一位置），
    /// 只存 Y 值则无法显示进度条（不知道文档总高度）。</para>
    /// </summary>
    public class ReadProgress
    {
        /// <summary>文档绝对路径（与 FileHistory / HighlightStore 的 key 口径一致）。</summary>
        public string Path { get; set; } = "";

        /// <summary>阅读百分比 0–100。内容不足一屏时为 100（视为已读完）。</summary>
        public double Percent { get; set; }

        /// <summary>历史最大滚动深度（像素）。还原用，只增不减 —— 回滚到顶部不该抹掉"读到过 80%"的记录。</summary>
        public double MaxScrollY { get; set; }

        /// <summary>最后阅读时间（UTC）。用于「继续阅读」取最新一条。</summary>
        public DateTime LastReadUtc { get; set; }
    }

    /// <summary>
    /// 阅读进度存储。
    ///
    /// <para><b>与 <c>PanelState.LastScrollY</c> 的关系</b>：后者是**内存态**的滚动位置
    /// （只在单次运行、单个面板内有效，切文件即清零）。本类把它升级为**跨会话持久化**，
    /// 并补上百分比与时间戳。<c>OpenFileInternal</c> 打开文件时从本类读回，
    /// 离开文件时写回 —— 这就是"继续阅读"的全部机制。</para>
    ///
    /// <para><b>写入节流是必须的</b>：scroll 事件经 rAF 节流后仍有约 60 次/秒的上报频率，
    /// 每次都写盘会打爆 SSD 且毫无意义。写入时机收敛为两种：
    /// ① 离开文件（<c>OpenFileInternal</c> 的落盘点）；② 进度变化超过 5% 且停顿 3 秒。
    /// 具体策略由调用方（MainWindow）控制，本类只负责持久化。</para>
    ///
    /// <para>构造器可注入自定义存储路径（测试隔离用），默认走真实 LocalApplicationData。</para>
    /// </summary>
    public class ReadProgressStore : JsonListStore<ReadProgress>
    {
        private static string DefaultStoragePath => StoragePaths.Combine("progress.json");

        /// <summary>全部进度条目（按最近阅读时间倒序，便于「继续阅读」直接取第一条）。</summary>
        public IReadOnlyList<ReadProgress> Items =>
            _items.OrderByDescending(i => i.LastReadUtc).ToList().AsReadOnly();

        public event Action? Changed;

        public ReadProgressStore(string? storagePath = null) : base(storagePath ?? DefaultStoragePath, "ReadProgress") { }

        public static ReadProgressStore Load(string? storagePath = null)
        {
            var store = new ReadProgressStore(storagePath);
            store.LoadFromDisk();
            return store;
        }

        /// <summary>
        /// 合法条目：路径非空 + 文件仍存在 + 百分比在 0–100 + 去重（同路径只保留第一条）。
        ///
        /// <para>剔除已删文件沿用 <see cref="FileHistory"/> 的既有口径 ——
        /// 否则用户删了文档，进度列表里会留下永远点不开的僵尸条目。</para>
        /// </summary>
        protected override bool CanIngest(ReadProgress item)
        {
            if (item == null) return false;
            if (string.IsNullOrWhiteSpace(item.Path)) return false;
            if (item.Percent < 0 || item.Percent > 100) return false;
            if (!File.Exists(item.Path)) return false;
            return _items.All(i => !string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>查询某文件的进度；无记录返回 null。</summary>
        public ReadProgress? ForFile(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return _items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 记录进度（存在则更新，不存在则插入）。
        ///
        /// <para><b>MaxScrollY 只增不减</b>：用户读到 80% 又滚回顶部，不该让"读到过哪"
        /// 的记录倒退 —— 那会让下次打开时"继续阅读"跳到一个很早的位置。</para>
        /// </summary>
        public void Record(string path, double percent, double scrollY)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { path = Path.GetFullPath(path); } catch { return; }

            percent = Math.Max(0, Math.Min(100, percent));
            if (double.IsNaN(percent)) return;   // 除零产生的 NaN 防御（见前端 scale 计算）

            var existing = _items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                _items.Add(new ReadProgress
                {
                    Path = path,
                    Percent = percent,
                    MaxScrollY = scrollY > 0 ? scrollY : 0,
                    LastReadUtc = DateTime.UtcNow
                });
            }
            else
            {
                existing.Percent = percent;
                if (scrollY > existing.MaxScrollY) existing.MaxScrollY = scrollY;
                existing.LastReadUtc = DateTime.UtcNow;
            }
            Save();
            Changed?.Invoke();
        }

        public void Remove(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var n = _items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            if (n > 0) { Save(); Changed?.Invoke(); }
        }

        public void Clear()
        {
            _items.Clear();
            Save();
            Changed?.Invoke();
        }

        /// <summary>清理磁盘上已不存在的条目（可在启动时调用，避免列表里堆积打不开的僵尸项）。</summary>
        public int PruneMissing()
        {
            var n = _items.RemoveAll(i => !File.Exists(i.Path));
            if (n > 0) { Save(); Changed?.Invoke(); }
            return n;
        }
    }
}
