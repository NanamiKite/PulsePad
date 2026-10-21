# make yuou 手柄 like jumpegg

## 这是个啥玩意

大概十年前看到某知名up的妙妙视频，一直想试试，感谢gpt大人生产了这坨AIslop
不得不说xbox手柄的x轴线性马达比几块钱的转子马达手感好多了（


![PulsePad 界面](images/dashboard.jpg)



## 咋用
把 release 的 ZIP 解压到任意目录，双击里面的 `PulsePad.exe` 就行。旁边的 `native` 文件夹要保留，SDL 兼容库已经带了，不用另外安装驱动。

## 网页版

现在也有纯静态网页版，文件在 `docs/`，可以直接放 GitHub Pages，不需要服务器或安装依赖。

连接手柄后按一下手柄按键，在页面里选择设备，再点「开始」。支持下面这 16 种模式、两个马达各自的强度、节奏周期、定时和无限播放。打开页面、连接设备和调整待机参数都不会自动震动。音乐和曲目导入仍在桌面版里。

建议用电脑上的 Chrome / Edge，通过 HTTPS 打开。网页用的是浏览器的手柄接口，兼容范围和桌面版的 SDL 不一样；Xbox、PS、Switch 或第三方手柄能不能震动，以页面检测结果和实际响应为准。只识别到按键、没有震动能力时会禁用「开始」，可以试试 USB 连接或桌面版。

**网页版切换标签页、最小化或页面进入后台会停止，回来后需要手动开始。** Esc、已映射手柄的右侧面键、断线、定时结束或输出错误也会停止；网页没有全局快捷键。需要持续后台播放就用桌面版。两个强度滑块对应低频和高频通道，不保证每种手柄都在物理左右位置。

### 放到 GitHub Pages

1. 把包含 `docs/` 的代码提交并推送到 GitHub。
2. 打开仓库 **Settings → Pages**，在 **Build and deployment** 里选 **Deploy from a branch**。
3. 分支选 `master`（如果你改成了 `main` 就选 `main`），目录选 **`/docs`**，保存。
4. 等 GitHub 部署完成，再打开 Pages 显示的网址。本仓库对应 `https://nanamikite.github.io/PulsePad/`，首次使用前需要先开启 Pages。

所有资源使用相对路径，没有 CDN、遥测或外部脚本；`docs/.nojekyll` 让 GitHub 直接发布静态文件。其他支持 HTTPS 的静态托管也可以，发布目录选 `docs` 即可。桌面版 ZIP 不包含这些网页文件。

### 本地运行和验证网页

安装 Node.js 22 或更新版本，在项目目录运行：

```powershell
node tools/serve-web.mjs
```

打开 `http://localhost:4173/PulsePad/`。不要直接双击 HTML；网页使用模块脚本。端口被占用时可运行 `node tools/serve-web.mjs 4174`。

```powershell
node --test tests/web-engine.test.mjs
node tests/web-ui.mjs
```

第二条需要本机有 Chrome / Edge，也可在命令后传浏览器可执行文件的完整路径。它在独立临时配置里运行无界面浏览器，以模拟手柄检查连接、开始/停止、切换到后台、断线、错误提示和 320–1440 像素布局；预览图写入已忽略的 `build/`。不会向真实手柄输出。自动化测试不能代替各型号实机验证。

网页接口依据：[Gamepad 规范](https://w3c.github.io/gamepad/)、[震动输出接口](https://developer.mozilla.org/en-US/docs/Web/API/GamepadHapticActuator/playEffect)、[GitHub Pages 发布目录设置](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site)。

## 16 种模式

| 模式 | 节奏 |
| --- | --- |
| 持续 | 保持设定强度 |
| 呼吸 | 平滑地增强、减弱 |
| 起伏 | 在设定强度的 30%–100% 之间变化 |
| 脉冲 | 半拍震动、半拍暂停 |
| 双击 | 两次短震后停顿 |
| 三连击 | 三次短震后停顿 |
| 心跳 | 一下重、一下轻，再停顿 |
| 短连发 | 四次快速震动，再停顿 |
| 渐强 / 渐弱 | 从弱到强 / 从强到弱循环 |
| 往复 | 匀速增强，再匀速减弱 |
| 阶梯上升 / 阶梯下降 | 分五档增强 / 减弱 |
| 左右交替 | 两侧各震动半拍 |
| 左右交融 | 一侧增强，另一侧减弱 |
| 随机变化 | 每拍改变强度，平滑衔接；预览与播放使用相同的伪随机序列 |
括弧（那个音乐模式当不存在就行
## 手柄兼容范围

以下是桌面版的兼容情况；网页版以浏览器提供的能力为准。

我只试了xbox one的手柄，但是让astra大人加了兼容层所以能不能用难说

PS3、特殊转接器和仿制手柄依赖其 Windows 驱动及 SDL 支持，不能保证所有型号可用。普通 Joy-Con 充电握把不会自动提供 USB 手柄数据连接。程序不安装驱动、不修改蓝牙配对，也不自动创建虚拟 Xbox 手柄。


## 源码和验证

在项目目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```
构建后运行 `PulsePad.exe`。便携包在 `build\PulsePad-Windows.zip`，解压后保留旁边的 `native` 文件夹。ZIP 仅包含程序、手柄兼容库和许可证。MIDI 可直接导入；MP3 / WAV 导入需要系统安装 FFmpeg。

跑不起来就把报错扔给豆包问：豆包豆包，为什么这个报错，然后你豆包姐就会给你最直白，最不绕弯子的回答（


实现依据：[Microsoft XInputSetState](https://learn.microsoft.com/en-us/windows/win32/api/xinput/nf-xinput-xinputsetstate)、[XINPUT_VIBRATION](https://learn.microsoft.com/en-us/windows/win32/api/xinput/ns-xinput-xinput_vibration)、[SDL 震动接口](https://wiki.libsdl.org/SDL3/SDL_RumbleJoystick)、[SDL 设备能力](https://wiki.libsdl.org/SDL3/SDL_GetJoystickProperties)、[PS 蓝牙增强报告](https://wiki.libsdl.org/SDL3/SDL_HINT_JOYSTICK_ENHANCED_REPORTS)、[SDL 官方发行版](https://github.com/libsdl-org/SDL/releases/tag/release-3.4.18)。

## 许可证

本项目采用 [GNU GPL v3.0](LICENSE)（`GPL-3.0-only`）。

SDL 保留其[原许可证](vendor/SDL/LICENSE.txt)。
