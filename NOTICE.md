# 致谢与来源说明

StillTouch 是一个面向“单指静止触摸转鼠标点击”场景的精简衍生项目，致敬并基于 [luojunyuan/TouchChanX](https://github.com/luojunyuan/TouchChanX) 发展而来。

TouchChanX 提供了 Windows 输入处理、`SendInput` 与触摸手势识别相关的代码基础和实现参考。StillTouch 在此基础上重新收敛产品目标，移除了游戏启动和复杂 UI，并在 1.2 版将早期低级鼠标 Hook/Raw Input 方案替换为独立的全屏透明触摸捕获层，增加了单指状态机、多显示器坐标处理、管理员托盘程序、单文件发布和针对异常输入序列的安全释放。

透明捕获层的方向也参考了 TouchMousePointer（现为 Tablet Pro 组件）公开帮助中关于“全屏模式使用覆盖整个屏幕的透明窗口、而非鼠标驱动”的说明。StillTouch 未包含、反编译或复制 Tablet Pro / TouchMousePointer 的专有代码。

感谢 TouchChanX 作者及贡献者以开放源码形式分享工作。原项目和 StillTouch 均依据 MIT License 使用与分发；本仓库保留上游 Git 历史以便追溯来源。
