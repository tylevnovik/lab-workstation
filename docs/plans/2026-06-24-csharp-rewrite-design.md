# 课题组工作站 C# WinForms 重写设计

> 将现有 PowerShell 脚本(Lab-TrayApp.ps1 / Manage-LabAccounts.ps1 / Lab-Monitor.ps1)整体重写为 .NET 8 WinForms / Worker Service 应用。

## 1. 目标与范围

| 原 PS 脚本 | C# 项目 | 类型 | 说明 |
|---|---|---|---|
| Lab-TrayApp.ps1 (~900行) | LabWorkstation.TrayApp | WinForms exe | 用户端悬浮导航 + 系统托盘 |
| Manage-LabAccounts.ps1 (~2400行) | LabWorkstation.Admin | WinForms exe | 管理员端账户管理(7 Tab) |
| Lab-Monitor.ps1 (~670行) | LabWorkstation.Monitor | Worker Service exe | 后台资源监控守护 |
| (共享逻辑) | LabWorkstation.Common | 类库 | 配置/审计/账户/通知/存储 |

- **目标 .NET**:net8.0(WinForms 支持完善,LTS)
- **功能策略**:1:1 完整保留全部功能
- **部署形态**:两个独立 WinForms exe + 一个 Windows Service

## 2. 工程结构

```
LabWorkstation.sln
├── LabWorkstation.Common/        类库(net8.0) — 共享内核
│   ├── Configuration/LabConfig.cs        路径常量、阈值、分类目录
│   ├── Audit/AuditLogger.cs              WriteAuditLog + RotateAuditLog
│   ├── LocalAccounts/                    本地用户/组操作(PrincipalContext)
│   │   ├── AccountManager.cs             创建/禁用/启用/改密/重置
│   │   ├── GroupManager.cs               导师组创建/删除/成员增删
│   │   └── NtfsAclHelper.cs              目录权限隔离
│   ├── Notifications/NotificationStore.cs 通知 JSON 读写(pending/sent)
│   └── Storage/FolderSizer.cs            文件夹大小计算、归档复制
│
├── LabWorkstation.TrayApp/       WinForms exe — 用户端
│   ├── FloatingWidget.cs         悬浮窗(置顶/拖拽/无标题栏/自绘圆角)
│   ├── TrayIconContext.cs        托盘图标 + 右键菜单
│   ├── Dialogs/                  SelfServiceDialog / NoticePopup / NotificationPopup
│   └── NotificationPoller.cs     30s 轮询 pending 通知
│
├── LabWorkstation.Admin/         WinForms exe — 管理员端
│   ├── MainForm.cs               7 Tab 容器
│   ├── Tabs/                     Members / CreateAccount / GroupManage / Performance
│   │                             / Batch / Departure / Broadcast
│   └── Dialogs/                  重置密码 / 修改分组 / 新建导师组
│
└── LabWorkstation.Monitor/       Worker Service exe — 后台守护
    ├── MonitorWorker.cs          60s 循环主逻辑
    ├── Metrics/                  Cpu / Memory / Disk / Gpu / LongRunning / Sessions
    └── Logging/MonitorLogger.cs  日志 + 轮转 + 告警冷却
```

## 3. 关键技术映射

### 3.1 本地账户/组操作

| 原 PS | C# (System.DirectoryServices.AccountManagement) |
|---|---|
| `net localgroup X /add` | `GroupPrincipal.Save()` |
| `New-LocalUser` / `Get-LocalUser` | `UserPrincipal` + `PrincipalContext` |
| 改密 `net user` / `Set-LocalUser -Password` | `UserPrincipal.SetPassword()` |
| `[ADSI]"WinNT://./X,group"` 成员 | `GroupPrincipal.GetMembers()` |
| `Enable/Disable-LocalUser` | `UserPrincipal.AccountDisabled` |
| `Set-Acl` NTFS | `DirectorySecurity.AddAccessRule()` |

### 3.2 Monitor 指标采集

| 指标 | C# 方案 |
|---|---|
| CPU 总体 | `ManagementObjectSearcher` Win32_Processor.LoadPercentage |
| 进程 CPU delta | `Process.TotalProcessorTime` 采样差值 / (窗口×核数) |
| 内存 | Win32_OperatingSystem via WMI |
| 磁盘 | Win32_LogicalDisk (DriveType=3) |
| GPU | `nvidia-smi.exe` 解析 CSV(与原逻辑一致) |
| 长时进程 | Win32_Process + GetOwner() via ManagementObject |
| 活跃会话 | `query user` 进程输出解析 |

## 4. 错误处理

- Common 层账户/组/ACL 操作抛 `LabOperationException(Action, Target, Detail)`。
- Admin/TrayApp:catch → 写审计日志 + UI 提示(对应原 `Write-AuditLog` + `Show-Dialog`)。
- Monitor:采集异常写 WARN 日志,不中断主循环(外层 try/catch)。
- 日志轮转:Common 提供 `RotatingFileWriter`(按大小切分,保留 N 份)。

## 5. 部署

- **Admin.exe**:`app.manifest` 声明 `requireAdministrator`,双击提权。
- **TrayApp.exe**:部署到 `shell:startup` 开机启动,普通用户身份。
- **Monitor Service**:`sc create LabMonitor binPath=... start=auto obj=LocalSystem`。
- 配置常量集中到 `LabConfig.cs`,改路径只动一处。

## 6. 路径约定(保持与原 PS 一致)

- 共享数据:`D:\GroupData`
- 公共区:`D:\GroupData\_公共`
- 个人目录根:`D:\Users`
- 全员组:`Lab_All`,导师组:`Lab_[导师名]`
- 审计日志:`D:\GroupData\_公共\_使用手册\admin_operations.log`
- 监控日志:`D:\GroupData\_公共\_使用手册\system_monitor.log`
- 通知:`D:\GroupData\_公共\_notifications\{pending,sent}`
- 导师组分类:`01_人才类数据` … `99_归档`
