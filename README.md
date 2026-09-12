# iQing 校园网自动登录

一个面向 Windows 10/11 的 iQing 校园网自动登录工具。首次运行时输入账号和密码，软件会把凭据使用 Windows DPAPI 机器级加密保存，并安装一个开机自动启动的 Windows 服务。

## 功能

- 首次运行图形界面输入账号和密码，之后无需再次输入。
- 服务在 Windows 用户登录前启动，等待 WLAN 和 `iQing` 连接后自动认证。
- 认证请求从 WLAN 网卡直连，避免系统代理影响校园网门户。
- 账号密码保存在 `C:\ProgramData\CampusAutoLogin\credential.bin`，目录仅允许 SYSTEM 和本机管理员访问。
- 安装时创建全用户 `iQing` Wi-Fi 配置；安装新版本时会移除旧版计划任务，避免重复运行。
- 支持“立即检测”和卸载。

## 安装使用

从 `dist` 下载 ZIP，解压后运行 `iQing校园网自动登录.exe`，同意一次管理员授权，输入本机对应的校园网账号和密码，点击“保存并安装”。每台电脑、每个账号都需要单独配置。

首次运行没有商业代码签名证书，Windows SmartScreen 可能显示“未知发布者”；确认来源后选择运行即可。

## 构建

本项目使用 Windows 自带的 .NET Framework C# 编译器，不需要安装 Visual Studio：

```powershell
.\build.ps1
```

输出位于 `dist\iQing校园网自动登录-v1.0\`，同时生成 ZIP 和 SHA-256 校验文件。

## 门户适配范围

当前版本针对以下门户实现：

- Wi-Fi SSID：`iQing`
- 认证门户：`http://10.1.7.3/eportal/`
- 登录接口：`http://10.1.7.3/eportal/InterFace.do?method=login`

不同学校的认证系统、字段、加密方式和动态参数可能不同，不能直接套用。

## 开机前认证的边界

Windows 服务可以在用户登录前启动，但实际认证仍取决于无线网卡驱动、WLAN 服务启动顺序和 `iQing` 信号是否已经可用。因此软件会持续轮询，通常能在 Windows 登录页面阶段完成；无法保证每台电脑在开机画面出现的同一瞬间已经联网。

## 安全说明

本项目不包含任何个人账号、密码或动态 `userIndex`。密码不会写入源码、安装包或日志。服务以 LocalSystem 运行，因此安装需要管理员权限；机器级 DPAPI 的安全边界是本机管理员和 SYSTEM。

## 许可证

MIT License，见 [LICENSE](LICENSE)。
