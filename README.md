# SeeMe

一个 Windows 桌面文档查看器，基于 **WPF + WebView2**。以 Markdown 为核心，并扩展支持 Office 文档与 PDF 的「所见即所得」预览。

> 当前版本：**v1.0.1** ｜ 技术栈：.NET 8.0 (Windows) · WPF · WebView2 · Markdig · OpenXML · PdfPig

---

## ✨ 核心特性

| 类别 | 说明 |
|------|------|
| **Markdown 渲染** | Markdig 高级管线：管道表格 / 网格表格 / 任务列表 / 自动链接 / 自动锚点 / Emoji；禁用原始 HTML 以保证安全 |
| **多格式文档** | 直接预览 `.md`、Word(`.docx/.doc/.docm/.rtf/.odt`)、Excel(`.xlsx/.xls/.xlsm/.ods/.csv`)、PowerPoint(`.pptx/.ppt/.odp`)、电子书(`.epub`)、PDF(`.pdf`)，统一 HTML 呈现 |
| **PDF 文本层** | 打开时用 anydoc 内嵌 pdf-inspector 毫秒级提取文本：`>关键词` 内容搜索命中 PDF、扫描版无文本层自动标记 |
| **编辑自动保存** | 编辑模式输入停顿 5/10/30 秒自动落盘（可开关），顶栏 ● 未保存指示，退出/切文件不丢字；开关与间隔改动对已打开编辑页即时生效 |
| **双栏分屏** | 左右双面板对照阅读，可独立打开不同文件 |
| **自动刷新** | `FileSystemWatcher` + 350ms 防抖，源文件改动即时刷新 |
| **明 / 暗主题** | 完整主题色板（亮 / 暗），偏好持久化到 `settings.json` |
| **缩放 & 滚动记忆** | 字体缩放（`FontScale`）、滚动位置记忆（`LastScrollY`）随文件恢复 |
| **文件历史** | 最近 15 个打开记录，一键重开（持久化 `history.json`） |
| **YAML Front Matter** | Markdown 头部的 YAML 元信息渲染为信息卡片 |
| **窗口状态记忆** | 尺寸 / 位置 / 最大化状态持久化（`window.json`） |
| **拖拽 & 命令行** | 支持拖入文件打开；支持启动时传入文件路径参数 |
| **多窗口** | 可同时打开多个独立窗口，全部关闭后退出 |
| **pandoc 导出** | 一键导出 docx / pdf / latex / html / epub / markdown；白色圆角对话框 + 自定义保存路径 |
| **安全加固** | 多级纵深防御：Markdig 禁用原始 HTML、全链路输出转义、CSP nonce 脚本白名单、文档虚拟主机单文件白名单、file: 导航拦截、外链 scheme 白名单、远程图片按文档授权加载（默认拦截 + no-referrer） |

---

## 🧩 支持的文档类型

| 类别 | 扩展名 | 渲染方式 | 回退 |
|------|--------|----------|------|
| 📝 Markdown | `.md` `.markdown` `.mkd` `.mdown` | Markdig 直渲（主题化 CSS） | — |
| 📄 Word | `.docx` `.doc` `.docm` `.rtf` `.odt` | anydoc-wasm → Markdown → Markdig | OpenXML 解析 |
| 📊 Excel | `.xlsx` `.xls` `.xlsm` `.ods` `.csv` | anydoc-wasm → Markdown → Markdig | OpenXML 解析（`.xls` 仅 OpenXML） |
| 📽 PowerPoint | `.pptx` `.ppt` `.odp` | anydoc-wasm → Markdown → Markdig | OpenXML 解析 |
| 📚 电子书 | `.epub` | anydoc-wasm → Markdown → Markdig | 错误页 |
| 📕 PDF | `.pdf` | PDF.js 整页渲染（文本层提取、缩放、双栏同步） | — |

> **说明**：
> - **anydoc-wasm**：页内 WebAssembly 转换器，覆盖 Office 12 种格式；失败或 5s 超时自动回退右侧列。
> - **宏文档变体**：`.docm` / `.xlsm` 含宏，仅解析内容不执行宏，映射到 docx/xlsx 解析器。

---

## 📁 项目结构

```
SeeMe/
├── src/                           # 全部源码
│   ├── App.xaml / App.xaml.cs     # 应用入口，多窗口管理，启动参数打开文件
│   ├── MainWindow.xaml(.cs)       # 主窗口：双栏、搜索、缩放、自动刷新、拖拽
│   ├── RenderService.cs           # Markdown → 主题化 HTML、Office/PDF 页面模板、CSP/消毒
│   ├── FileConverter.cs           # docx / xlsx / pptx / pdf → HTML
│   ├── ThemeManager.cs            # 亮/暗主题 ResourceDictionary 构建与持久化
│   ├── FileHistory.cs             # 最近文件历史（持久化）
│   ├── PanelState.cs              # 单面板状态（文件、监视器、防抖、滚动、缩放）
│   ├── PdfTextCache.cs            # PDF 文本层提取结果缓存
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

1. 启动后通过菜单「文件 → 打开」选择文档，或直接**拖拽文件**到窗口。
2. 命令行打开指定文件：
   ```bash
   SeeMe.exe path/to/document.md
   ```
3. 顶部工具栏可切换**双栏分屏**、调整**缩放**、开启**自动刷新**、切换**明/暗主题**。

---

## 🛠 构建

要求：Windows + [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) + WebView2 运行时。

```bash
# 还原依赖并构建
dotnet build -c Release

# 单文件自包含发布（win-x64，含运行时）
dotnet publish -c Release -p:PublishSingleFile=true -r win-x64
```

生成物位于 `bin/Release/net8.0-windows/`。

---

## 📜 许可证

基于 **MIT 许可证** 开源，详见仓库根目录 `LICENSE` 文件。
