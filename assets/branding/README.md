# Melvune 应用图标

2026-10-10：用户从 A 青绿音符、B 紫色唱片、C 黑白飞牛音符中选择 C。
使用内置 imagegen 生成，透明 PNG 原图保存在 melvune.png；melvune.ico 仅做
Windows 格式转换，包含 16/24/32/48/64/128/256 像素版本。

用于主应用/便携启动器 EXE、窗口/任务栏、标题栏品牌、安装器和开始菜单
快捷方式。预览方案不作为官方飞牛标志，不表示官方关联或背书。

生成提示词：

Use case: logo-brand. Create ONE production-ready Windows desktop app icon for
Melvune, an unofficial NAS and local music player with a clean monochrome WinUI
interface. Variant C: a charcoal-black rounded square tile, with a bold ivory-white
original abstract symbol combining two upward swept bull horns, a musical note,
and a gentle wing-like sweep. Extremely simple geometric silhouette, elegant and
friendly rather than aggressive, no literal detailed animal face. Thick, clear
negative spaces and instantly legible at 16 and 32 pixels. Front facing flat icon,
no perspective, no letters, no text, no grid, no mockup, no watermark. Center the
rounded tile filling 88% of a square canvas with true transparent outer corners
and crisp edges. Must be original, do not copy the official fnOS or any existing logo.

## 飞牛来源标志

`fnos-music-source.png` 为用户 NAS 飞牛音乐公开静态资源 `/music/static/assets/img/logo.png`（2026-10-16，未使用登录凭据）。只用于标识飞牛同步歌单，原图未改绘，使用 20/30px 图标、圆角衬底与统一边距融入 WinUI。标志归原权利人所有，不作为本客户端应用图标，不表示官方关联或背书。

rc.7 保留此官方原图，在应用中后台解码并按亮度转换为中性灰，精确保留原图透明度；共享转换后的图像源，以低透明度放在歌单名称背景，不替代本客户端应用图标。未采用图像生成的轮廓变形版本。
