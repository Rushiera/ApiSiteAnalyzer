# ApiSiteAnalyzer

API 站用量分析器（**C# / .NET 8**）。把 API 站的用量日志**全量**拉全、落库留存、做多维分析——取代在站点网页里逐页翻。

首站：贤鱼 API（`https://api.xyjun.fun`，New API v1.0.0-rc.40）。

> 架构与选型 → `CCBP:Project/ApiSiteAnalyzer/design-ApiSiteAnalyzer.md` §〇。

## 目录

```
ApiSiteAnalyzer/                主项目
  Config/                       配置加载（严格模式——未知键 / 类型错报错退出）
  Browser/                      CDP 通道（chrome 启动器 / 会话 / 受控实例中心）
  Collect/                      站点会话（登录探测 + 分页拉取 + 口径快照）+ 逐页落库器
  Sites/                        站点适配器（IApiSite + NewApiSite）
  Store/                        SQLite 库（幂等写入 + 增量比对 + 聚合查询）
  Web/                          Minimal API 端点 + 自动采集器 + wwwroot/index.html
config.json                     站点清单 + 端口 + chrome 路径
data/                           运行时生成：usage.db · settings.json · browsers.json · profiles/<站点>/
启动.bat                        启动器：停旧面板 → 起最新槽位
ApiSiteAnalyzer_A.exe           槽位 A（部署产物，随仓走）
ApiSiteAnalyzer_B.exe           槽位 B
```

## 运行

双击 `启动.bat`（或命令行跑 `ApiSiteAnalyzer_A.exe serve`）。子命令：

```
ApiSiteAnalyzer_A.exe check            # 探测登录态（含真实余额）
ApiSiteAnalyzer_A.exe fetch            # 增量拉取（追平历史即停）+ 口径快照入库
ApiSiteAnalyzer_A.exe fetch --full     # 全量重扫到底（不提前停止）
ApiSiteAnalyzer_A.exe serve            # 起面板（默认，双击 exe 等同）
ApiSiteAnalyzer_A.exe stats            # 库内计数
ApiSiteAnalyzer_A.exe sites            # 站点清单
```

开发态用 `dotnet run --project ApiSiteAnalyzer -- <子命令>`。

**配置查找**：当前工作目录优先，缺则回落 exe 同级；两处都没有即报错退出（不静默起空面板）。
配置内的相对路径（`data` 目录）一律相对**配置文件所在目录**解析，故可在任意工作目录调用。

## 增量拉取（追平即停）

站点列表**新的在前**（`logs.id desc`）。据此：逐页比对库内既有记录，
**连续 20 条**与库内完全一致（含 token / 额度 / 耗时等全部业务字段）即认定其后都是已入库的历史——
停止后续请求。省下的是页数，也就是站点的刷新频控额度。

实测（2026-10-01，库内 3000+ 行）：

| 情形 | 全量口径 | 增量口径 |
|:--|:--|:--|
| 30 页 / 2995 条 | 30 页全拉 | — |
| 有 102 条新记录 | 30 页 | **1 页**（首页 100 条里第 99 条起连续一致） |
| 无新记录 | 30 页 | **1 页**（首页 98 条一致即追平） |

判据（服务端打印）：
`完成：拉取 1 页 / 100 条 · 新增 2 条 · 库内 2996 行 · 已追平历史（提前停止）`

- **比对在写入之前**——写入后旧行已被新值覆盖，"是否与库内一致"就问不出来了
- **不比 `remote_id`**——站点展示序号每次查询从 1 重排，拿它判定会误报「有变化」
- 想强制重扫历史：CLI 加 `--full`；面板弹窗里点「全量拉取一次」

## 自动采集（面板）

**双击「拉取数据」**打开设置弹窗：

| 项 | 说明 |
|:--|:--|
| 自动采集 | 勾选框，**默认勾选** |
| 间隔 | 滑块 **5–119 秒**，**默认 29 秒** |
| 增量拉取 | 勾选框，默认勾选——按上一节的「追平即停」口径拉 |
| 全量拉取一次 | 按下的那一次不提前停止，用于重扫历史 |

- **单击**「拉取数据」= 按当前设置拉一次；**双击** = 打开设置弹窗
- 设置存 `data/settings.json`（运行时数据，不入仓；`config.json` 是部署期声明，面板不回写它）
- **计时归服务端**（`Web/AutoCollector.cs`）：关掉页面照样按时拉。
  若计时挂在页面上，关掉页面就悄悄不跑了——「设置说开着、实际没跑」正是静默失败
- 顶部状态显示「自动采集 29s（下次 11:03:22）」，页面刷新后是同一个时刻
- 间隔越界一律夹进 5–119；`auto` / `interval` 缺值或非法值**报错拒绝**，不静默回落默认值

## 更新（双槽部署）

**运行中的 exe 锁着自己的文件**——直接覆盖会失败。故产物分 A / B 两槽轮换：

1. 发布到**仓库外的暂存区**（`ApiSiteAnalyzer.csproj` 的 `PublishDir` 已指向 `..\..\CatTemp\asa-publish\`）：

```
dotnet publish ApiSiteAnalyzer\ApiSiteAnalyzer.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

2. 把产物移进**空闲槽**（正在跑的那一槽不动）：

```
move CatTemp\asa-publish\ApiSiteAnalyzer.exe ApiSiteAnalyzer_B.exe
```

3. 双击 `启动.bat`——它按修改时间取**最新的槽位**启动，先停掉占着 8766 端口的面板。

槽位 exe **随仓走**（各 2.3 MB，体积小、方便取用）——沿用原「exe 入库」定案，仅从单件改双件。

`启动.bat` **不杀浏览器实例**：登录态在浏览器用户目录里，新面板启动时会自动接管（读 `data/profiles/<站点>/DevToolsActivePort`）。

**版本自证**：面板右上角显示当前运行版本；若另一槽有更新的产物，会标出「有新版本」。
鼠标悬停可看到正在运行的可执行文件路径。跑的是哪份产物，一眼可见，不用口头核对。

## 登录

**程序不存账号密码。** 登录归人：

1. 面板点「去登录」（或 CLI 跑 `check` 时按提示）→ 程序打开一个**可见的**浏览器窗口并切到登录页
2. 你在那个窗口里登录（账号密码 / 扫码 / 两步验证都行）
3. 回到面板点「检查登录」→ 显示用户名与**真实余额**即成功
4. 点「拉取数据」→ 增量入库（追平历史即停）

登录态存在**浏览器用户目录**里（`data/profiles/<站点键>`）——只要不删这个目录，下次免登录。

**凭据不出浏览器**：所有需要凭据的请求都在页面上下文里发出（页面内 `fetch`），
access_token 只活在页面里（`window.__asaToken`），从不回传程序进程、从不落盘。

## 三类数字（别混淆）

面板把三种口径**分开放**，因为它们的含义不同：

| 位置 | 来源 | 含义 |
|:--|:--|:--|
| **真实余额**（金色大行） | `GET /api/user/self` → `data.quota` | **这是真实余额**——dashboard/overview 页「剩余额度」显示的就是它 |
| **站点口径**（第二行） | `GET /api/data/self`（按小时聚合）→ 前端汇总 | dashboard/models 页那套「总数 / 总额度 / 总 TOKEN 数 / 平均 RPM·TPM」——**站点自己的聚合口径** |
| **本地汇总**（卡片 + 图表） | 本地 SQLite 逐条汇总 | 从落库的全量明细算出来的——**分析用这一套** |

站点口径与本地汇总**未必相等**（实测差约 1%：站点 2177 次 / 本地 2196 次）——因为站点按自己的窗口与聚合规则算。
两者都展示、互不覆盖。

## 站点实况与判例

1. **`/usage-logs/common` 是前端路由，不是接口**——直接 curl 返回 React 空壳 HTML。真实数据在
   `GET /api/log/self?p=N&page_size=100`。
2. **面板接口只认 `Authorization: Bearer`**，不认 cookie——页面内裸 fetch 会 401。
   cookie（`new_api_refresh`，HttpOnly）只用于 `POST /api/user/auth/refresh` 换 access_token。
3. **刷新接口有频控**——实测连续调用会 429 + `retry-after: 492`（秒）。故设计上**一轮拉取只刷一次**，
   token 缓存在页面上下文里；401 才重刷，且遵守冷却窗口。
4. **分页口径**（上游 `common/page_info.go` 与本站实测一致）：`p` 1 起、`page_size` 上限 100、
   排序 `logs.id desc`（新的在前）；`total` 字段随响应返回，翻到 `ceil(total/100)` 即到底。
5. **站点返回的 `id` 是展示序号，不是稳定键**——每次查询从 1 重排。
   拿它当主键会导致整库错位更新（实测：第二次拉取时 id 1 被新记录占用，覆盖掉旧记录）。
   **改用 `request_id` 作主键**（唯一且稳定）；缺失时退化为「时刻+模型+用量」组合键。
6. **全量拉取不带时间区间**——站点默认只给当天（`/usage-logs/common` 页默认 24 小时窗口）。
   要历史就**不带** `start_timestamp` / `end_timestamp`。
7. **token 捕获走页面钩子**——`Page.addScriptToEvaluateOnNewDocument` **必须在导航之前**调用；
   注入列表会累积，故每个用户目录只注入一次（登记在 `browsers.json` 的 `hookedSites`，跨进程持久），
   且补注入后要重新加载页面让钩子生效。
8. **不要重复导航**——页面已在站点域内时重新导航会清掉 `window.__asaToken`，逼出一次多余刷新（撞频控）。
   判据：`location.href` 已以站点根开头 → 不导航。
9. **登录必须在通道标签页里做**——程序驱动的只有一个标签页（登记在 `browsers.json` 的 `channelTargetId`）。
   你在浏览器里另开标签页登录，程序看不到（那是个独立上下文）。所以「去登录」按钮会把**通道标签页本身**导航到登录页。
10. **`other` 字段是字符串化的 JSON**——`cache_tokens` / `frt`（首字延迟毫秒）/ `upstream_model_name`
    在里面；解析失败按缺项处理，不影响主字段。
11. **缓存命中率口径**：本站 `prompt_tokens` **不含**缓存部分，故命中率 = `cache_tokens / (prompt_tokens + cache_tokens)`。
12. **`frt`（首字延迟）实测出现负值**——全量 2199 条里有 2 条为 `-1000`、3 条为 `0`。
    聚合时按 `> 0` 过滤（原值保留可追溯）。
13. **模型名大小写不统一**——实测同一上游三种写法（`DeepSeek-V4.1-Flash-gq` / `DeepSeek-V4.1-Flash` / `Deepseek-V4.1-Flash`）。
    按原值入库、不归一——归一会掩盖站点的模型映射实况。
14. **CDP 指令必须有超时**（2026-10-01 判例）——`CdpSession.SendAsync` 原先用 `CancellationToken.None` 等回执，
    浏览器实例一换（旧端口死掉）指令就**永久挂起**；面板的浏览器操作共用一条串行队列，
    一个挂起把「检查登录 / 去登录 / 拉取」全部堵死，表现为「一直在检查登录态」。
    现：单条指令 60 秒上限（须高于页面侧最坏耗时 = 刷新 15s + 取数 15s）；通道关闭时未决指令立即失败；
    队列等待另有 120 秒上限。**失败必须可见**——超时出声，不静默挂起。
15. **用户目录是单例**（2026-10-01 判例）——同一 `--user-data-dir` 起第二个 chrome 会被单例转发吃掉，
    新进程**静默退出**（stderr 无 `DevTools listening on`，只报"进程提前退出"）。
    跨进程场景（CLI 与面板各有内存副本）必须**先接管再启动**：读 chrome 自己写的
    `data/profiles/<站点>/DevToolsActivePort` 拿端口，探活通过即接管，不起第二个。
    接管失败且目录确被占用 → 出声给出 PID，不擅自杀用户进程。
16. **运行中的 exe 锁文件**——直接发布到仓库根会失败（`The process cannot access the file`）。
    产物走**双槽 + 仓库外暂存区**：publish 到 `CatTemp/asa-publish/`，再 move 进空闲槽，由 `启动.bat` 起最新槽位。
17. **面板不杀浏览器实例**——登录态在浏览器用户目录里，杀面板不动浏览器；新面板启动时自动接管既有实例（判例 15）。
    所以更新面板**不影响登录**，不需要重新登录。
18. **HTTP 请求体只能读一次**（2026-10-01 判例）——`ReadFieldAsync` 原先每调一次就把请求体读一遍，
    第二个字段起拿到的是**空串**。症状隐蔽：`/api/settings` 三个字段，`auto` 读到了、`interval` 读到 0——
    表现为「保存了但没生效」，不报错。现把整份字段缓存进 `HttpContext.Items`，同一请求内复用（判例详见
    `CCBP:Project/ApiSiteAnalyzer/design-ApiSiteAnalyzer_log.md` §七）。
19. **有带参构造的类不能直接当反序列化目标**——`PanelSettings(string path)` 被 System.Text.Json 当成
    反序列化构造函数，而 `path` 没有对应属性 → **启动即抛**（面板起不来，CLI 不受影响）。
    落盘结构拆出独立无参 DTO（`PanelSettings.SettingsFile`），序列化面与运行态对象解耦。
20. **面板设置与部署配置分家**——`config.json` 是部署期声明（严格校验、未知键报错、启动链只读）；
    面板改的自动采集开关 / 间隔属**运行期用户设置**，落 `data/settings.json`。启动链不回写部署配置
    （全量回写 + 混装文件 = 静默吞字段）。

## t/s 口径复算（本地自算列）

站点 `/usage-logs/common` 每行显示一个 `xx t/s`。**实测复算结论**：

```
t/s = completion_tokens / use_time   （四舍五入取整）
```

**六条样本逐条对上**（站点显示值 vs 本地复算）：

| 站点显示 | completion | use_time | 复算 | 取整 |
|--:|--:|--:|--:|--:|
| 24 t/s | 121 | 5 | 24.2000 | 24 ✅ |
| 13 t/s | 53 | 4 | 13.2500 | 13 ✅ |
| 193 t/s | 3091 | 16 | 193.1875 | 193 ✅ |
| 522 t/s | 188351 | 361（6m1s） | 521.7479 | 522 ✅ |
| 166 t/s | 1491 | 9 | 165.6667 | 166 ✅ |
| 57 t/s | 284 | 5 | 56.8000 | 57 ✅ |

**命中 6/6**。反例排除：

- **截断取整**（`floor`）只对 3/6 ——排除
- **扣掉首字延迟**（`completion / (use_time - frt)`）差得远（93 vs 24）——排除

**落库形态**：`speed_tps` 列（`REAL`），由本地在入库时自算（`completion_tokens / use_time`）。
全库一致性实测：与公式不一致的行数 **0**、负值 **0**（`use_time=0` 的 2 条按 0 计）。
面板「最近记录」有独立的 **t/s** 列，卡片里有「平均速率」。

**速率分布实测**（2196 条消费记录）：

| 区间 | 条数 |
|:--|--:|
| 100–200 t/s | 1092 |
| 50–100 t/s | 483 |
| 20–50 t/s | 347 |
| ≥200 t/s | 153 |
| <20 t/s | 119 |
| 0（use_time=0） | 2 |

## 设计要点

- **适配器可插拔**：站点差异全收在 `IApiSite` 实现里——采集链 / 库 / 分析面 / 面板零改动。
- **幂等入库**：主键 `(site_id, log_key)`，`log_key` 取 `request_id`——重复拉取只更新、不重复。
- **逐页落盘**：每页到手即写库，进度可见（面板状态条 / CLI 逐行打印）。
- **增量追平**：列表新的在前 → 连续 20 条与库内一致即停（`Store/Db.MatchExisting` + `Collect/PageWriter`）。
- **落库口径唯一**：CLI 与面板共用 `PageWriter`——两处各写一份必然漂移。
- **自动采集归服务端**：`Web/AutoCollector` 每秒对表；关掉页面照样跑（计时挂页面 = 关页面就静默不跑）。
- **库结构版本化**：`PRAGMA user_version` 核对；不匹配则备份旧库（`.bak-vN-时间戳`）并重建，出声不静默。
- **失败可见**：未登录 / 拉取中断 / 接口异常 / 配置错误一律出声，不静默回落成空表。
- **WAL + busy_timeout**：WAL 未生效即抛错（不静默退回 delete 模式）。
- **面板数字可复算**：每个聚合都能用一条 SQL 在库上复算。
