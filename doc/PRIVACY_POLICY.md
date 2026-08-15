# Privacy Policy for ExHyperV / 隐私政策

**Last updated / 生效日期**: 2026-08-14

ExHyperV is a free and open-source graphical Hyper-V management tool. It does not include usage telemetry, advertising identifiers, or an ExHyperV-operated cloud service.

ExHyperV 是一款开源免费的图形化 Hyper-V 管理工具，不包含使用情况遥测、广告标识符，也不依赖由 ExHyperV 运营的云端服务。

---

### 1. Data Collection / 数据收集

- ExHyperV does not send personally identifiable information, VM inventory, credentials, logs, or configuration data to the ExHyperV project.
- ExHyperV 不会向 ExHyperV 项目上传个人身份信息、虚拟机清单、凭据、日志或配置数据。
- The application stores the local settings and operational data described below so that requested management functions can work.
- 为了完成用户请求的管理功能，应用会在本机保存下文列出的设置与运行数据。

### 2. Local and LAN Processing / 本机与局域网处理

- Local-host operations are executed on the computer running ExHyperV.
- 本地宿主操作在运行 ExHyperV 的计算机上执行。
- When you explicitly diagnose, configure, connect to, or manage a saved remote host, ExHyperV communicates directly with the selected unicast IPv4 address. The feature is designed for controlled LAN hosts. WMI/DCOM is used for supported Hyper-V management operations, and TCP 2179 is used for the VM console.
- 当您明确检测、配置、连接或管理已保存的远程宿主时，ExHyperV 会直接访问所选单播 IPv4 地址；该功能用于受控局域网宿主。受支持的 Hyper-V 管理操作使用 WMI/DCOM，虚拟机控制台使用 TCP 2179。
- The configuration wizard can start a target-local PowerShell process through WMI/DCOM only after showing a change preview and receiving the exact Chinese confirmation `确认`.
- 配置向导仅在展示修改预览并收到完全一致的中文确认文本 `确认` 后，才会通过 WMI/DCOM 在目标宿主本地启动 PowerShell 进程。
- ExHyperV does not route remote-host traffic through an ExHyperV server, proxy, agent, WinRM, or SSH.
- ExHyperV 不会通过 ExHyperV 服务器、代理、驻留代理、WinRM 或 SSH 中转远程宿主流量。

### 3. Locally Stored Data / 本地保存的数据

- Application settings and host profiles are stored under `%LOCALAPPDATA%\ExHyperV`. Host profiles can contain a display name, unicast IPv4 address, authentication mode, optional user name, and a Windows Credential Manager target reference. They do not contain passwords.
- 应用设置和主机配置保存在 `%LOCALAPPDATA%\ExHyperV`。主机配置可以包含显示名称、单播 IPv4 地址、身份模式、可选用户名和 Windows 凭据管理器目标引用，不包含密码。
- If you choose to remember an explicit credential, its password is stored as a Generic Credential in Windows Credential Manager on the local computer. Without that choice, the password is kept only for the active operation.
- 只有在您选择记住显式凭据时，密码才会作为通用凭据保存在本机 Windows 凭据管理器中；未选择记住时，密码只在当前操作期间使用。
- Runtime logs and configuration rollback scripts are stored in the `logs` directory beside `ExHyperV.exe`. The two rolling log files are UTF-8 without BOM and capped at 100 MiB each. Sensitive password, token, authorization, credential, and secret fields are redacted before logging.
- 运行日志和配置回滚脚本保存在 `ExHyperV.exe` 同目录的 `logs` 文件夹中。两个滚动日志文件均为无 BOM 的 UTF-8 编码，每个上限 100 MiB。密码、令牌、授权、凭据和密钥等敏感字段会在写入前脱敏。
- You can remove saved profiles, delete remembered credentials when prompted, and delete local logs or rollback scripts using normal Windows file and credential-management tools.
- 您可以删除主机配置、在提示时删除已记住的凭据，也可以使用 Windows 常规文件和凭据管理工具删除本地日志或回滚脚本。

### 4. Other Network Access / 其他网络访问

- In addition to user-requested LAN remote-host access, ExHyperV may access GitHub Releases when checking for software updates and may use network access required by VM features you explicitly configure.
- 除用户主动发起的局域网远程宿主访问外，ExHyperV 检查软件更新时可能访问 GitHub Releases，也可能执行您明确配置的虚拟机功能所需的网络访问。

### 5. Contact Us / 联系方式

If you have questions about this policy, open an issue at:

如有隐私相关疑问，请通过以下 GitHub Issues 页面联系：

https://github.com/Justsenger/ExHyperV/issues
