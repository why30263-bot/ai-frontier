# AI Frontier

面向普通读者的开源 Windows AI 新闻流。首页每批固定显示 10 条，可点击“换一批”，并优先保证大模型、Agent、重要研究、开源项目和产业动态都有覆盖。卡片只显示「标题 + 摘要」；点开后按资讯类型阅读带小标题的完整报道和原始来源。

左侧的“今日”保持公共编辑顺序；“三日热榜”独立按最近 72 小时的公开趋势信号排序；“为你推荐”才使用本机偏好重排。个人喜好不会改变公共热榜，也不会让低质量来源绕过编辑门槛。

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

- 公共内容池目标为 20–30 条，首页每批固定 10 条。首批目标为大模型、Agent、重要研究、开源项目和产业动态各 2 条。
- 主窗口聚焦最近 72 小时；某一类别不足时，仅对该类别放宽到近 14 天并明确标记为“补充阅读”，随后启用可核查的 GitHub 专项发现。
- 单一来源最多 4 条，避免论文、某家公司或某家媒体淹没整个信息流。
- GitHub 项目必须达到最低关注度并在近期有提交；Star 和活跃度只是发现信号，不等于安全或生产可用。
- 社交平台只用于发现线索，正式条目必须回到公告、论文、仓库或可核查报道。
- 一手来源扩展到 OpenAI、DeepMind、Microsoft Research、Apple ML、NVIDIA、Qwen、ACL Anthology 和重点实验室官方 GitHub；论文继续使用 arXiv，并为 OpenReview、Semantic Scholar、Hugging Face 等增强源预留接口。
- 同一事件只保留一张主卡片；观点、事实和限制在详细页分开写。
- 每条详情必须结论先行。论文先讲研究结论和核心贡献；项目先讲它是什么、做到了什么；模型先讲真正新增的能力；Agent 先讲能完成的任务。实现方式、适用人群、影响和限制随后展开，日期、来源与统计信息放在末尾。
- 没有足够高质量内容时允许少于目标数量，不用重复消息凑数。

详细规则见 `Data/editorial-policy.json`。

## 个性化与隐私

详情点击、打开原文、使用 Codex、喜欢、不感兴趣、收藏、评分和“减少此类”只写入本机：

- `%LOCALAPPDATA%\AIFrontier\feedback-events.jsonl`
- `%LOCALAPPDATA%\AIFrontier\workflow-profile.json`

项目不内置遥测、不上传阅读历史，也不会把反馈直接变成不可检查的自由文本提示词。点击只是弱信号；喜欢、收藏、评分等明确选择权重更高且可撤销。推荐强度随明确反馈逐步增加并设有上限，固定类别覆盖和探索内容仍会保留。

## 三日趋势与热度

- 热榜使用公共信号，不读取个人偏好；同一公司和同一事件不会无限占榜。
- 数据完整时，综合热度由公开讨论、升温速度、来源质量、创新性、技术相关性和新鲜度组成。
- 尚未取得稳定的跨平台讨论快照时，后台只使用可核查来源、创新性、技术相关性和时间衰减，不在阅读界面展示计算声明或伪造评论量。
- 后续公开工作流会逐步保存 GitHub Star/Release 活跃增量、Hacker News 讨论和 Hugging Face 互动快照；这些信号只用于排序，不能替代论文、仓库或官方公告的事实依据。

## 自动更新

客户端启动后会检查固定的 [GitHub Releases](https://github.com/why30263-bot/ai-frontier/releases/latest)，运行期间每 6 小时再次检查一次，与是否接入 Codex 无关。发现新版本时会显示以下选择：

- 立即更新：下载安装器，完成 SHA-256 校验后覆盖安装，不用先卸载旧版本。
- 接受并开启自动更新：当前及以后版本校验通过后自动安装。
- 跳过当前版本：这个版本不再提示，下一个新版本仍会询问。
- 不再提示：继续后台检查但不再弹窗；可在“设置”中恢复更新提醒。
- 稍后：保留提醒，下次检查时再询问。

## 接入本机 Codex（可选）

在“设置”中点击“接入本机 Codex”，应用会检测本机已安装并登录的 Codex CLI，然后创建专用工作区：

- `%LOCALAPPDATA%\AIFrontier\codex-workspace\AGENTS.md`：固定的中文辅助阅读规则。
- `%LOCALAPPDATA%\AIFrontier\codex-workspace\APP_OVERVIEW.md`：软件的采集、推荐、详情和更新逻辑。
- `%LOCALAPPDATA%\AIFrontier\codex-workspace\CURRENT_ARTICLE.md`：用户主动选择的当前资讯。

资讯详情页提供“使用 Codex 深度分析”和“打开原始网站”两个入口。Codex 会按预设协议区分已确认事实、解释与待验证判断，并用入门中文说明重要性、影响、局限和后续观察。应用不会自动把阅读历史上传到项目仓库，也不会要求 Codex 承担采集或软件更新。

## 可选 AI / 工作流增强

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
