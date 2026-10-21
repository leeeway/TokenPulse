# 2026-10-21 上线记录

- 站点：https://lwblog.cn/projects/；提供四个项目及对应英文页面。
- CardPulse 网页版：https://lwblog.cn/apps/cardpulse/，从 `v0.2.0` / `5c10e100126350414d8898444e7f243ed4c80a06` 构建。
- TokenPulse 下载分别指向 `mac-v0.1.2` 和 `windows-v0.4.2` 预览版本，公开附件请求均返回 200。
- 四个 GitHub 仓库的 homepage 指向各自项目介绍页；现有应用源码没有修改。

部署保留备份 `/var/backups/lwblog.cn-before-projects-20261021T145542Z`，静态 release 位于 `/var/www/lwblog-releases/20261021T145542Z-projects`。站点原有 CSP 保持不变，仅 CardPulse 路径采用应用专属 CSP 和模块 MIME。Nginx 配置测试通过，仅执行 reload；Nginx 主进程 PID 保持 719，代理主进程 PID 保持 680，两个服务均为 active。

线上 Chrome 验收通过：12 个中英文页面、320/390/1440 像素布局、图片、语言导航、禁用 JavaScript 后的内容与下载入口，以及 CardPulse 示例数据和合成 PDF 导入。PDF 模块 worker 在实际 CSP 下加载正常，无页面异常或应用控制台错误。原有技术雷达的实际日报、周报链接、RSS 和中英文手机布局回归通过。

截图和临时验收产物位于本地 `dist/lwblog-live-qa/`，不纳入 Git。展示和导入测试均使用示例数据。构建的既有 Vite 插件存在 esbuild 配置弃用提示，本次未调整站点依赖。


## 2026-10-21 Review 修复发布

- 应用源码提交 `3968eff2a59db91f63be9e11b52311f5b2db73bf`。Mac 0.1.3、Windows 0.4.3 分别发布预览包及 SHA-256 清单，官网与 README 下载链接已同步。
- CLI 使用实际窗口、重置时间与套餐字段；Windows 日志先展示、Ollama 随后更新。两端加入兼容估算版本、每百万 Token 费率与未知模型回退说明；未更改现有估算价格。
- Mac 18 项测试与 Release 构建通过，本地应用已更新启动。Windows 核心 35 项测试、WPF/托盘启动及全部截图场景通过：[Windows CI](https://github.com/leeeway/TokenPulse/actions/runs/39623788784)。[Mac CI](https://github.com/leeeway/TokenPulse/actions/runs/39623788788)、[网站 CI](https://github.com/leeeway/TokenPulse/actions/runs/39623788873) 均通过。
- 官网五个下载条目的显示版本、附件及校验文件在线核对通过；新增网站 CI 在集成目录变更时自动校验。
- 网站改用 `/var/www/lwblog-current` 指针原子切换完整版本；旧的哈希资源保留。切换、回退及无效版本保护等 4 项测试通过。
- 当前 release：`/var/www/lwblog-releases/20261021T151833Z-projects`；备份：`/var/backups/lwblog.cn-before-projects-20261021T151833Z`。可使用备份内 `rollback-static.sh` 独立恢复上一个版本。
- 部署前后代理 PID 保持 680，Nginx 主进程保持 719，主站 CSP 未修改。
