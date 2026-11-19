# 公网管理面板：安全审计与部署基线

审计日期：2026-11-17。本次评估覆盖仓库中的独立 Web 宿主、TUI/无头宿主、公开 Web 装配扩展、面板前端和 Linux 部署脚本。保留任意来源 IP 访问 HTTPS 登录页的能力；不以 VPN 或客户端 IP 白名单作为防护前提。

代码审计和本地攻击回归不能证明系统永远无法被攻破。结论针对下述配置：受支持且已打补丁的系统与运行时、可信 HTTPS 证书、强管理员口令、只信任明确配置的反向代理、未泄露的管理员凭据。真实云主机、域名、证书、防火墙及流量清洗服务未在本次本地审计中部署或验收。

## 发现与修复

| 风险 | 原行为及攻击前提 | 修复 |
| --- | --- | --- |
| 严重：首次部署接管 | 未登录可向 `/api/auth/change-password` 提交公开的安装默认口令，远程设置自己的管理员密码 | 无会话初始化需要直接回环连接、回环 Host、没有代理转发信息，并且提供服务器维护文件 `logs/panel-bootstrap.token` 中的短时初始化码；反代与公网请求都拿不到该文件。默认口令不能建立公网会话 |
| 高：公网明文凭据 | 两个宿主固定监听公网 HTTP，Cookie 默认不要求 Secure；部署监听配置不能完整生效 | 非直接本机连接必须 HTTPS，未加密请求返回 426；支持部署 URL/HTTPS 端点配置；systemd 默认只监听回环 |
| 高：撤销后实时数据仍可读取 | WebSocket 建立时通过一次 Cookie 校验，注销、改密后已有连接仍能接收日志与消息 | 接入标准认证与端点授权；到期关闭连接，注销、远程撤销、改密和配置凭据变更立即撤销连接 |
| 中：跨站入口缺少统一校验 | 未统一检查 Origin 和写操作校验头，WebSocket 不受 CORS 保护；同站点恶意子域等场景仍有风险 | API/Hub 校验同源 Origin 与 Fetch Metadata；Cookie 写操作（含登录、注销、改密）要求 `X-Mutter-Request: 1`；无跨源 CORS 放行 |
| 中：认证资源耗尽 | 仅按 IP 记录失败次数，来源表没有容量上限；分布式请求可并发执行昂贵密码哈希 | 全局每分钟最多 20 次认证尝试、同时最多 1 个哈希、同源 5 次失败锁定 15 分钟、来源表上限 4096 并回收过期项；拒绝时返回 429 与 Retry-After |
| 中：凭据及会话配置过弱 | 旧 Bearer 可为短字符串，会话可持续滑动续期，数量不受限 | Bearer 至少 32 字符，仅允许 API，不能打开网页/Hub；公网口令至少 15 位、新密码最多 128 位；会话最多 32 个、绝对有效期最多 12 小时，令牌使用 256 位密码学随机数 |
| 加固：浏览器与文件权限 | 无统一 CSP/防嵌套策略，登录返回地址未排除反斜杠；部署文件权限受调用者 umask 影响 | 外置登录脚本，启用 CSP、禁止 iframe、nosniff、no-referrer、敏感响应 no-store；HTTPS 使用 `__Host-`、Secure、HttpOnly、SameSite Strict Cookie；修正返回地址；配置/数据库/备份权限 600，服务 umask 0077 |
| 阻塞项：运行时停止支持 | .NET 9 已于 2026-11-10 结束支持 | 升级 .NET/ASP.NET Core/EF Core 10 LTS；SDK 10.0.404，最低运行时 10.0.15，允许后续同版本补丁升级 |

登录、Cookie、Bearer、来源校验、异常处理、静态资源和 SignalR 由三个宿主入口共用同一套管道，业务端点另有标准授权要求。全局请求体上限 1 MiB，认证接口上限 16 KiB。表情上传接口单独允许 17 MiB 请求体，其中图片文件最多 16 MiB。请求头上限 16 KiB，HTTP 连接 256、升级连接 64、HTTP/2 单连接并发流 16，SignalR 单条消息 16 KiB。限额用于约束单进程资源，不代表能够抵挡无限规模流量。

前端没有发现把聊天正文或日志直接传给 `v-html`/`innerHTML` 的路径。数据库业务查询使用参数化 EF 查询，更新接口需要管理员认证；更新源继续校验 HTTPS、签名/校验和与出站地址。管理员可配置内网 AI/搜索服务，故私网出站并未一刀切禁止；这不是对匿名用户开放的代理。系统代理默认关闭，开启后必须信任代理本身。

## 公网部署

建议路径：`任意来源 IP → HTTPS 反向代理:443 → 127.0.0.1:5188 → 面板认证 → API / Hub`。

1. 使用受支持并更新过的 Linux。运行 `deploy/install.sh` 安装；服务使用独立的 `mutter-bot` 用户，不以 root 运行。systemd 模板只在回环监听，并限制可写目录、能力、设备访问和核心转储。已有安装通过面板更新到新版本后，程序自身默认也只监听 `127.0.0.1:5188`（旧 unit 未更新时同样生效）；显式 `ASPNETCORE_URLS`/Kestrel 配置仍被尊重。systemd 单元的其他加固项在重新执行 install.sh 时更新，不要求为升级手动改 `/etc`。
2. 通过 SSH 转发完成首次设置：`ssh -L 5188:127.0.0.1:5188 user@server`，在本机打开 `http://127.0.0.1:5188/login.html`。用安装默认口令进入本机初始化流程，同时填写服务器上 `/opt/mutter-bot/logs/panel-bootstrap.token` 中的初始化码（30 分钟有效，进程重启后更换；成功改密即失效），设置密码管理器生成的长随机口令。默认口令、短于 15 位的旧口令不能用于公网登录；旧短口令同样走本机强制改密流程完成迁移。
3. 将 `deploy/nginx-mutter.conf.example` 安装到 Nginx 的 `http {}` 配置范围，替换域名和可信证书路径。启用前先处理其注释中的前置检查（nginx >= 1.19.4；发行版默认站点的 `default_server` 冲突），`nginx -t` 通过后再 reload。证书必须匹配访问的服务域名或服务 IP；不要求限制访问者的 IP。不要把 Vite 开发/预览服务器直接开放到公网。
4. 安全组/防火墙只向公网开放 443、可选的 HTTP 跳转端口 80 和必要的运维端口；5188 不对公网开放。这里限制的是服务器入口端口，不是管理面板访问者的 IP。
5. 同机 Nginx 默认使用 `PanelSecurity:TrustedProxies` 的 `127.0.0.1`/`::1`。若代理在另一台机器，只添加它的精确地址，并让后端端口仅接受代理流量。不得设为信任全部地址；不要使用 `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` 取消可信代理限制。代理必须覆盖 X-Forwarded-For/Proto，不能照抄客户端自报值。
6. 将 `AllowedHosts` 设置为实际服务域名/服务 IP；如前置 CDN，只有明确可信的 CDN 出口才能提供真实客户端地址。默认保持 `ApiAuth:Token` 为空。确需自动化时，生成至少 32 字符的随机令牌并视为管理员凭据保管，仅经 HTTPS 的 Authorization 头发送；令牌变更独立于管理员改密。

Windows 或其他托管方式可用 `ASPNETCORE_URLS` 绑定回环，或配置 Kestrel 的有效 HTTPS 端点；使用低权限服务账户和仅该账户可读的配置/数据库 ACL。框架依赖包必须安装 **ASP.NET Core Runtime 10.0.15 或更新的 10.0 补丁**；从旧 .NET 9 自包含包升级需使用完整包，不能只复制新 DLL。

配置里保留的 `PanelAuth:RequireSecureCookie` 不会关闭 HTTPS Cookie 的 Secure 属性。浏览器在 HTTPS 上使用带 `__Host-` 前缀的新 Cookie，旧 Cookie 不作为 HTTPS 会话使用，升级后需要重新登录。命令行使用 Cookie 调用写 API 时也要提供 `X-Mutter-Request: 1`；有效 Bearer 的非浏览器 API 调用不要求此头，但仍受 HTTPS 和 Origin 策略限制。

## 验证与持续维护

本地安全回归入口：`dotnet run --project tools/ProactiveCheck -- --panel-security-only`。测试启动临时 HTTP/HTTPS 宿主，使用合成凭据和仅测试信任的临时证书，真实发送攻击请求及 WebSocket 握手；不连接真实机器人或生产服务。

已覆盖：公开默认口令接管与路由变体、本机初始化（含初始化码缺失/错误/一次性消费/过期轮换）、无转发头同机反代、伪造 X-Forwarded-*、回环 DNS rebinding Host、匿名 API/页面保护、Origin/null/Fetch Metadata、登录 CSRF、自定义头、Cookie 安全属性、弱 Bearer、Bearer 网页/Hub 隔离、超长登录请求、WebSocket 跨站拒绝及注销断开、改密/外部轮换撤销、哈希并发/全局速率/IP 锁定/来源表容量、会话容量与绝对期限。浏览器回归另外在严格 CSP 下检查实际登录脚本、主题、初始化及恶意返回地址（含 URL 点段规范化变体）。

依赖扫描发现 Axios 的 Node 适配器间接依赖 `follow-redirects` 1.16.0 存在高危公告；未据此认定生产浏览器路径可直接利用，已更新至 1.16.3。开发依赖 Vite 6.4.4 的文件访问限制绕过已通过更新至 6.4.6 消除。更新后 npm（含开发依赖）和 NuGet（含传递依赖）扫描无已知漏洞报告；CI 与发布流水线加入漏洞门禁。无告警仅代表所用公告库在扫描时的结果。

仍需在真实部署验收：证书链、只暴露所需端口、代理头覆盖、错误来源无法直连上游、日志/备份访问权限、配置确实生效。流量型 DDoS、未知漏洞、操作系统漏洞、凭据泄露和管理员主动授权恶意更新源不能由一次应用审计彻底消除。长期公网运行应持续更新系统与运行时，并由云防护/CDN/WAF 承担大规模流量攻击的入口防护。

参考：[微软支持周期](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)、[SignalR 安全](https://learn.microsoft.com/en-us/aspnet/core/signalr/security?view=aspnetcore-10.0)、[SignalR 认证失效处理](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0)、[可信代理](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0)、[OWASP 跨站请求防护](https://cheatsheetseries.owasp.org/cheatsheets/Cross-Site_Request_Forgery_Prevention_Cheat_Sheet.html)。
