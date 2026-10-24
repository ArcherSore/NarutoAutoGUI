# NarutoAutoGUI 用户使用手册

使用带游戏资源的 MaaNOP 完整包，运行其中的 `NarutoAutoGUI.exe`，按提示允许管理员权限。
首次完成下面的配置后，日常只需选择配置、准备运行环境、开始任务。

## 账户和密码填在哪里

| 要填写的内容 | 在哪里填写 | 用途 |
| --- | --- | --- |
| Windows 账户及账户密码 | 设置 → 桌面分身 → 自动登录 Windows 账户 → 保存或更新 | 自动登录桌面分身 |
| QQ 密码或扫码授权 | QQ 游戏大厅自己的登录窗口 | 首次登录、重新授权，并由大厅保存登录状态 |
| QQ 数字账号 | 任务配置 → 启动阶段 → 自动登录 QQ 号 → QQ 号 | 指定要启动的游戏账号 |
| 大厅程序路径 | 任务配置 → 启动阶段 → QQ 游戏大厅路径 → QQGame.exe 路径 | 找到本机安装的大厅 |

**Windows 密码与 QQ 密码分别在对应窗口输入。Windows 登录处需要账户密码，不能填 PIN。**
NarutoAutoGUI 将 Windows 凭据保存在 Windows 凭据管理器中；QQ 登录状态由腾讯客户端保存。

## 首次配置

### 保存 Windows 分身登录密码

打开“设置 → 桌面分身 → 自动登录 Windows 账户”，点击“保存或更新”，填写当前 Windows 账户：

| 账户类型 | 账户填写方式 | 密码 |
| --- | --- | --- |
| 本地账户 | `.\用户名` 或 `计算机名\用户名` | 本地账户密码 |
| Microsoft 账户 | `MicrosoftAccount\邮箱` | Microsoft 账户密码 |

不知道本地账户名称时，可以在 PowerShell 运行 `whoami` 查看。
保存时不验证密码，需点击“准备运行环境”，确认分身能成功登录；Windows 密码更改后在这里重新保存。

### 配置 QQ 游戏大厅自动启动

以下步骤适用于启动阶段提供“启动QQ大厅”的资源包。

1. 安装 QQ 游戏大厅，在大厅中手动登录要使用的 QQ 号，完成首次授权或验证码，并保留该账号的登录状态。
2. 按[附录](#附录-注册-qq-大厅启动任务)注册一次 `QQGame-SavedAccount` 计划任务。
   每台电脑、每个 Windows 用户各配置一次，同一任务可以用于多个已保存登录状态的 QQ 号。
3. 在 NarutoAutoGUI 中选择或新建一份任务配置，打开“启动阶段”，开启“启动QQ大厅”。
4. 填写自己的 **QQ 数字账号**，例如 `123456789`；填写大厅 `QQGame.exe` 的完整路径，输入框中不加引号。
   这里的路径与注册计划任务时的 `$exe` 必须指向同一个程序。
5. 点击启动阶段的播放图标，悬停提示为“仅执行启动”，确认大厅和游戏能在分身中打开。

保存的登录状态有效时，自动启动大厅无需在分身中登录 QQ。首次授权、授权过期或腾讯要求验证时，
仍需在大厅手动完成登录。换账号时，先让新账号在大厅完成一次登录，再修改配置中的 QQ 号。

**大厅启动与游戏网页登录是两个步骤。** “进入游戏”等任务若要求 QQ 在线，请继续按该任务说明准备。

### 使用旧版独立微端的用户

当前 MaaNOP 发行版请使用 QQ 游戏大厅。若仍使用提供独立微端启动项的旧版资源包，
无需注册上述大厅计划任务；先手动登录微端并勾选“记住密码”，再在对应启动项中填写自己的 QQ 号。

## 每天使用

1. 打开 NarutoAutoGUI，选择要运行的配置，确认账号、大区、区服和任务参数。
2. 点击“准备运行环境”，等待就绪。此按钮只准备分身，不自动启动游戏。
3. 点击“开始任务”。已开启的启动项会先执行，成功后再执行普通任务。
4. 需要查看游戏时点击“观看分身”。需要手动输入时，先“放大”，再点击“只读”切为“可操作”；
   手动操作前先停止正在运行的任务，完成后切回“只读”。
5. 仅隐藏分身窗口会保留游戏和任务。需要彻底结束时，从托盘菜单选择“结束桌面分身”，或正常退出程序；
   这会关闭分身中的游戏和其他程序。

## 常见问题

| 情况 | 处理方式 |
| --- | --- |
| 分身提示 Windows 密码错误 | 核对账户格式和账户密码，不能填 PIN；必要时先用账户密码成功登录 Windows 一次，再保存凭据 |
| 找不到 `QQGame-SavedAccount` | 按附录，用运行前端的同一个 Windows 账户注册任务 |
| 换了大厅安装位置 | 同时修改任务配置中的路径，以及附录 `$exe` 路径，然后重新注册任务 |
| 大厅提示 `21007 / not found ntqq` | 检查任务参数是否为 `QQGAME_UID:$(Arg0)`；若仍失败，先在大厅重新登录目标账号，再重试 |
| 出现验证码或授权过期 | 手动完成腾讯登录验证，再运行启动项；保存登录状态不保证永久免验证 |
| 游戏打开了，但自动任务没动作 | 检查任务要求的游戏界面；关闭 Windows 任务栏的“自动隐藏”，再重试 |
| Worker 断开或其他异常 | 先查看运行状态，勿连续点击启动；在“设置 → 帮助与诊断 → 问题诊断”导出诊断包反馈 |

## 附录 注册 QQ 大厅启动任务

在运行 NarutoAutoGUI 的同一个 Windows 账户下，打开**管理员 PowerShell**。
把第一行改为本机大厅实际安装路径，再复制执行下面整段命令。同名任务已存在时会更新配置，无需填写密码。

```powershell
$exe = 'C:\Program Files (x86)\Tencent\QQGameTempest\QQGame.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw '请修改为实际的大厅安装路径。' }

$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$scheduler = New-Object -ComObject Schedule.Service
$scheduler.Connect()

$definition = $scheduler.NewTask(0)
$definition.Principal.UserId = $user
$definition.Principal.LogonType = 3
$definition.Principal.RunLevel = 1

$settings = $definition.Settings
$settings.Enabled = $true
$settings.AllowDemandStart = $true
$settings.DisallowStartIfOnBatteries = $false
$settings.StopIfGoingOnBatteries = $false
$settings.ExecutionTimeLimit = 'PT1M'
$settings.MultipleInstances = 2

$action = $definition.Actions.Create(0)
$action.Path = $exe
$action.WorkingDirectory = Split-Path -Parent $exe
$action.Arguments = 'QQGAME_UID:$(Arg0) qqgame://enterapp:appid=10190,SourceType=1'

$scheduler.GetFolder('\').RegisterTaskDefinition(
    'QQGame-SavedAccount', $definition, 6, $user, $null, 3
) | Out-Null
```

注册后，日常直接通过 NarutoAutoGUI 的“启动QQ大厅”运行，无需再手动执行命令。
任务使用 `QQGAME_UID` 入口复用大厅保存的登录状态；请保留参数中的 `$(Arg0)`，由启动项传入具体 QQ 号。
