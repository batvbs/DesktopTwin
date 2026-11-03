# 第三方组件与来源声明

## 代码来源

本项目的桌面分身实现源自 **[BetterGI](https://github.com/babalae/better-genshin-impact)**（作者：huiyadanli 及贡献者），
许可证为 **GPL-3.0**。抽取并改写的部分包括：

| 本项目文件 | 对应 BetterGI 文件 |
| --- | --- |
| `src/DesktopTwin/Services/ChildSessionService.cs` | `BetterGenshinImpact/Service/ChildSession/ChildSessionService.cs` |
| `src/DesktopTwin/Interop/ChildSessionNativeMethods.cs` | `BetterGenshinImpact/Service/ChildSession/ChildSessionNativeMethods.cs` |
| `src/DesktopTwin/Services/ChildSessionProcessLauncher.cs` | `BetterGenshinImpact/Service/ChildSession/ChildSessionProcessLauncher.cs` |
| `src/DesktopTwin/Services/ChildSessionEnvironmentCheck.cs` | `BetterGenshinImpact/Service/ChildSession/ChildSessionEnvironmentCheck.cs` |
| `src/DesktopTwin/Interop/RdpActiveXHost.cs` | `BetterGenshinImpact/View/Controls/ChildSession/RdpActiveXHost.cs` |
| `src/DesktopTwin/View/MainWindow.xaml(.cs)` | `BetterGenshinImpact/View/Windows/ChildSessionWindow.xaml(.cs)` |
| `src/DesktopTwin/ViewModel/MainWindowViewModel.cs` | `BetterGenshinImpact/ViewModel/Windows/ChildSessionWindowViewModel.cs` |
| `src/DesktopTwin/View/Behaviors/*` | `BetterGenshinImpact/View/Behavior/*` |

改动内容：命名空间与命名调整、去除对 BetterGI 配置/日志/i18n/依赖注入的耦合、把「启动 BetterGI」泛化为
「启动任意目标程序」、新增系统版本预检与诊断快照、补充注释。

由于上游为 GPL-3.0，本项目整体同样以 **GPL-3.0-or-later** 发布，见 [LICENSE](LICENSE)。

## 运行时依赖（NuGet）

| 包 | 版本 | 许可证 | 用途 |
| --- | --- | --- | --- |
| [WPF-UI](https://github.com/lepoco/wpfui) | 4.2.0 | MIT | Fluent 风格窗口、标题栏、图标、通知条 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.0 | MIT | `ObservableProperty` / `RelayCommand` 源生成器 |
| [Microsoft.Xaml.Behaviors.Wpf](https://github.com/microsoft/XamlBehaviorsWpf) | 1.1.135 | MIT | XAML 行为（拖拽、宽高比、尺寸记忆） |

## 系统组件

- **Remote Desktop ActiveX Control**（`MsRdpClient10`，CLSID `A0C63C30-F08D-4AB4-907C-34905D770C7D`）：
  Windows 自带组件，本项目通过 COM 调用，**不随包分发任何 mstscax.dll 文件**。
- **wtsapi32.dll / user32.dll**：Windows 系统库，通过 P/Invoke 调用。

## 商标

本项目中提到的第三方产品名称（包括但不限于 Windows、Chrome）仅用于说明兼容性，商标归各自所有者所有。
本项目与 BetterGI 团队、Microsoft 均无隶属关系。
