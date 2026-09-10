# PioneerScenarioAnalyzer

解析野人杯回合信息，并在插件 workspace 中保存当前进程的训练分析 history。

History 以 `(single_mode_chara_id, turn)` 为键；同键输出原位更新，不会因其它插件修改分析结果而新增记录。History 内容不跨进程保存。

`PluginData/PioneerScenarioAnalyzer/settings.json` 中的 `historyLimit` 配置最多保留的记录数，默认 `100`，有效范围 `0-1000`。设为 `0` 时仅显示最近一次分析，不保留 history。

History 启用时，焦点位于训练分析内容内可使用：

- `↑`：上一条（更旧）
- `↓`：下一条（更新）
- `←`：最旧一条
- `→`：最新一条

正文滚动使用 `PageUp`、`PageDown`、`Home`、`End` 或鼠标滚轮。

## 构建

```powershell
git -c core.longpaths=true submodule update --init --recursive
dotnet build .\PioneerScenarioAnalyzer\PioneerScenarioAnalyzer.csproj -c Release -m:1 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PlatformTarget=AnyCPU -p:DeployUraPluginToLocalAppDataOnBuild=false
```

## 验证与发布

在 Windows 仓库根执行 `act workflow_dispatch --artifact-server-path "$env:TEMP/ura-act-artifacts"`。本地与 GitHub 使用同一份 workflow；版本 tag 触发 GitHub Release 发布。环境要求、共用 workflow 本地映射和发布规则见 [URA plugin workflows](https://github.com/URA-Plugins/.github/blob/v1/README.md)。
