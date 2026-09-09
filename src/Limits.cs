// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

namespace SeeMe
{
    /// <summary>
    /// 防御性资源上限（DoS 防护）集中定义，供运行时与测试统一引用，避免 magic number 散落漂移。
    /// 边界测试直接以本类常量 ±1 构造用例，杜绝 off-by-one 与常量不同步。
    /// </summary>
    public static class Limits
    {
        /// <summary>内联图片大小上限（超限不内联，防 OOM）。</summary>
        public const long MaxInlineImageBytes = 5 * 1024 * 1024;

        /// <summary>内容搜索 / 自定义 CSS 单文件大小上限（超限跳过）。</summary>
        public const long MaxSearchBytes = 256 * 1024;

        /// <summary>内联编辑文件大小上限（超限建议用外部编辑器）。</summary>
        public const long MaxEditBytes = 5 * 1024 * 1024;

        /// <summary>Excel 单表最大渲染行数。</summary>
        public const int MaxExcelRows = 5000;

        /// <summary>Excel 最大渲染工作表数。</summary>
        public const int MaxExcelSheets = 50;

        /// <summary>PPT 最大渲染幻灯片数。</summary>
        public const int MaxPptSlides = 200;

        /// <summary>
        /// Office 容器（docx/xlsx/pptx = zip）解压后总大小上限（防 zip 炸弹 OOM）。
        /// 按中央目录声明的 entry.Length 累计预检，超限拒绝解析。
        /// </summary>
        public const long MaxDecompressedBytes = 256L * 1024 * 1024;
    }
}
