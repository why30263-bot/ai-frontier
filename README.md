# AI Frontier

面向普通读者的开源 Windows AI 新闻流。它把大模型、Agent、重要研究、开源项目和产业动态整理成「标题 + 摘要」卡片；点开后再阅读关键事实、背景、入门解释、影响、局限、后续观察与原始来源。

## 一键安装

[下载最新版 Windows 安装器](https://github.com/why30263-bot/ai-frontier/releases/latest/download/AIFrontier-Setup.exe)

- 支持 Windows 10 1809 及以上的 x64 电脑。
- 安装到当前用户目录，不要求管理员权限，不要求预装 .NET，也不依赖 Codex。
- 项目暂未购买商业代码签名证书，首次下载时 Windows SmartScreen 可能显示“未知发布者”；请只从本仓库 Releases 下载，并可用同页 `.sha256` 文件校验。

## 它怎样保持更新

应用采用三层降级机制，即使维护者暂时不手工更新也能继续工作：

| 层级 | 作用 | 是否依赖 AI |
| --- | --- | --- |
| GitHub 公共编辑源 | 每 4 小时采集、去重并发布共享 `Data/news.json` | 自动使用 GitHub Models 做中文编辑，不依赖 Codex |
| 客户端内置采集器 | 公共编辑源超过 18 小时未更新时，直接读取 RSS、Atom、公开 API 与 GitHub 项目搜索 | 否 |
| 可选编辑增强 | 可由兼容接口或本机 Codex 把来源摘要改写成更完整的中文分析 | 是，可选 |

来源配置在 `Data/source-feeds.json`。应用优先读取仓库里的最新版配置，所以新增、替换或停用来源不需要先发布新版客户端。内置默认配置始终保留，断网或远程配置不可用时仍可回退。

## 内容质量策略

- 默认目标为 12–18 条，主窗口聚焦最近 72 小时；不足时最多补充近 7 天且明确标记为“补充阅读”。
- 单一来源最多 4 条，避免论文、某家公司或某家媒体淹没整个信息流。
- GitHub 项目必须达到最低关注度并在近期有提交；Star 和活跃度只是发现信号，不等于安全或生产可用。
- 社交平台只用于发现线索，正式条目必须回到公告、论文、仓库或可核查报道。
- 同一事件只保留一张主卡片；观点、事实和限制在详细页分开写。
- 没有足够高质量内容时允许少于目标数量，不用重复消息凑数。

详细规则见 `Data/editorial-policy.json`。

## 个性化与隐私

喜欢、不感兴趣、收藏、评分和“减少此类”只写入本机：

- `%LOCALAPPDATA%\AIFrontier\feedback-events.jsonl`
- `%LOCALAPPDATA%\AIFrontier\workflow-profile.json`

项目不内置遥测、不上传阅读历史，也不会把反馈直接变成不可检查的自由文本提示词。反馈只调整有限范围的主题、来源与阅读深度权重。

## 自动更新

客户端启动后检查 GitHub Releases。发现新版时会下载安装器和 SHA-256 校验文件；校验通过后暂存，并在下一次启动时静默安装。更新失败不会影响当前版本使用。

## 可选 AI / Codex 增强

固定采集器始终可独立运行。公开仓库的 Actions 默认使用 GitHub 自动提供的 `GITHUB_TOKEN` 调用 GitHub Models，把标题、摘要和详细分析统一改写成中文，不需要配置 Codex 或单独购买 API Key。若希望替换为自己的兼容接口，可配置：

- Secret：`AI_API_KEY`
- Variable：`AI_API_BASE`（兼容 `/chat/completions`）
- Variable：`AI_MODEL`

不设置时工作流自动使用可审计的规则模板。也可以让本机 Codex 读取 `workflow-profile.json`，核查来源后更新 `Data/news.json`；建议遵循 [工作流约定](Workflow/README.md)，Codex 只是可选增强层。

## 本地开发

需要 .NET 10 SDK、Windows 10/11 和 Visual Studio 2022 的 WinUI 工具，或直接使用命令行：

```powershell
.\run-dev.ps1
```

如果存在 `D:\DevTools\dotnet`，脚本会自动把 SDK、NuGet 缓存和构建输出放到 D 盘；其他电脑则使用 PATH 中的 `dotnet`。发布命令：

```powershell
dotnet publish AIFrontier.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishTrimmed=false -p:PublishReadyToRun=false -o dist
```

资讯采集器没有第三方 Python 依赖：

```powershell
python scripts/update_feed.py --config Data/source-feeds.json --output Data/news.json
```

## 参与贡献

欢迎提交新的高质量来源、去重规则、无障碍改进、中文编辑模板和 Windows 兼容性修复。请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。

本项目使用 [MIT License](LICENSE)。
