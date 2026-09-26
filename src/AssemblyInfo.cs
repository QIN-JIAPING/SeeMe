using System.Runtime.InteropServices;
using System.Windows;

// ⚠️ 本文件只保留 SDK **生成不出来** 的程序集特性。
//
// AssemblyTitle / Description / Company / Product / Copyright 以及
// AssemblyVersion / AssemblyFileVersion / AssemblyInformationalVersion
// 全部由 SeeMe.csproj 声明标准属性后、SDK 的 GenerateAssemblyInfo 统一生成。
// **不要在这里重复声明**——C# 不允许同一个 assembly 特性出现两次（会编译报错 CS0579）。
//
// 🔴 版本号唯一来源 = SeeMe.csproj 的 <Version>。要改版本，只改那一处。
// 历史教训：这里曾写死 AssemblyVersion("1.0.1.0")，而 csproj 是 1.0.3，
// 导致 exe 属性、安装器、ZIP 名三者版本不一致，且没有任何编译错误。

[assembly: ComVisible(false)]

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly
)]
