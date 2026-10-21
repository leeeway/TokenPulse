# 2026-10-21 上线记录

- 站点：https://lwblog.cn/projects/；提供四个项目及对应英文页面。
- CardPulse 网页版：https://lwblog.cn/apps/cardpulse/，从 `v0.2.0` / `5c10e100126350414d8898444e7f243ed4c80a06` 构建。
- TokenPulse 下载分别指向 `mac-v0.1.2` 和 `windows-v0.4.2` 预览版本，公开附件请求均返回 200。
- 四个 GitHub 仓库的 homepage 指向各自项目介绍页；现有应用源码没有修改。

部署保留备份 `/var/backups/lwblog.cn-before-projects-20261021T145542Z`，静态 release 位于 `/var/www/lwblog-releases/20261021T145542Z-projects`。站点原有 CSP 保持不变，仅 CardPulse 路径采用应用专属 CSP 和模块 MIME。Nginx 配置测试通过，仅执行 reload；Nginx 主进程 PID 保持 719，代理主进程 PID 保持 680，两个服务均为 active。

线上 Chrome 验收通过：12 个中英文页面、320/390/1440 像素布局、图片、语言导航、禁用 JavaScript 后的内容与下载入口，以及 CardPulse 示例数据和合成 PDF 导入。PDF 模块 worker 在实际 CSP 下加载正常，无页面异常或应用控制台错误。原有技术雷达的实际日报、周报链接、RSS 和中英文手机布局回归通过。

截图和临时验收产物位于本地 `dist/lwblog-live-qa/`，不纳入 Git。展示和导入测试均使用示例数据。构建的既有 Vite 插件存在 esbuild 配置弃用提示，本次未调整站点依赖。
