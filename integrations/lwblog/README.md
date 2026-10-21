# lwblog.cn 项目入口

本目录保存现有 Astro 主站的增量集成源码，不包含服务器凭据。主站继续静态托管；CardPulse 在浏览器本地处理账单，LogMind 只展示介绍和源码。

## 入口

- `/projects/`：全部精选项目；首页展示 `featured: true` 的项目。
- `/projects/tokenpulse/`、`/projects/cardpulse/`、`/projects/daily-trend-radar/`、`/projects/logmind/`：项目介绍、使用入口、下载和源码。
- `/en/projects/` 及对应英文详情页。
- `/apps/cardpulse/`：从 CardPulse `v0.2.0` 源码构建的独立网页版，加入返回项目页的导航，不修改桌面客户端。

## 应用到现有网站

使用 Python 3.12+ 和现有网站支持的 Node.js 环境；CardPulse 构建复用版本一致的依赖，缺失时执行 `npm ci --ignore-scripts`。

```sh
python3 integrations/lwblog/install.py --site-dir /path/to/leeway-apple-src
python3 integrations/lwblog/build-cardpulse.py --repo /path/to/CardPulse --output /path/to/leeway-apple-src/public/apps/cardpulse
cd /path/to/leeway-apple-src
npm run build
```

安装器只写本集成的组件、页面、资源及首页作品区，并在 `.lwblog-backups/` 保存原文件。原首页、视差场景、雷达和语言切换继续保留。再次安装可更新自己的集成区块。

新增项目修改 `site/src/data/projects.json`，填写双语介绍、真实源码地址、主入口和功能；再次应用并构建即可生成目录和详情页。`featured` 控制是否出现在首页。下载指向明确的平台版本，不使用可能混淆 Mac/Windows 的统一 `latest` 链接。数据没有自动定时同步；后续发布新版本时更新版本清单并部署。

## 部署要求

先备份线上站点并在独立 release 目录准备文件，检查后再切换或安装页面。保留其他现有目录与资源，不能删除主站以外的服务。部署前后检查 Nginx 和代理服务状态/PID。

CardPulse 的 HTML、模块 worker 与 WASM 需要正确 MIME 类型，`.mjs` 使用 JavaScript MIME、`.wasm` 使用 `application/wasm`。仅 CardPulse 路径的 CSP 允许 `script-src 'self' 'wasm-unsafe-eval'`、`worker-src 'self'`，并在 `connect-src` 中允许 `https://api.frankfurter.dev` 以支持用户主动更新公开汇率。保留其余现有安全头及主站 CSP；不要加入 `unsafe-eval` 或第三方脚本。

`nginx-cardpulse.conf.example` 提供该 HTTPS 站点的路径配置，`nginx-cardpulse-security.conf` 提供应用专属安全头。应先检查现有 location 和安全头，备份配置后再合并；`nginx -t` 成功后仅 reload Nginx。禁止修改或重启代理服务。

网页版保险库以浏览器和站点 origin 为边界，与 GitHub Pages、本地开发地址或桌面版数据不互通。换地址前应备份；不自动读取或迁移任何用户账单。

## 验收

检查中英文目录/详情页、320/390/1440 像素宽度、导航、真实下载地址、关闭 JavaScript 后的基本内容，以及 CardPulse 的示例数据、CSV/PDF 导入和子路径 worker。确认无横向溢出、无页面异常与 CSP 拦截。截图中的用量与账单均为示例数据。

在安装了 Playwright 的环境运行 `node integrations/lwblog/check-browser.mjs https://lwblog.cn`。可用 `PLAYWRIGHT_MODULE` 指定模块路径、`BROWSER_EXECUTABLE` 指定 Chrome 路径、`LWBLOG_QA_OUTPUT` 指定截图目录。脚本只使用示例数据和合成 PDF，不读真实账单；上线验收应使用真实响应头。
