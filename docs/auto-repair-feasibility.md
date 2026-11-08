# 启动失败原因：自动修复可行性验证

本文记录「桌面分身启动失败」各类原因的**可自动化程度**验证结果，供后续实现「一键诊断与修复」功能时参考。

> 状态：**已落地**（v0.2.0 起步，v0.2.4 补齐）。
> v0.2.0：远程桌面被关闭、TermService 被补丁接管、TermService 被禁用/未运行。
> v0.2.4：又补 5 项——ServiceDll 值类型写坏、TermService 服务键被整键覆盖、
> 破解工具的持久化项（开机任务 / Run 自启项）、RDP-Tcp 安全参数被调低、termsrv.dll 被直接打补丁。
> 诊断项合计 12 项环境检查，另有 1 项运行时结论（「密码输入错误，或 Microsoft 帐户凭证已过期」，
> v0.2.6 起，只在连接失败的现场出现）。本文保留为设计依据与「为什么某些项只能手动」的记录。

## 一、结论总览

| # | 原因 | 能否自动检测 | 能否自动修复 | 必须手动的部分 |
|---|---|---|---|---|
| 1 | 远程桌面主机被关闭（`fDenyTSConnections=1`） | ✅ 完全 | ⚠️ **可以，但有前提** | 组策略下发时只能改策略；修复后需重启 TermService（会断开现有会话） |
| 2 | 「仅允许 Windows Hello 登录」 | ✅ 完全 | ⚠️ **能改注册表，但改完不生效** | **必须注销或重启**才生效 |
| 3 | 微软账户本地密码缓存过期 | ✅ 完全（可精确到「密码对不对」） | ⚠️ **有条件**（需要密码） | 没保存密码时只能手动 `runas`；且刷新效果未能验证 |
| 4 | 装过 RDP Wrapper / SuperRDP 等补丁 | ⚠️ 当前实现**漏检**，需放宽判定 | ✅ **可以，且可逆** | 需用户确认（会重启 TermService）；卸载后要清计划任务 |
| 5 | TermService 未运行 / 被禁用 | ✅ 完全 | ✅ 可以 | 无（但同样会打断现有会话） |
| 6 | 账户被禁用 / 密码过期 / 空密码 | ✅ 完全（`LogonUser` 错误码可区分） | ❌ 不能 | 全部手动（改密码、启用账户） |
| 7 | 系统版本过旧（无 Child Session API） | ✅ 完全 | ❌ 不能 | 升级系统 |
| 8 | 安全软件拦截 | ⚠️ 只能按进程名猜 | ❌ 不能 | 手动加白名单 |
| 9 | 组策略禁止保存凭据 | ✅ 完全 | ❌ 不能 | 改域策略 |

一句话概括：**注册表层面的开关能自动改，但「改完要重启才生效」和「需要用户密码」这两类缺口，只能靠引导。**

## 二、本机实测证据

测试环境：Windows 11 专业版，账户为微软账户 `batvbs@qq.com`，当前处于「干净状态」。

```
TermService            : Running / Manual
ServiceDll             : C:\Windows\System32\termsrv.dll（原生，未被劫持）
fDenyTSConnections     : 0（本机）/ 未下发（策略）
DevicePasswordLessBuildVersion : 0（Hello-only 关闭）
RDP-Tcp UserAuthentication     : 1（要求 NLA）
LimitBlankPasswordUse          : 1（空密码禁止网络登录）
会话                    : 0 Services(Disconnected) / 1 Console(Active) / 5 分身(Active)
```

### 2.1 写权限：必须在提权上下文里做

以**非提权**身份打开 TermService 的各类权限句柄（只开句柄、不调用任何修改函数）：

```
查询状态               : 可以
修改配置（改启动类型）  : 失败（错误 5 拒绝访问）
停止服务               : 失败（错误 5 拒绝访问）
启动服务               : 失败（错误 5 拒绝访问）
```

**结论**：自动修复不能交给一个普通权限的外部脚本去跑。本程序清单是 `requireAdministrator`，天然满足；
但如果将来做成「导出修复脚本给用户双击」，必须确保脚本自己提权。

### 2.2 凭据校验 API 可用

用**不存在的账户名**探测（绝不触碰真实账户，避免累计失败次数）：

```
LogonUser              : 按预期失败，错误 1326（用户名或密码错误）
CreateProcessWithLogonW: 失败，错误 5
```

`LogonUser` 返回 1326 说明 API 通路正常，可以直接用来判断「用户保存的密码对不对」——
这比让用户反复试密码有价值得多。

`CreateProcessWithLogonW` 返回错误 5 而非 1326，**未能确认**是 API 本身受限还是当前受限会话所致
（本进程非提权、且运行在受限环境中）。这条需要在实际提权环境下复核，见第三节。

### 2.3 账户类型与 UPN 可读

```
Get-LocalUser          : batvb  PrincipalSource = MicrosoftAccount
UserExtendedProperties : batvbs@qq.com
whoami /upn            : 报错「登录的用户不是域用户」
```

**结论**：用 `HKCU\Software\Microsoft\IdentityCRL\UserExtendedProperties` 的子键名可以可靠拿到
微软账户邮箱，这正是 `runas /u:MicrosoftAccount\<邮箱>` 需要的参数——也就是说这条命令**可以自动拼出来**。

### 2.4 重启 TermService 的代价是真实的

本机当前会话 5 就是正在运行的分身（Active）。TermService 一旦重启，所有 RDP 会话（含分身）都会断开，
分身里的程序会随之结束。**这是所有涉及服务重启的修复必须让用户知情的原因。**

## 三、逐项分析

### 原因 1：远程桌面主机被关闭

- **检测**：先读组策略 `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services\fDenyTSConnections`，
  再读本机 `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\fDenyTSConnections`。策略优先。
- **自动修复**：本机值时，写 `fDenyTSConnections = 0` 并重启 TermService。
- **为什么不能无脑修**：
  1. 这一项等于「允许别人从网络远程连接这台电脑」，是**安全设置的放宽**，必须让用户明确同意；
  2. 重启 TermService 会断开当前所有 RDP 会话，包括用户正在用的分身。
- **必须手动**：组策略下发时（`policyValue is not null`），改本机注册表会被覆盖，只能改 gpedit 或找域管理员。
- **建议交互**：检测到 → 弹确认框说明「会开启远程桌面、会断开现有会话」→ 用户确认后执行 → 重启服务 → 复查。

### 原因 2：仅允许 Windows Hello 登录

- **检测**：`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device\DevicePasswordLessBuildVersion == 2`。
  注意该值没有官方文档，属社区结论，因此只把 `2` 判为开启。
- **自动修复**：写 `0`。
- **关键限制**：**改完不生效**。凭据提供程序在登录会话建立时就已确定，必须注销重新登录（多数情况还要重启）
  才会重新生成可用的密码凭据。程序能做的只有「改 + 提示用户重启」。
- **必须手动**：注销/重启。这一步没有 API 可以绕过——`ExitWindowsEx` 虽然能重启，但替用户重启机器
  属于不可接受的副作用，只能提示。
- **额外注意**：这项只对**微软账户**有意义。本地账户没有 Hello-only 限制，检测到 2 时应先确认账户类型，
  否则会给出误导性的建议。

### 原因 3：微软账户本地密码缓存过期

- **现象**：网页上改过密码，本机仍用旧密码校验，输入新密码一直失败。
- **手动方案（用户给的）**：`runas /u:MicrosoftAccount\<邮箱> winver`，输入一次密码后回车。
  本质是用新密码做一次登录，把本地缓存的凭据验证器刷新。
- **可自动化的部分（已验证）**：
  - 用 `LogonUser` 校验用户保存的密码是否**当前有效**，错误码可区分「密码错(1326) / 账户禁用(1331) /
    密码过期(1330) / 空密码受限(1327)」。这能把「密码不对」和「缓存过期」区分开。
  - 从 `UserExtendedProperties` 读出邮箱，自动拼出上面那条 `runas` 命令，用户只需粘贴执行。
- **可自动化的部分（未验证）**：`CreateProcessWithLogonW(user, "MicrosoftAccount", password, ...)`
  理论上等价于 `runas`，可以用保存的密码直接刷新缓存。但：
  1. 本机探测返回错误 5，未确认是环境限制还是 API 限制；
  2. 用错密码调用会**累计失败登录次数**，存在触发账户锁定策略的风险；
  3. 是否真的能刷新微软账户的本地缓存，没有官方文档支持。
- **建议**：首发只做「校验 + 生成命令」，**不要自动执行登录**。若要自动，必须限定一次、失败即停、并明确告知风险。

### 原因 4：装过 RDP Wrapper / SuperRDP

- **根因**（issue #3409 已确认）：补丁工具把 `TermService\Parameters\ServiceDll` 指向 `rdpwrap.dll` /
  `superrdp.dll`，劫持 TermService 以解锁多用户并发。桌面分身走的是原生 `termsrv.dll` 的 Child Session，
  同一时刻 `ServiceDll` 只能指一个，**两者互斥**。
- **判定方式（已修正）**：早期实现只判断路径里是否含 `rdpwrap.dll`，漏掉了 `superrdp.dll`、`RDPWrappers.dll`。
  现在按「**不等于原生 termsrv.dll 就算被劫持**」判定，不看名字。
- **自动修复（可逆）**：
  1. 备份当前 `ServiceDll` 值；
  2. 优先调用补丁自带的卸载器 `%ProgramFiles%\RDP Wrapper\RDPWInst.exe -u`（issue 评论里也推荐这条，
     比自己改注册表更可靠）；
  3. 没有卸载器时，直接写回 `C:\Windows\System32\termsrv.dll`；
  4. 重启 TermService；
  5. **清理持久化项**（v0.2.4 起）：不再只认死的任务名 `RDP Wrapper Autoupdate`，而是扫描
     计划任务 XML 全文与 Run/RunOnce 自启项里的 `rdpwrap` / `superrdp` / `autoupdate.bat` 等特征，
     命中项先导出备份再删除。改个任务名就漏检的写法在这里不可接受——它们唯一的作用就是
     开机把 ServiceDll 改回去，不清掉的话用户看到的是「修好了，重启又坏」。
- **必须手动**：无（前提是用户确认重启服务）；若同时装了多套补丁工具，可能需要用户自己判断保留哪个。
- **注意**：还原后 RDP Wrapper 的多用户能力就没了，这是用户的取舍，必须在确认框里说清楚。

### 原因 5：TermService 未运行 / 被禁用（本次补充）

- **检测**：`GetServiceStatus` + 启动类型。
- **自动修复**：启动类型 `Disabled` → 改 `Manual`；然后启动服务。
- **代价**：同样会打断现有会话（若服务本就没运行，则没有会话可打断）。

### 原因 6：账户层面的问题（本次补充）

- **检测**：`LogonUser` 返回码。
  - `1327` → 空密码或登录时段限制。本机 `LimitBlankPasswordUse=1`，空密码账户**无法用于 RDP**。
  - `1331` → 账户已禁用。
  - `1330` → 密码已过期。
- **自动修复**：不能。改密码、启用账户都需要用户本人操作（或域管理员）。
- **价值**：能把「连接超时」翻译成一句人话，避免用户反复重试。

### 原因 7：系统版本过旧（本次补充）

- `WTSEnableChildSessions` 的最低支持客户端是 Windows 10 1803。更早的系统上该导出不存在，
  调用会以 `EntryPointNotFoundException` 失败。
- **检测**：捕获 `EntryPointNotFoundException`，或 `WTSIsChildSessionsEnabled` 返回错误 120（不支持）。
- **自动修复**：不能，只能提示升级。

### 原因 8：安全软件拦截（本次补充）

- 本机实测有 `360DesktopLite64`、`360DesktopService64` 等进程（属桌面助手/压缩，不是杀毒核心）。
- **检测**：只能按已知进程名做提示性判断，**不可靠**——拦不拦由驱动层决定，进程在不在说明不了问题。
- **自动修复**：不能，且**不应该尝试**（绕过安全软件是本项目明确不做的事）。
- **建议**：只在连接失败后作为「可能原因」列出，让用户自己去加白。

### 原因 9：组策略禁止保存凭据（本次补充）

- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\DisableDomainCreds = 1` 时，
  自动登录保存的密码无法用于凭据框。
- **检测**：读该值即可（本机未设置）。
- **自动修复**：不能，属域策略。

## 四、自动修复必须遵守的边界

如果将来实现这个功能，以下五条是硬约束，建议直接写进代码注释：

1. **不静默改安全设置**。`fDenyTSConnections`、`ServiceDll` 都属于安全相关，必须弹确认框写明「改什么、影响什么、能不能撤回」。
2. **改前必备份，改后必复查**。`ServiceDll` 原值写进设置文件；改完重新读一遍确认生效，不能只看写入成功。
3. **服务重启前检查活动会话**。用 `WTSEnumerateSessions` 判断除 Console 外是否还有 Active 会话，
   有就先提示用户「这会关闭分身里的程序」，让用户决定。
4. **不替用户重启/注销**。能改注册表的就只改注册表，重启这一步交给用户——机器上可能有别人正在用的东西。
5. **需要密码的路径必须限次**。任何会触发登录验证的操作，一次连接最多一次，失败即停并转人工，
   避免把账户往锁定策略上推（这条与自动登录的重试上限是同一个原则）。

## 五、建议的实现顺序

| 阶段 | 内容 | 理由 |
|---|---|---|
| 第一步 | 把 RDP 补丁检测从「黑名单」改为「非原生即异常」，并列出计划任务 | 当前是**漏检**，属准确性问题，成本最低 |
| 第二步 | 补上 TermService 状态、账户错误码、系统版本三项检测 | 纯读取，无副作用，直接提升报错可读性 |
| 第三步 | 「一键修复」：`fDenyTSConnections`、`ServiceDll` 两项，带确认框与会话检查 | 收益最大，风险可控（可逆 + 有备份） |
| 第四步 | 微软账户：校验密码 + 生成 `runas` 命令供复制 | 零风险，解决「输对了密码还是失败」的困惑 |
| 暂不做 | 自动执行 `CreateProcessWithLogonW` 刷新凭据 | 效果未验证，且有账户锁定风险 |

## 六、尚未验证的事项

诚实记录，避免后续误以为已经验证过：

1. **`CreateProcessWithLogonW` 能否真正刷新微软账户缓存** —— 未验证。需要真实密码 + 真实微软账户，
   且失败会累计登录次数，不宜在验证阶段尝试。
2. **改 `fDenyTSConnections` 后是否必须重启 TermService** —— 属通用结论，未在本机实测
   （实测会打断正在运行的分身会话）。
3. **`DevicePasswordLessBuildVersion` 改为 0 后能否立即恢复密码登录** —— 未实测，按社区结论需注销/重启。
4. **RDP Wrapper 卸载后是否残留其他劫持点**（如 `ServiceDll` 以外的注册表项）—— 未实测，
   本机未安装过补丁，无法构造现场。
