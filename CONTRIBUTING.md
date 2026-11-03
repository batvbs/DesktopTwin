# 参与开发

## 环境

- Windows 10 及以上（开发与调试都需要）
- .NET SDK 10.0+
- Visual Studio 2022（含 .NET 桌面开发工作负载）或 Rider

```powershell
dotnet build -c Release
```

编译通过即可提交。**注意：程序清单要求管理员权限，调试时需要以管理员身份启动 IDE 或生成的 exe。**

## 代码约定

- C# 文件使用 **UTF-8 无 BOM**（`.editorconfig` 已声明，Windows 上注意不要用 PowerShell 的默认编码写文件）
- 私有字段 `_camelCase`，公共成员 `PascalCase`
- 可空引用类型已开启，不要用 `!` 抑制警告，优先改成显式判空
- 新增类型、公共成员必须有 XML 注释；关键流程要写清「为什么这么做」，而不是复述代码
- 一个文件原则上只放一个类（枚举、单一用途的小 record 可以合并）
- XAML 中绑定只读属性时必须显式写 `Mode=OneWay`（`Run.Text` 等默认双向绑定的属性会直接抛异常）

## 提交

- 一个提交只做一件事，提交信息写清「改了什么、为什么」
- 涉及 `Interop/` 或 `ChildSessionProcessLauncher` 的改动，请在 PR 描述里说明验证方式
  （这类代码在多数机器上无法自动化验证，需要人工说明依据）

## 报告问题

请附上 **控制中心 → 诊断 → 复制环境信息** 的内容，以及 `%AppData%\DesktopTwin\logs\` 里对应的日志。

由于本功能完全建立在 Windows 的 RDP 能力之上，**「在你这台机器上不行」不等于「程序有 bug」**。
提交前请先过一遍 [docs/troubleshooting.md](docs/troubleshooting.md)，并在 Issue 里说明已经试过哪些步骤。

## 许可证

本项目为 GPL-3.0。提交代码即表示同意以同一许可证发布。
如果你引入了第三方代码或资源，请同步更新 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
