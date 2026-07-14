# 参与贡献

感谢帮助改进 AI Frontier。

## 内容来源

- 优先提交官方公告、论文原文、项目仓库或有稳定编辑标准的专业媒体。
- 新来源必须有公开、长期可访问的 RSS、Atom、API 或页面地址。
- 社交账号只能作为线索来源，不能单独支撑正式新闻条目。
- 请说明来源类别、可信度、更新频率以及为什么适合普通读者。

## 代码贡献

1. Fork 仓库并从 `main` 创建分支。
2. 运行 `python scripts/update_feed.py`，确认 JSON 可解析且没有单一来源占满信息流。
3. 运行 `dotnet build AIFrontier.csproj -c Debug -r win-x64 -p:Platform=x64`。
4. 手动检查列表、详细页、窄窗口、反馈和来源跳转。
5. Pull Request 中写清行为变化、验证方法和截图（涉及 UI 时）。

请不要提交 API Key、用户反馈文件、构建输出、抓取缓存或受版权保护的全文。
