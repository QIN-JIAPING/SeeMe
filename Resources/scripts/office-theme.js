// 办公页主题切换：与 BuildOfficePage 配套的外链脚本（冗余/扩展位）。
// BuildOfficePage <head> 已内联定义同名 window.setTheme 作主兜底，本文件作为冗余：
// 双套定义确保 window.setTheme 总是存在，避免外链加载竞态/opaque-origin CSP 边缘
// 行为导致 setTheme 未定义、被 PushThemeAsync 静默吞掉。逻辑用 classList.toggle 双向对称。
window.setTheme = function (dark) {
    var d = !!dark;
    document.documentElement.classList.toggle('dark', d);
    if (document.body) document.body.classList.toggle('dark', d);
};