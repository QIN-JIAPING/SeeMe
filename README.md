# SeeMe

一个 Windows 桌面文档查看器，基于 **WPF + WebView2**。以 Markdown 为核心，并扩展支持 Office 文档与 PDF 的「所见即所得」预览。

> 当前版本：**v1.0.3** ｜ 技术栈：.NET 8.0 (Windows) · WPF · WebView2 · Markdig · OpenXML · PdfPig · anydoc-wasm

---

## ✨ 核心特性

| 类别 | 说明 |
|------|------|
| **Markdown 渲染** | Markdig 高级管线：管道表格 / 网格表格 / 任务列表 / 自动链接 / 自动锚点 / Emoji；代码高亮（Prism）、数学公式（KaTeX）、流程图（Mermaid）离线渲染；禁用原始 HTML 以保证安全 |
| **多格式文档** | 直接预览 `.md`、Word(`.docx/.doc/.docm/.rtf/.odt`)、Excel(`.xlsx/.xls/.xlsm/.ods/.csv`)、PowerPoint(`.pptx/.ppt/.odp`)、电子书(`.epub`)、PDF(`.pdf`)，统一 HTML 呈现 |
| **PDF 文本视图** | PDF.js 逐页图文重建：文本行 + 图片按 y 坐标交错排版，点击图片超采样放大；扫描版无文本层自动标记 |
| **目录大纲** | 左侧「大纲」Tab：Markdown 按标题层级生成，点击平滑滚动到锚点；PDF 用书签/标题重建 |
| **页内查找** | Ctrl+F 悬浮搜索栏，逐条高亮定位（Markdown / Office / PDF 通用） |
| **笔记** | 用户自由输入的笔记：添加 / 修改（自动保存）/ 删除 / 清除，按文件持久化 `notes.json`，一键导出 Markdown |
| **高亮笔标注** | 选中文本即高亮（md / Office / PDF），右键删除，重启后按文本恢复；开关状态持久化 |
| **编辑自动保存** | 文本类与 docx 内联编辑，输入停顿 5/10/30 秒自动落盘（可开关），● 未保存指示，退出/切文件不丢字 |
| **内容搜索** | 搜索框输入 `>关键词` 扫描历史文件内容（限量 256KB/文件），命中 PDF 文本层 |
| **打印** | Ctrl+P 调系统打印对话框，Markdown / Office / PDF 文本视图统一 DOM 打印 |
| **文件关联** | 设置页一键设为 .md 默认查看器 + 注册资源管理器右键菜单「用 SeeMe 打开」（HKCU，免管理员） |
| **双栏分屏** | 左右双面板对照阅读，可独立打开不同文件；右栏无文件时自动收折不占空间 |
| **自动刷新** | `FileSystemWatcher` + 防抖，源文件改动即时刷新 |
| **明 / 暗主题** | 完整主题色板（亮 / 暗，含护眼模式），偏好持久化到 `settings.json` |
| **缩放 & 滚动记忆** | 字体缩放（`FontScale`）、滚动位置记忆（`LastScrollY`）随文件恢复 |
| **文件历史 & 书签** | 最近 15 个打开记录；右键收藏书签置顶；反向链接展示引用当前文件的文档 |
| **YAML Front Matter** | Markdown 头部的 YAML 元信息渲染为信息卡片 |
| **窗口状态记忆** | 尺寸 / 位置 / 最大化状态持久化（`window.json`）；多窗口；最小化到托盘 |
| **拖拽 & 命令行** | 支持拖入文件打开；支持启动时传入文件路径参数 |
| **pandoc 导出** | 一键导出 docx / pdf / latex / html / epub / markdown；自定义保存路径 |
| **安全加固** | 多级纵深防御：Markdig 禁用原始 HTML、全链路输出转义、CSP nonce 脚本白名单、文档虚拟主机单文件白名单、file: 导航拦截、外链 scheme 白名单、远程图片按文档授权加载（默认拦截 + no-referrer） |

---

## 🧩 支持的文档类型

| 类别 | 扩展名 | 渲染方式 | 回退 |
|------|--------|----------|------|
| 📝 Markdown | `.md` `.markdown` `.mkd` `.mdown` | Markdig 直渲（主题化 CSS + Prism/KaTeX/Mermaid） | — |
| 📄 Word | `.docx` `.doc` `.docm` `.rtf` `.odt` | anydoc-wasm → Markdown → Markdig | OpenXML 解析 |
| 📊 Excel | `.xlsx` `.xls` `.xlsm` `.ods` `.csv` | anydoc-wasm → Markdown → Markdig | OpenXML 解析（`.xls` 仅 OpenXML） |
| 📽 PowerPoint | `.pptx` `.ppt` `.odp` | anydoc-wasm → Markdown → Markdig | OpenXML 解析 |
| 📚 电子书 | `.epub` | anydoc-wasm → Markdown → Markdig | 错误页 |
| 📕 PDF | `.pdf` | PDF.js 逐页图文重建（文本层、缩放、大纲、页内查找） | — |

> **说明**：
> - **anydoc-wasm**：页内 WebAssembly 转换器，覆盖 Office 12 种格式；失败或 5s 超时自动回退右侧列。
> - **宏文档变体**：`.docm` / `.xlsm` 含宏，仅解析内容不执行宏，映射到 docx/xlsx 解析器。

---

## 📁 项目结构

```
SeeMe/
├── src/                           # 全部源码
│   ├── App.xaml / App.xaml.cs     # 应用入口，多窗口管理，托盘，启动参数打开文件
│   ├── MainWindow.xaml(.cs)       # 主窗口：双栏、搜索、缩放、自动刷新、拖拽、快捷键
│   ├── MainWindow.Files.cs        # 文件打开 / 历史 / 内联编辑（自动保存、编码检测）
│   ├── MainWindow.Rendering.cs    # 各格式渲染入口 + PDF 图文重建页模板
│   ├── MainWindow.Panels.cs       # 面板显隐 / 列宽动画 / 双栏自适应 / 反向链接
│   ├── MainWindow.Notes.cs        # 笔记面板（添加/修改/删除/导出）+ 高亮笔 + 打印
│   ├── RenderService.cs           # Markdown → 主题化 HTML、标注脚本、CSP/消毒
│   ├── FileConverter.cs           # docx / xlsx / pptx / pdf → HTML（OpenXML 回退）
│   ├── HighlightStore.cs          # 高亮标注持久化（highlights.json）
│   ├── NoteStore.cs               # 用户笔记持久化（notes.json）
│   ├── FileAssoc.cs               # 文件关联（HKCU 注册 .md 默认打开 + 右键菜单）
│   ├── GridLengthAnimation.cs     # 右侧面板列宽平滑动画
│   ├── ThemeManager.cs            # 亮/暗主题 ResourceDictionary 构建与持久化
│   ├── FileHistory.cs             # 最近文件历史（持久化）
│   ├── BookmarkStore.cs           # 书签（持久化）
│   ├── PanelState.cs              # 单面板状态（文件、监视器、防抖、滚动、缩放）
│   ├── PdfTextCache.cs            # PDF 文本层提取结果缓存
│   ├── PandocExportService.cs     # pandoc 导出
│   ├── Controls/                  # 自定义控件
│   └── app.manifest / app_icon.*  # 清单与图标
├── tools/                         # 构建/发布/依赖扫描脚本（本地开发用）
├── SeeMe.csproj                   # .NET 8 WPF 工程（支持单文件自包含发布）
├── README.md
├── LICENSE
└── .gitignore
```

---

## 🚀 使用

1. 启动后通过「文件 → 打开」选择文档，或直接**拖拽文件**到窗口。
2. 命令行打开指定文件：
   ```bash
   SeeMe.exe path/to/document.md
   ```
3. 常用操作：
   - **高亮笔**：标题栏 🖍 按钮开启，选中文本自动高亮（右键标注删除）
   - **笔记**：状态栏「笔记」打开面板，输入内容点「添加」，选中笔记直接修改（自动保存），可导出 Markdown
   - **打印**：Ctrl+P 或状态栏「打印」
   - **文件关联**：设置 → 通用设置 → 文件关联，一键设为 .md 默认查看器 / 注册右键菜单
   - **页内查找**：Ctrl+F 悬浮搜索栏；**内容搜索**：搜索框输入 `>关键词`
   - **大纲**：左侧「文件 / 大纲」Tab 切换，点击条目跳转

### ⌨ 快捷键

| 快捷键 | 功能 |
|--------|------|
| Ctrl+O | 打开文件 |
| Ctrl+T | 切换双栏 |
| Ctrl+F | 页内查找 |
| Ctrl+P | 打印 |
| Ctrl+S | 编辑模式保存 |
| Ctrl+N / Ctrl+W | 新建窗口 / 关闭窗口 |
| Ctrl+Shift+D | 切换主题 |
| Ctrl+0 / Ctrl+= / Ctrl+- | 缩放复位 / 放大 / 缩小 |
| F5 | 手动刷新 |

---

## 🛠 构建

要求：Windows + [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) + WebView2 运行时。

```bash
# 还原依赖并构建
dotnet build -c Release

# 单文件自包含发布（win-x64，含运行时）
dotnet publish -c Release -p:PublishSingleFile=true -r win-x64
```

生成物位于 `bin/Release/net8.0-windows/`；单文件发布产物在输出目录（含 `Resources/` 离线资源，需与 exe 同目录）。

---

## 📜 许可证

基于 **MIT 许可证** 开源，详见仓库根目录 `LICENSE` 文件。
