# StudentAge QQ AI Moments

这是一个用于《StudentAge》的 BepInEx 插件源码仓库。插件会在不替换游戏原有 QQ 空间配置动态的前提下，增量加入 AI 生成的 NPC 空间互动。

## 功能概览

- NPC 可根据人设、好感、关系和上下文给主角动态点赞、评论。
- NPC 可主动发布 AI 生成的 QQ 空间动态。
- NPC 之间可以在动态下产生评论和回复。
- 玩家可在支持的 QQ 空间互动入口输入自由回复。
- AI 输出可映射到游戏内好感、关系、属性等数值变化。
- 默认人设会内嵌在 DLL 中，首次运行时释放到配置目录，开发者可继续编辑 JSON。
- 支持 OpenAI-compatible `/v1/chat/completions` 与 `/v1/responses` 端点。
- 支持可选使用数据上报；公开源码不内置作者私有上报 token，默认不上传。

## 仓库不包含什么

本仓库只开源插件源码和辅助脚本，不包含：

- 游戏本体文件。
- 游戏反编译源码。
- 付费资源或第三方专有 DLL。
- 作者本机 API Key、Cloudflare Worker token、调试日志、用户存档。

## 构建要求

- Windows
- .NET SDK
- 已安装《StudentAge》
- 游戏目录中已安装 BepInEx 5

插件项目需要引用游戏目录中的 `BepInEx/core` 和 `StudentAge_Data/Managed`。构建时通过 `GameDir` 指向你的游戏安装目录：

```powershell
dotnet build .\StudentAge.QQAIMoments.csproj -c Release -p:GameDir="D:\Steam\steamapps\common\StudentAge"
```

如果只想构建 DLL，不想自动复制到游戏目录：

```powershell
dotnet build .\StudentAge.QQAIMoments.csproj -c Release -p:GameDir="D:\Steam\steamapps\common\StudentAge" -p:CopyToGame=false
```

输出文件位于：

```text
bin\Release\net472\StudentAge.QQAIMoments.dll
```

## 安装

把构建出的 DLL 放入游戏目录：

```text
StudentAge\BepInEx\plugins\StudentAge.QQAIMoments.dll
```

也可以使用 `release/install_qqai_moments.bat`。脚本会尝试自动定位 Steam 游戏目录，并把 DLL 复制到 `BepInEx\plugins`。

## 用户配置

首次启动后，BepInEx 会生成配置文件：

```text
StudentAge\BepInEx\config\studentage.qqai.moments.cfg
```

常用配置：

```ini
[AI]
UseAI = true
BaseUrl = http://127.0.0.1:11434/v1/chat/completions
ApiKey =
Model = qwen2.5:7b
```

说明：

- `BaseUrl` 可以填完整端点，也可以填兼容服务的 base URL。
- `ApiKey` 留空时不会发送 `Authorization` 请求头。
- 默认端点兼容 `/v1/chat/completions`；配置为 `/v1/responses` 时也会走 Responses 接口。

## 人设配置

默认人设内嵌在插件 DLL 中。首次运行时会释放到：

```text
StudentAge\BepInEx\config\QQAIMoments\personas.json
```

后续新增或修改角色时，优先编辑这个 JSON。插件支持热加载，保存后新的 AI 请求会使用新内容。

## 数据共享与隐私

数据共享默认关闭。首次启动时插件会询问用户是否共享使用数据。

公开源码中：

- `ShareUsageDataEndpoint` 默认值为空。
- 不内置作者私有 Cloudflare Worker token。
- 为空时不会联网上传，只在本地 `QQAIMoments/telemetry` 留存 JSONL。

如果你要自行部署上报服务，可以参考 `telemetry-worker/`，并在本地配置文件中填入自己的端点。

## 编码约定

源码和中文文档统一使用 UTF-8。仓库包含 `.editorconfig` 与 `.gitattributes`，用于减少 Windows PowerShell、旧编辑器或 Git 换行/编码设置导致的中文乱码。

## 许可

见 `LICENSE`。

