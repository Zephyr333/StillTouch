# StillTouch

StillTouch 是一个 Windows 全局触摸转鼠标工具。启用后用覆盖整个虚拟桌面的透明触摸捕获层接管手指输入，通过 Windows 10/11 的 `WM_POINTER` 或兼容 `WM_TOUCH` 路径识别接触：轻点对应鼠标左键，长按对应鼠标右键，单指移动对应普通鼠标拖动。手写笔和真实鼠标不经过转换。

> StillTouch 致敬并基于 [luojunyuan/TouchChanX](https://github.com/luojunyuan/TouchChanX) 的 Win32 输入处理基础发展而来。感谢原作者公开项目与实现思路。详细说明见 [NOTICE.md](NOTICE.md)。

## 下载与使用

从 [GitHub Releases](https://github.com/Zephyr333/StillTouch/releases/latest) 下载 `StillTouch.exe`。这是 Windows x64 自包含单文件程序，不需要安装 .NET，也不需要把 DLL 或配置文件放在旁边。

1. 双击 `StillTouch.exe`，确认管理员权限提示。
2. 功能默认开启，程序常驻系统托盘。
3. 左键单击托盘图标可关闭或重新开启功能。
4. 右键单击托盘图标并选择“退出”可完全退出。

程序会在 EXE 同目录生成 `StillTouch.log`，记录启动、开关、退出以及运行期输入注入错误；仅当该目录不可写时才回退到 `%TEMP%`。

## 行为

- 单指静止轻点：抬起时立即在触点坐标注入一次普通鼠标左键点击；原始触摸由透明捕获层消费，不再同时产生原生触摸点击。
- 单指静止长按：持续约 450 毫秒后直接在触点坐标注入一次普通鼠标右键点击，不依赖目标软件或 Windows 是否会把长按提升为右键。
- 单指明显移动：超过阈值后按下鼠标左键并跟随触点移动；抬起、第二根手指加入、输入异常、停用和退出都会显式释放左键。
- 双指/多指：立即取消当前单指转换；启用期间不向底层程序转发原生多指手势。
- 手写笔和真实鼠标：透明穿过捕获层，不转换。
- 每次触摸按下都会建立新的独立序列，同一位置快速连续轻点可以形成正常的双击或连续点击。
- 支持多显示器、负坐标和不同 DPI 缩放。
- 鼠标输入与触摸输入走完全分离的消息路径，转换后的输入不会被自身再次捕获。

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

自动测试覆盖轻点、主动长按、同位置连续轻点、完整拖动、仅在抬起时检测到的拖动、多指取消与释放、异常重置释放、负坐标和虚拟桌面坐标归一化。

建议在目标触摸设备上人工验证：桌面、任务栏、开始菜单和窗口客户区轻点；不会自行响应触摸长按的软件；同位置快速双击；超过阈值的拖动和抬起；触摸托盘图标后再次操作；手写笔和真实鼠标。

## 已知边界

- 程序默认申请管理员权限。全屏顶置透明捕获层会先于普通窗口、任务栏和开始菜单接收触摸，因此这些区域不会再同时响应原生触摸；鼠标注入直接以 `SendInput` 的真实结果为准。
- Windows 登录界面、锁屏、UAC 安全桌面以及高于本程序完整性级别的进程无法操作。`SendInput` 受 Windows UIPI 约束，只允许向同级或更低完整性级别注入输入。
- 某些反作弊、反注入或独占输入程序会主动忽略 `SendInput`，便携式用户态工具无法保证绕过这些策略。
- 独占全屏游戏、Windows 安全桌面以及位于透明捕获层之上的受保护系统表面不在便携式用户态程序的保证范围内。真正的系统级全局指针重定向需要签名安装并启用 `uiAccess`，当前单文件版未启用。
- 启用期间多指滚动、缩放和旋转会被捕获层消费，不会转发给底层软件；这是当前“只保留鼠标操作”版本的预期行为。
- 程序停用或退出时先注销并隐藏触摸捕获层，再延迟释放托盘资源；不再安装可能在托盘回调中重入的全局鼠标 Hook。

## 许可证与致谢

StillTouch 使用 [MIT License](LICENSE.txt)。项目保留了 TouchChanX 的 Git 历史，并在 [NOTICE.md](NOTICE.md) 中说明来源与致谢。
