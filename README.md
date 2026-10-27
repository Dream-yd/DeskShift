# Meeting Desktop 会议桌面

一个 Windows 桌面切换工具：为会议、录屏或演示准备独立的桌面文件夹，并尝试保存和恢复原来的图标排列。

**当前状态：开发预览。** 自动测试覆盖了模拟切换与恢复；在单屏 Windows 11 虚拟机中，已验证多次真实切换、图标位置恢复和部分异常恢复。动态壁纸、多屏、混合缩放、切换中途崩溃及默认 UAC 交互仍需更多实机验收。图标、权限或显示器状态变化时，恢复可能不完整。请先备份重要文件与布局，在可恢复的测试环境中试用。

[English](README.en.md) · [使用与恢复说明](docs/USAGE.zh-CN.md) · [开发与测试](docs/DEVELOPMENT.md) · [第三方声明](THIRD-PARTY-NOTICES.md)

## 功能

- 在原桌面与备用文件夹之间切换，返回时按图标身份及位置尝试恢复。
- 通过软件页面、全局快捷键、桌面中键双击或系统托盘操作；快捷方式可以关闭或更改。
- 保存和查看图标布局，按需保留自动快照；当前恢复所需的快照受到保护。
- 中英文、深浅色和跟随系统主题；可选液态玻璃皮肤、切换提示及过渡。
- 提供恢复和诊断入口；遇到无法确认的状态时保留记录，提示人工处理。

## 使用前

个人桌面文件保留在各自文件夹中；软件通过更改 Windows 桌面文件夹位置实现切换。公共桌面的 `.lnk` 快捷方式可能需要管理员授权来暂存与恢复。授权提示的时机取决于是否已有有效授权，并非保证每次切换都会弹窗。

1. 备份重要桌面文件，确认知道原桌面文件夹的位置，并在软件中保存一份布局。
2. 在设置中选择独立的备用文件夹；不要使用原桌面或其子目录。
3. 按需选择提前授权，并在“桌面切换”页操作；切回原桌面后核对图标排列。
4. 出现异常时打开“恢复与修复”或“诊断”。不要自行删除恢复记录或自动快照。

响应优先模式尝试不重启 Explorer；兼容模式可能重启 Explorer 并关闭资源管理器窗口。软件默认数据位于 `%LOCALAPPDATA%\MeetingDesktop`，可通过设置入口导出恢复备份。详见[使用与恢复说明](docs/USAGE.zh-CN.md)。

## 安装与构建

从 [GitHub Releases](https://github.com/Dream-yd/MeetingDesktop/releases) 下载 Windows x64 预览包；解压后保留整个文件夹，运行 `MeetingDesktop.exe`。自包含包不需要另装 .NET 运行时。

从源码构建需要 Windows、.NET 10 SDK 和网络可访问的 NuGet 源：

```powershell
dotnet restore MeetingDesktop.sln
dotnet build MeetingDesktop.sln -c Release --no-restore
dotnet test MeetingDesktop.sln -c Release --no-build
```

测试包含在独立窗口上运行的 Windows 原生测试；完整真实桌面往返不包含在自动化测试中。发布包为多文件自包含部署，不要把 EXE 单独移出文件夹。

## 许可证

项目许可证为 [MIT](LICENSE)。液态玻璃实现参考了第三方项目的光学思路；详细归属和许可证见[第三方声明](THIRD-PARTY-NOTICES.md)。
