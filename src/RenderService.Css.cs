// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Collections.Generic;

namespace SeeMe
{
    /// <summary>RenderService 分部类：CSS 生成（主题变量 / 页面外壳 / 文档排版唯一来源）。</summary>
    public partial class RenderService
    {
        /// <summary>生成 :root（亮）+ html.dark（暗）双套 CSS 变量块（唯一来源 ThemeColors）。</summary>
        public static string ThemeCss() => ThemeColors.ThemeCss();

        /// <summary>页级 CSS reset（占位页/桥接页共用唯一来源）：margin/padding 清零 + 统一盒模型。</summary>
        public const string PageResetCss = "* { margin:0; padding:0; box-sizing:border-box; }";

        /// <summary>加载/解析页共用的旋转指示器样式（.spin + @keyframes spin）。</summary>
        public const string SpinnerCss =
            ".spin { width:18px; height:18px; margin-right:10px; border:2px solid var(--border); border-top-color:var(--accent);" +
            " border-radius:50%; display:inline-block; animation:spin .8s linear infinite; vertical-align:middle; }" +
            "@keyframes spin { to { transform:rotate(360deg); } }";

        /// <summary>
        /// 文档排版共享核心（标题层级 / 引用块 / 图片基础）——Markdown 页与 Office/PDF 桥接页的唯一来源。
        /// office=true 时选择器带 ".content " 前缀（Office/PDF 页内容包裹在 .content 中）。
        /// 两页在段落密度、代码块、表格上有意保持不同设计，差异部分各页自持，不强行统一。
        /// </summary>
        public static string DocumentCss(bool office)
        {
            var s = office ? ".content " : "";
            return $@"
{s}h1,{s}h2,{s}h3,{s}h4,{s}h5,{s}h6 {{ color:var(--heading); font-weight:600; margin:1em 0 .4em; line-height:1.3; }}
{s}h1 {{ font-size:1.5em; padding-bottom:.3em; border-bottom:1px solid var(--h1-border); }}
{s}h2 {{ font-size:1.25em; padding-bottom:.2em; border-bottom:1px solid var(--h2-border); }}
{s}h3 {{ font-size:1.1em; }}
{s}h4 {{ font-size:1em; }}
{s}h5 {{ font-size:.92em; }}
{s}h6 {{ font-size:.85em; color:var(--secondary); }}
{s}blockquote {{ border-left:3px solid var(--quote); background:var(--quote-bg); color:var(--quote-text); border-radius:0 4px 4px 0; }}
{s}img {{ max-width:100%; border-radius:4px; }}
";
        }

        /// <summary>
        /// Office/PDF 桥接页文档 CSS：共享核心（DocumentCss）+ Office 页专属密度/代码块/表格样式。
        /// </summary>
        public static string MdDocumentCss => DocumentCss(true) + @"
.content h1 { margin:.8em 0 .5em; }
.content p { margin:.6em 0; }
.content blockquote { padding:.5em 1em; margin:.8em 0; }
.content img { margin:.5em 0; }
.content code {
  font-family:'Consolas','JetBrains Mono',monospace;
  background:var(--code-bg); padding:2px 6px; border-radius:3px;
  font-size:.9em;
}
.content pre {
  background:var(--code-bg); padding:12px 16px; border-radius:8px;
  overflow-x:auto; margin:.8em 0; font-size:12px; line-height:1.5;
}
.content table {
  border-collapse:collapse; width:100%; margin:1em 0; font-size:.93em;
}
.content th, .content td {
  border:1px solid var(--border); padding:6px 12px; text-align:left;
}
.content th {
  background:var(--card); font-weight:600; color:var(--heading);
}
.content tr:hover td {
  background:var(--row-hov);
}
html.dark .content tr:hover td {
  background:rgba(255,255,255,.06);
}
";
    }
}
