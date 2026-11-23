# CHENMU Browser for Windows

CHENMU Browser 是一款轻量 Windows x64 浏览器，使用 Microsoft Edge WebView2 内核。主页提供 Google、Bing 和 Soumu 搜索选择；Soumu 是直达 DuckDuckGo 第三方结果的简洁入口，并非 CHENMU 自建网页索引，第三方结果页可能包含广告。

安装包与各版本说明请见 [GitHub Releases](https://github.com/nwr8yz74hp-debug/chenmu-browser/releases/latest)。浏览器官网：[CHENMU Browser](https://chenmu-browser-site.cosmic-cast.workers.dev/)。

## 安装与卸载

运行 `CHENMU-Browser-Setup-x64.exe`，即可为当前 Windows 用户安装；默认安装在 `%LOCALAPPDATA%\Programs\CHENMU Browser`，不需要管理员权限。安装器会创建开始菜单入口，桌面快捷方式可在安装过程中选择。可从 Windows“已安装的应用”中卸载；卸载程序不会删除用户的书签和浏览器数据。

运行环境需要 Windows x64、.NET Framework 4.8 和 Microsoft Edge WebView2 Evergreen Runtime。大多数较新的 Windows 设备已具备这些组件；如果网页内核缺失，浏览器会提供运行时安装入口。

## 应用内更新

浏览器启动后会在后台检查公开 GitHub Releases，每 24 小时最多自动检查一次；也可以在右上角“设置与隐私”菜单中手动选择“检查更新”。发现新版本时，浏览器会先询问；确认后会自动下载 Windows x64 安装器、验证 SHA-256、关闭浏览器并静默升级，随后自动重启。更新完成后会弹窗显示该版本更新内容。

自动安装更新仅适用于已安装版本；便携版发现新版本时不会覆盖所在文件夹，请先使用安装器安装后再使用应用内更新。若网络不可用、发布包缺少校验摘要或校验失败，更新会停止，当前版本不会运行未验证的安装包。

更新检查会连接项目的公开 GitHub Releases。它不上传浏览记录、书签或设备标识；GitHub 仍会像接收普通 HTTPS 请求一样看到请求的网络来源。更新发布依赖 GitHub 账号和 Release 权限，更新地址固定在本仓库的最新正式 Release。

当前安装器没有 Authenticode 代码签名证书，Windows 可能显示“未知发布者”提示。SHA-256 用于确认文件没有被意外损坏或替换，不等同于发布者身份签名；更新信任边界仍是 GitHub 账号、GitHub HTTPS 和公开 Release 元数据。

## 轻量日常操作

- `Ctrl+L`：定位并选中地址栏；`Ctrl+D`：收藏当前网页。
- `Ctrl+R` 或 `F5`：刷新；`Alt+←` / `Alt+→`：前进或后退。
- `Ctrl+T` 新建标签页；`Ctrl+W` 关闭当前标签；`Ctrl+Tab` / `Ctrl+Shift+Tab` 切换标签。
- `Esc`：停止加载；在地址栏可恢复当前网址，在起始页搜索框可清空内容。
- `Ctrl+Shift+Delete`：打开清理浏览数据确认框。
- 地址栏和起始页搜索框右侧的 `×` 可一键清空；设置菜单可复制当前网址。
- 点击窗口左侧细边即可展开或收起标签栏；展开时为浮层，不会缩小网页可视区域。
- 设置菜单可打开浏览历史、浏览器设置、下载管理和官网；历史记录保存在本机，可设置保留天数。
- 工具栏的下载按钮和设置菜单都可打开下载管理；列表支持查看进度、取消、打开文件或定位文件夹。下载默认保存到系统“下载”目录，重名文件会自动加序号避免覆盖。

密码保存和表单自动填充默认关闭。广告/跟踪域名拦截、省流模式及轻量页面淡入动画可在浏览器设置中调整。历史记录文件保存在本机应用数据目录，CHENMU Browser 不会单独上传该文件；输入的当前查询仍会发送给所选搜索服务。“清除浏览数据”也会清除本机历史。

## 从源码构建

需要 .NET SDK 10.0.404 或更高版本，以及 NSIS 3.x（命令 `makensis`）。在 Windows PowerShell 中运行：

```powershell
.\build-windows.ps1
```

脚本会生成 x64 浏览器文件和当前用户安装器到 `dist`。新版本通过推送与项目版本一致的 `vX.Y.Z` Git tag 触发 GitHub Actions 构建并发布 Release；工作流会附上安装器、便携压缩包、源码压缩包和 SHA-256 清单。
