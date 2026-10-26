# DevHub 1.1.0 发布验收

本文件区分自动检查与需要干净系统完成的手工验收。Release 默认为草稿；没有执行的项目不能标记成已通过。

## 自动检查

- `./build.ps1 -Task Build`：完整方案、主界面、管理员辅助程序和独立更新程序。
- `./build.ps1 -Task Test -NoRestore`：隔离夹具中的环境修复、解析、下载校验、更新替换和恢复；不会修改本机环境变量。
- `./build.ps1 -Task Package -NoRestore`：从独立输出目录生成完整 ZIP、更新描述及 SHA-256。
- `./scripts/Test-PublishedApp.ps1 -PublishDirectory <发布目录>`：逐文件校验后复制到含中文与空格的目录，从其他工作目录启动，检查就绪信号和退出结果。
- 同一启动检查在普通、最大化、还原窗口中检查 8 个页面，共 24 个可视范围断言；主界面和设置页的渲染图留在测试副本的日志目录，图像导出失败会单独记录，不等同于布局失败。
- `dotnet run --project tests/DevHub.Tests/DevHub.Tests.csproj -p:Platform=x64 --configuration Release --no-build --no-restore -- --update-smoke <发布目录>`：使用独立程序副本运行真实更新进程，分别验证启动成功及版本不符时自动恢复。测试模式不联网、不更改系统、不显示交互对话框。
- `./build.ps1 -Task Doctor -NoRestore`：本机只读检查，报告位于 `.devhub/logs/environment-report.json`，不纳入 Git。

## 干净 Windows 验收（公开发布前执行）

环境：Windows 10 22H2 x64、Windows 11 24H2 或更新的 x64；不安装 .NET SDK、Java、Python 或开发工具。此步骤需要独立 VM 或 Windows Sandbox，不能在开发电脑上卸载运行时来模拟。

1. 核对 ZIP 的 SHA-256，解压到含中文与空格的可写目录，断网后双击 `Start-DevHub.cmd`，再检查直接打开 `DevHub.App.exe`。
2. 确认主界面正常、程序图标正确、最大化与还原时不截断、页面只有一个主滚动区域。切换设置、诊断和 AI 工具页面。
3. 在独立副本中损坏 `.devhub/data/settings.json`，验证启动使用默认配置且损坏内容有备份；使程序目录不可写，确认能看到明确错误。
4. 仅在 VM 中安装两套测试 JDK、Python、Node，构造系统与用户 PATH 冲突；核对三个环境视角的实际解析结果。
5. 测试用户范围修改不弹管理员授权；系统范围取消授权后配置不变。应用后重新打开无个人配置终端，核对 java、javac、JAVA_HOME，并验证备份恢复。
6. 在测试发布仓库副本或离线集成夹具中验证更新成功、下载中断、错误哈希、文件占用、权限不足、替换中断和新版无法启动；检查 SDK、配置和用户文件未被改变。

本机检查不能替代上述干净系统和 UAC 交互验收。更新包未签名；公开前应保留这一说明。
