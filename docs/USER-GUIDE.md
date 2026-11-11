# 用户使用手册

## 一、保存 Windows 凭据

Bunshin 用一个独立的桌面分身运行游戏，分身需要登录你的 Windows 账户。
凭据保存在 Windows 凭据管理器中，不写入配置文件。

1. 打开“设置 → 桌面分身 → 自动登录 Windows 账户”，点击“保存或更新”。
2. 按下表填写**账户**和**账户密码**，**不能填 PIN**：

   | 账户类型 | 账户填写方式 | 密码 |
   | --- | --- | --- |
   | 本地账户 | `.\用户名` 或 `计算机名\用户名` | 本地账户密码 |
   | Microsoft 账户 | `MicrosoftAccount\邮箱` | Microsoft 账户密码 |

   不知道**本地账户名称**时，在 PowerShell 运行 `whoami` 查看。
3. 点击“准备运行环境”，确认分身能成功登录。

Windows 密码更改后，在同一位置重新保存。

## 二、配置 QQ 游戏大厅自动启动

以下步骤适用于启动阶段提供“启动QQ大厅”的资源包。
每台电脑、每个 Windows 用户只需配置一次。

### 1. 在大厅中保存登录状态

安装 QQ 游戏大厅，在大厅中手动登录要使用的 QQ 号，完成首次授权或验证码，并保留该账号的登录状态。

### 2. 注册大厅启动任务

按[附录](#附录-注册-qq-大厅启动任务)注册一次 `QQGame-SavedAccount` 计划任务。
同一个任务可以用于多个已保存登录状态的 QQ 号。

### 3. 填写任务配置

选择或新建一份任务配置，打开“启动阶段”，开启“启动QQ大厅”，然后填写：

| 要填写的内容 | 位置 | 说明 |
| --- | --- | --- |
| QQ 数字账号 | 自动登录 QQ 号 → QQ 号 | 例如 `123456789`，指定要启动的账号 |
| 大厅程序路径 | QQ 游戏大厅路径 → QQGame.exe 路径 | 完整路径，输入框中不加引号；必须与附录 `$exe` 指向同一个程序 |

## 三、命令行启动

适合让外部脚本或计划任务无人值守地运行指定配置。

### 参数

| 参数 | 作用 |
| --- | --- |
| `--autostart` | 按开机自启处理：窗口留在托盘，并自动执行配置 |
| `-i 名称`、`--instance 名称` | 只配合 `--autostart`：执行指定的配置，名称或配置 ID 均可。找不到或名称重复时不执行，不会擅自跑别的配置。`-i` 后面没有写值（例如脚本里的变量是空的）等同于没有指定，会执行**设置里选的配置** |
| `-q`、`--quit-after-run` | 本次启动实际触发了自动执行时，在任务结束后（无论成功、失败或被停止）结束桌面分身并退出程序；任务根本没开始时不会退出 |
| `-h`、`--help` | 在终端输出参数说明后退出 |

### 推荐用法

```powershell
Bunshin.exe --autostart -i "一键日常" -q
```

## 常见问题

| 情况 | 处理方式 |
| --- | --- |
| 分身提示 Windows 密码错误 | 核对账户格式和账户密码，不能填 PIN；必要时先用账户密码成功登录 Windows 一次，再重新保存凭据 |
| 找不到 `QQGame-SavedAccount` | 按附录，用运行前端的同一个 Windows 账户注册任务 |
| 换了大厅安装位置 | 同时修改任务配置中的路径，以及附录 `$exe` 路径，然后重新注册任务 |
| 大厅提示 `21007 / not found ntqq` | 检查任务参数是否为 `QQGAME_UID:$(Arg0)`；若仍失败，先在大厅重新登录目标账号，再重试 |
| 出现验证码或授权过期 | 手动完成腾讯登录验证，再运行启动项；保存登录状态不保证永久免验证 |
| 命令行启动后没有动作 | 先看托盘提示和日志；确认 `-i` 的配置名称存在且唯一 |

## 附录 注册 QQ 大厅启动任务

在运行 Bunshin 的同一个 Windows 账户下，打开**管理员 PowerShell**。
把第一行改为本机大厅实际安装路径，再复制执行下面整段命令。同名任务已存在时会更新配置。

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
