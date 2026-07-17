# StillTouch

StillTouch 是一个 Windows 全局单指触摸转鼠标工具。只有确认属于“单指基本静止”的触摸操作才会转换：轻点对应鼠标左键，长按对应鼠标右键；拖动、多指手势、手写笔和真实鼠标保持原样。

> StillTouch 致敬并基于 [luojunyuan/TouchChanX](https://github.com/luojunyuan/TouchChanX) 的 Win32 输入处理基础发展而来。感谢原作者公开项目与实现思路。详细说明见 [NOTICE.md](NOTICE.md)。

## 下载与使用

从 [GitHub Releases](https://github.com/Zephyr333/StillTouch/releases/latest) 下载 `StillTouch.exe`。这是 Windows x64 自包含单文件程序，不需要安装 .NET，也不需要把 DLL 或配置文件放在旁边。

1. 双击 `StillTouch.exe`，确认管理员权限提示。
2. 功能默认开启，程序常驻系统托盘。
3. 左键单击托盘图标可关闭或重新开启功能。
4. 右键单击托盘图标并选择“退出”可完全退出。

程序会在 EXE 同目录生成 `StillTouch.log`，记录启动、开关、退出以及运行期输入注入错误；仅当该目录不可写时才回退到 `%TEMP%`。

## 行为

- 单指静止轻点：抬起时立即在触点坐标注入一次普通鼠标左键点击，并屏蔽同一触摸序列随后产生的兼容鼠标点击。
- 单指静止长按：持续约 450 毫秒后直接在触点坐标注入一次普通鼠标右键点击，不依赖目标软件或 Windows 是否会把长按提升为右键。
- 单指明显移动：取消点击转换，让拖动继续。
- 双指/多指、滚动、缩放、旋转、手写笔、真实鼠标和无法确认的输入：不转换。
- 每次触摸按下都会建立新的独立序列，同一位置快速连续轻点可以形成正常的双击或连续点击。
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

自动测试覆盖轻点、主动长按、拖动取消、多指取消、同位置连续轻点、原始触摸与兼容鼠标去重、笔/鼠标识别、虚拟桌面负坐标、异常释放和原生窗口过程转发。

建议在目标触摸设备上人工验证：桌面与窗口客户区轻点、不会自行响应触摸长按的软件、同位置快速双击、超过阈值的拖动、双指滚动/缩放、手写笔和真实鼠标。

## 已知边界

- 程序默认申请管理员权限，可覆盖普通程序、非客户区以及大多数管理员程序；不再因无法查询目标进程而提前拒绝注入，而是直接以 `SendInput` 的真实结果为准。
- Windows 登录界面、锁屏、UAC 安全桌面以及高于本程序完整性级别的进程无法操作。`SendInput` 受 Windows UIPI 约束，只允许向同级或更低完整性级别注入输入。
- 某些反作弊、反注入或独占输入程序会主动忽略 `SendInput`，便携式用户态工具无法保证绕过这些策略。
- Windows 没有供普通用户态程序安全使用的“全局屏蔽所有原始触摸”Hook。StillTouch 会屏蔽触摸派生的兼容鼠标点击，但完全自行消费 `WM_POINTER`/Raw Input 的触摸优先程序仍可能同时收到原始触摸。
- 程序退出时先停用触摸监视并卸载鼠标 Hook，再释放托盘资源；若系统输入序列已经被第三方驱动或程序破坏，仍建议先用键盘结束相关程序。

## 许可证与致谢

StillTouch 使用 [MIT License](LICENSE.txt)。项目保留了 TouchChanX 的 Git 历史，并在 [NOTICE.md](NOTICE.md) 中说明来源与致谢。
