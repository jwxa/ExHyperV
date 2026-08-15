# ExHyperV 局域网远程宿主管理

本文说明 ExHyperV 第一阶段局域网远程宿主管理的协议、使用流程、支持矩阵、安全边界和故障排查。设计规格见 [remote-host-management-spec.md](remote-host-management-spec.md)。

## 1. 设计边界

- 管理通道固定为 WMI/DCOM，控制台通道固定为 TCP 2179。
- 只接受单播 IPv4 字面地址，不支持主机名、IPv6、回环或多播地址；该功能用于受控局域网宿主。
- 不使用 WinRM、PowerShell Remoting、SSH、自定义 RPC 或驻留代理。
- 可以保存多台远程宿主，但同一时刻只有一台活动宿主。
- 应用每次启动都以本地计算机为活动宿主，不自动恢复上次远程目标。
- 选择配置不会切换宿主；只有检测完成并明确连接后才执行原子切换。

## 2. 通道与部分可用

| 通道 | 用途 | 检测方式 | 失败后的行为 |
|---|---|---|---|
| WMI/DCOM | Hyper-V 查询、虚拟机生命周期、配置向导 | 实际查询 `root\virtualization\v2` | 不能激活远程管理，或活动连接进入旧数据/重连状态 |
| TCP 2179 | Hyper-V 虚拟机控制台 | 连接目标 IPv4 的 TCP 2179 | 管理功能保持可用，控制台置灰并显示原因 |

诊断不会把“端口可连接”等同于“Hyper-V 可管理”。WMI/DCOM 必须成功访问实际 Hyper-V 命名空间。两条通道分别建模，因此 WMI/DCOM 成功、TCP 2179 失败属于“部分可用”，不是整体失败。

## 3. 身份与本地保存

### 当前 Windows 身份

这是默认模式。ExHyperV 不设置 WMI 用户名或密码，也不会访问 Windows 凭据管理器。目标宿主根据当前登录令牌和 Windows/DCOM 规则进行认证。

### 显式凭据

显式凭据可以只用于当前操作，也可以由用户选择记住。记住后：

- 密码只作为 Windows 通用凭据写入本机 Windows 凭据管理器。
- `%LOCALAPPDATA%\ExHyperV\Hosts.xml` 只保存用户名和凭据目标引用。
- 配置 XML、日志、错误信息和回滚脚本不保存密码或令牌。

检测显式凭据时，主机连接页会先尝试通过目标机 TCP 445 的临时 `IPC$` 网络会话做独立校验。该校验只用于诊断，不是远程管理通道，也不会中断已有 SMB 会话。结果分为三态：

- **凭据有效**：后续 WMI/DCOM 仍失败时，页面明确提示目标账户缺少 WMI/DCOM 或 Hyper-V 管理权限。
- **凭据无效**：常见的 Win32 `1326`、`86`、错误用户名、锁定、过期或禁用等结果会在“身份”步骤直接提示用户名或密码、账户状态或网络登录限制，并跳过 WMI 查询。
- **无法确认**：TCP 445 未开放、已有 SMB 会话、超时或其他无法区分认证与授权的错误会显示“无法独立确认密码”，随后继续 WMI/DCOM。此时 WMI 的 `0x80070005` 不会被武断地归类为密码错误或权限不足；用户应先重新输入并确认密码，再按提示检查权限。

TCP 445 不可用不影响 WMI/DCOM 和 TCP 2179 的独立检测。关闭 445 时，诊断会在短超时后快速降级，不等待 SMB 长超时。

## 4. 连接流程

1. 打开 **主机连接**，新增显示名称和受控局域网宿主的单播 IPv4 地址。
2. 选择当前 Windows 身份，或输入显式凭据并决定是否记住。
3. 保存配置。保存和选中配置都不会改变活动宿主。
4. 执行检测，分别查看 IPv4、身份、WMI/DCOM 和 TCP 2179 结果及中文日志。
5. WMI/DCOM 成功后执行连接。ExHyperV 会建立候选会话并读取基础快照。
6. 候选连接和快照都成功后，活动宿主才会一次性切换；失败时原活动宿主保持不变。
7. 操作前始终核对窗口中的活动宿主名称和 IPv4 地址。

## 5. 远程功能支持矩阵

| 功能 | 本地宿主 | 远程宿主 | 说明 |
|---|---:|---:|---|
| 虚拟机列表和状态 | 支持 | 支持 | 远程通过活动 WMI 上下文读取 |
| 启动、正常关闭、强制关闭、重启 | 支持 | 支持 | 写入绑定活动宿主代次，切换后旧结果不会生效 |
| 基本/增强控制台 | 支持 | 条件支持 | 需要 TCP 2179；远程目标是活动宿主 IPv4 |
| 高级虚拟机设置 | 支持 | 不支持 | 第一阶段置灰并显示原因 |
| 宿主硬件页面 | 支持 | 不支持 | 依赖本机硬件、注册表或系统接口 |
| 虚拟交换机 | 支持 | 不支持 | 尚未迁移到远程活动上下文 |
| PCIe 直通 | 支持 | 不支持 | 依赖本机设备接口 |
| USB 直通 | 支持 | 不支持 | 依赖本机设备和后台服务 |
| 本地文件或磁盘选择 | 支持 | 不支持 | 路径属于运行 ExHyperV 的计算机，不代表远程宿主路径 |

## 6. 断线与自动重连

活动远程 WMI 操作出现网络、RPC 或超时类错误后：

1. 保留最后一份宿主快照，并明确标记为旧数据。
2. 禁止虚拟机写入和新控制台连接。
3. 保持远程宿主为活动目标，不静默切回本机。
4. 只运行一个自动重连循环，退避为 2、4、8、16、30 秒，之后保持 30 秒上限。
5. 重连成功后发布新的宿主代次、刷新快照和能力矩阵。

用户可以停止重连或立即重试。普通业务错误（例如对象不存在或权限不足）不会被误判为网络断线。远程配置完成后的复检如果发现 WMI/DCOM 已不可用，也会进入同一套旧数据、写冻结和自动重连流程，而不是仅把管理能力置灰后停住。

受控断线验收时，应从外部持续阻断目标宿主网络，直到运行器明确报告已观察到至少两级递增退避后，再恢复网络。验收报告必须同时证明：旧数据状态、旧数据期间写入被拒绝、活动目标未静默切回本机、退避递增且不超过 30 秒、恢复后产生新的会话代次、基础快照刷新，以及能力矩阵恢复。

## 7. 配置向导与最小修改

配置向导先执行只读预检，再让用户选择账户、网络接口和允许访问 TCP 2179 的私有 IPv4 CIDR。预览之后必须输入完全一致的中文 `确认`；前后空格、不同字符或附加文本都不会执行修改。

根据预检和用户选择，向导仅可能执行以下修改：

- 把所选账户加入内置 `Hyper-V Administrators`（SID `S-1-5-32-578`）。
- 把所选账户加入内置 `Remote Management Users`（SID `S-1-5-32-580`）。
- 仅在工作组本地管理员条件满足时启用 `LocalAccountTokenFilterPolicy`。
- 仅把用户明确选择的 Public 网络配置文件改为 Private。
- 启用 Windows 已有的 WMI 和 Hyper-V 入站防火墙规则；如果 `ActiveStore` 缺少规则，但目标机 `SystemDefaults` 中存在对应的 Windows 默认规则，则按 `InstanceID` 逐条复制到 `PersistentStore` 后再启用需要启用的规则。
- 创建或收紧 `ExHyperV Console (TCP 2179)` 入站规则：TCP 2179、Private/Domain、远程地址为用户选择的私有 IPv4 CIDR。

规则恢复不使用规则组、通配符或整库重置，也不会修改无关规则。向导不会创建账户、修改密码、加入 `Administrators`、修改全局动态 RPC 端口范围、启用 WinRM/SSH，或把 ExHyperV 创建的 TCP 2179 规则远程地址设为 `Any`。

### 回滚

- 每条远程命令提交前，程序都会先原子持久化包含当前步骤的保护性幂等回滚脚本；预写失败会阻止该命令提交。
- 命令明确未执行时，程序会从脚本中收缩或删除对应保护条目；提交结果未知、取消或异常时会保留该条目。每个确认成功的步骤继续由回滚脚本覆盖。
- 脚本位于 `ExHyperV.exe` 同目录的 `logs` 文件夹，使用无 BOM UTF-8。
- 配置结果显示回滚路径后，可点击路径右侧的文件夹按钮在资源管理器中定位脚本；文件已移动或删除时会显示明确错误。
- 回滚命令按应用顺序的反向执行，并只恢复本次操作前捕获的状态。
- 从 `SystemDefaults` 恢复规则时，复制中途失败会立即补偿已经复制的精确规则。人工回滚会先把整批 `PersistentStore` 规则及其地址、端口、应用、服务、接口和安全过滤器与 `SystemDefaults` 规范化比对；全部通过后才逐条删除。来源、状态、过滤器或所有权发生漂移时，脚本拒绝删除当前规则。
- 回滚脚本本身也要求输入完全一致的中文 `确认`。
- ExHyperV 不会自动执行回滚；用户需要检查脚本后在目标宿主上明确运行。

## 8. 日志

- 目录：`<ExHyperV.exe 所在目录>\logs`。
- 可从“主机连接”页顶部的“日志”按钮或“日志”页中的“打开日志目录”按钮直接打开；目录不存在或 Explorer 启动失败时会显示原因。
- 当前文件：`ExHyperV.log`；上一轮转文件：`ExHyperV.1.log`。
- 编码：UTF-8，无 BOM，支持中文。
- 每个日志文件上限 100 MiB，只保留两个日志文件，日志正文总量约 200 MiB。
- 回滚 `.ps1` 文件也位于该目录，但不计入两个滚动日志文件的 200 MiB 上限。
- 密码、令牌、授权头、凭据对象、密钥等敏感字段在写入前脱敏。

如果程序目录不可写，日志服务会变为不可用并给出原因，不会回退到其他隐藏目录。

## 9. 最小权限建议

- 首选域账户，或两台受控主机上密码一致的工作组账户。
- 当前用户至少需要访问远程 WMI/DCOM 和 `root\virtualization\v2`。
- 虚拟机管理使用内置 `Hyper-V Administrators`，远程管理使用内置 `Remote Management Users`。
- 不要为了排障直接授予 `Administrators`；先根据检测结果修复组成员、网络配置文件和防火墙。
- 配置向导的目标机修改需要足够权限；权限不足时只报告失败，不会绕过 Windows 安全边界。

## 10. 故障排查

### IPv4 不可达

- 确认目标机已开机，地址属于当前可信局域网，并且不是回环、多播、公网或 IPv6 地址。
- 检查本机路由和 ARP/邻居项。同网段邻居长期为 `Incomplete` 通常表示问题发生在 WMI 认证之前。
- ICMP 失败不一定证明 WMI 不可用，仍应查看 WMI/DCOM 和 TCP 2179 的独立结果。

### WMI/DCOM 不可用

- 确认目标机安装并启用了 Hyper-V，且账户可以查询 `root\virtualization\v2`。
- 检查 TCP 135、RPC/DCOM、防火墙内置 WMI/Hyper-V 规则和所需组成员。
- 如果 `ActiveStore` 中缺少 Windows 内置 WMI 或 Hyper-V 入站规则，预检会读取目标机 `SystemDefaults` 并列出可精确恢复的规则数量和 `InstanceID`。只有缺失规则无法从 `SystemDefaults` 唯一定位，或规则清单为空、重复、包含通配符时，ExHyperV 才会阻止配置预览并要求先修复目标机系统默认规则。
- 工作组本地账户还可能受到远程 UAC 令牌筛选影响；只在向导判定条件成立时考虑策略修改。
- 如果身份步骤已经报告“用户名或密码错误”、账户锁定/过期/禁用或网络登录限制，应先修正凭据，不要继续修改 WMI 权限。
- 如果身份步骤报告“无法独立确认密码”，先编辑配置重新输入密码；确认密码正确后，再检查账户格式、两台主机的信任/工作组条件、WMI/DCOM 和 Hyper-V 组权限。
- 如果身份步骤报告凭据验证通过而 WMI 返回拒绝，优先检查目标账户的 WMI/DCOM 和 Hyper-V 管理权限。

### 管理可用但控制台置灰

- 这是受支持的部分可用状态。继续使用虚拟机列表和生命周期操作。
- 检查目标机 TCP 2179 是否监听，以及 `ExHyperV Console (TCP 2179)` 规则是否启用。
- 核对规则为 TCP、本地端口 2179、Private/Domain，并包含客户端所在的私有 IPv4 CIDR。

### 连接中断

- 页面显示旧数据时不要把快照当作实时状态；写操作会保持禁用。
- 查看重连次数、下次尝试时间和 `logs\ExHyperV.log` 的中文详细记录。
- 网络恢复后可以等待自动重连或选择立即重试；需要明确操作才能切回本机。

### 配置或回滚失败

- 不要重复盲目执行向导。先保存结果面板中的已完成/失败步骤和回滚脚本路径。
- 目标状态不确定时，对应步骤会保守地进入回滚脚本。
- 在目标宿主上以足够权限检查并运行回滚脚本；脚本会逐项显示中文结果并保持幂等。

## 11. 已知限制

- 第一阶段只支持 Windows/Hyper-V 局域网宿主和单播 IPv4，不提供公网安全边界或主机发现。
- 只有虚拟机列表、四项生命周期操作和控制台接入远程活动上下文；其他页面保持本地专属。
- WMI/DCOM 使用 Windows RPC 动态端口，不能只靠开放 TCP 135 证明完整可用。
- TCP 2179 只决定控制台能力，不是 Hyper-V 管理能力的前置条件。
- 回滚脚本需要用户在目标宿主上明确运行，程序不会自动回滚远程系统。

## 12. 受控宿主集成验收

仓库提供独立的 `tests/ExHyperV.IntegrationTests` 验收运行器。它复用产品的 Windows IPv4、WMI/DCOM、TCP 2179、活动宿主、VM 查询和控制台捕获适配器；默认不会访问网络：

```pwsh
$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH="$env:DOTNET_ROOT;$env:PATH"
dotnet run --project tests\ExHyperV.IntegrationTests\ExHyperV.IntegrationTests.csproj -c Release
```

未设置 `EXHYPERV_INTEGRATION_RUN=确认` 时，命令只输出 `SKIP`。启用后至少需要设置 `EXHYPERV_INTEGRATION_HOST=10.0.0.6`；默认使用当前 Windows 身份。运行器会通过产品 `HostProfileStore` 保存并重载本次验收配置，配置证据写入与报告同名的 `.hosts.xml` 文件。验收结果写入 `.codex-tasks/remote-host-management/raw/controlled-host-acceptance-*.json`，也可用 `EXHYPERV_INTEGRATION_REPORT` 指定其他 `.json` 文件；路径会在联网前规范化和校验。配置证据无法可靠落盘时，运行器会在任何网络访问前停止。报告和配置证据均不包含密码、Credential Manager 内容或完整异常堆栈。

显式凭据可从 Windows Credential Manager 读取：设置 `EXHYPERV_INTEGRATION_AUTH=credential-manager`、固定的 `EXHYPERV_INTEGRATION_PROFILE_ID=<GUID>` 和 `EXHYPERV_INTEGRATION_USERNAME=<用户名>`，运行器读取 `ExHyperV/RemoteHost/<GUID>`。也可以额外设置 `EXHYPERV_INTEGRATION_PASSWORD=<一次性密码>`；密码只存在当前进程内，不写入报告或日志，运行结束后应立即清除环境变量。

默认只读验收包含配置保存/重载、两通道诊断、只读预检、原子激活、基础快照、真实远程 VM 列表和 TCP 2179 控制台捕获。设置 `EXHYPERV_INTEGRATION_SECOND_HOST=<IPv4>` 后，运行器还会诊断第二宿主，执行第一宿主 → 第二宿主 → 第一宿主的原子往返切换，并通过第二宿主活动上下文读取真实 VM 列表。可用 `EXHYPERV_INTEGRATION_SECOND_DISPLAY_NAME` 指定显示名；当前身份模式可省略 `EXHYPERV_INTEGRATION_SECOND_PROFILE_ID`，Credential Manager 模式必须提供不同的固定 GUID，并预先将第二宿主凭据保存到 `ExHyperV/RemoteHost/<第二宿主GUID>`。

未提供第二宿主时，“两台受控宿主切换”阶段明确记为“跳过”，报告只能是部分通过，不能作为 Issue #13 两宿主验收证据。以下危险开关相互独立，只有值精确为中文 `确认` 才会启用：

执行真实配置前，先设置 `EXHYPERV_INTEGRATION_CONFIGURE_PREVIEW=确认`，并提供下述账户、接口和 CIDR 变量。运行器会基于本次真实只读预检生成完整计划并写入报告，然后在创建 `HostConfigurationPipeline` 前返回；该模式不会执行远程命令，也不会生成回滚脚本。审查报告后必须在下一次独立运行中改用 `EXHYPERV_INTEGRATION_CONFIGURE=确认` 才会申请修改。

TCP 2179 可用和不可用是两个独立的受控验收场景，必须分别留存报告。可用场景证明控制台捕获绑定活动宿主的 IPv4 和会话代次；不可用场景必须在 WMI/DCOM 仍可用时临时阻断 TCP 2179，并证明真实 VM 读取及 VM 读写能力保持可用、控制台能力置灰、控制台捕获被拒绝，且原因明确包含 `TCP 2179`。

| 环境变量 | 作用 | 额外要求 |
|---|---|---|
| `EXHYPERV_INTEGRATION_VM_WRITE` | 执行一次远程 VM `Start`/`Stop`/`TurnOff`/`Restart` | `EXHYPERV_INTEGRATION_VM`、`EXHYPERV_INTEGRATION_VM_ACTION` |
| `EXHYPERV_INTEGRATION_DISCONNECT` | 等待用户从外部临时断网，验证旧数据、写冻结和自动重连 | 持续阻断直到运行器观察到至少两级递增退避并提示恢复；运行器不修改网络 |
| `EXHYPERV_INTEGRATION_CONFIGURE` | 执行真实配置向导计划 | 账户、接口索引、私有 CIDR 等配置变量；修改前仍由产品管线复检并要求 `确认` |
| `EXHYPERV_INTEGRATION_ROLLBACK_VERIFY` | 等待用户在目标宿主运行生成的回滚脚本，再复检基线 | 必须同时启用配置；不会自动运行脚本 |

配置验收还需要设置 `EXHYPERV_INTEGRATION_ACCOUNT_KIND=local|domain`、`EXHYPERV_INTEGRATION_ACCOUNT=<账户>`、`EXHYPERV_INTEGRATION_NETWORKS=1,2`、`EXHYPERV_INTEGRATION_MAKE_PRIVATE=1`（可为空）和 `EXHYPERV_INTEGRATION_CIDRS=10.0.0.0/24;192.168.50.0/24`。CIDR 必须完全位于 RFC1918 私有地址范围内。不要把生产凭据写入脚本或提交到 Git。

实机验收必须记录目标地址、认证模式、各阶段状态、WMI/DCOM 和 TCP 2179 错误码、活动宿主会话代次、VM 数量、控制台目标、配置步骤/回滚路径以及断线恢复代次。若目标在凭据层之前不可达，应保留失败报告并继续将 Issue #12/#13 标记为 OPEN，不得把探测结果伪装成成功。
