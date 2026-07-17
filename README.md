# StillTouch

StillTouch 是一个 Windows 全局单指触摸转鼠标工具。只有确认属于“单指基本静止”的触摸操作才会转换：轻点对应鼠标左键，长按对应鼠标右键；拖动、多指手势、手写笔和真实鼠标保持原样。

> StillTouch 致敬并基于 [luojunyuan/TouchChanX](https://github.com/luojunyuan/TouchChanX) 的 Win32 输入处理基础发展而来。感谢原作者公开项目与实现思路。详细说明见 [NOTICE.md](NOTICE.md)。

## 下载与使用

从 [GitHub Releases](https://github.com/Zephyr333/StillTouch/releases/latest) 下载 `StillTouch.exe`。这是 Windows x64 自包含单文件程序，不需要安装 .NET，也不需要把 DLL 或配置文件放在旁边。

1. 双击 `StillTouch.exe`，确认管理员权限提示。
2. 功能默认开启，程序常驻系统托盘。
3. 左键单击托盘图标可关闭或重新开启功能。
4. 右键单击托盘图标并选择“退出”可完全退出。

如果启动失败，程序会在 EXE 同目录生成 `StillTouch-startup.log`；仅当该目录不可写时才回退到 `%TEMP%`。

## 行为

- 单指静止轻点：屏蔽触摸派生点击，在触点坐标注入一次普通鼠标左键点击。
- 单指静止长按：屏蔽触摸派生右键，在触点坐标注入一次普通鼠标右键点击。
- 单指明显移动：取消点击转换，让拖动继续。
- 双指/多指、滚动、缩放、旋转、手写笔、真实鼠标和无法确认的输入：不转换。
- 支持多显示器、负坐标和不同 DPI 缩放。
- 注入输入带有专用标记，不会被程序自身重复捕获。

## 构建

需要 Windows 和 .NET 10 SDK：

```powershell
dotnet build .\StillTouch.slnx -c Release -p:Platform=x64
dotnet publish .\StillTouch\StillTouch.csproj -c Release -r win-x64 --self-contained true -o .\EXE
```

输出文件为 `EXE\StillTouch.exe`。

## 测试

```powershell
dotnet run --project .\StillTouch.Tests\StillTouch.Tests.csproj -c Release
```

自动测试覆盖轻点、长按、拖动、多指取消、笔/鼠标识别、虚拟桌面负坐标、异常释放和原生窗口过程转发。

建议在目标触摸设备上人工验证：桌面与窗口客户区轻点、长按、超过阈值的拖动、双指滚动/缩放、手写笔和真实鼠标。

## 已知边界

- 支持普通 Windows 桌面，包括普通程序、非客户区和管理员程序。
- Windows 登录界面、锁屏和 UAC 安全桌面无法操作。
- 少数完全自行接管原始触摸且不产生鼠标兼容输入的程序可能无法转换。
- `SendInput` 受 Windows UIPI 约束，因此程序默认申请管理员权限。

## 许可证与致谢

StillTouch 使用 [MIT License](LICENSE.txt)。项目保留了 TouchChanX 的 Git 历史，并在 [NOTICE.md](NOTICE.md) 中说明来源与致谢。
