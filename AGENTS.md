# ONVIF 运动录像 → WebDAV

摄像头检测到画面变化就录像，录完上传 WebDAV，本地不留副本；自带局域网网页按日期/时间回看；WinForm 负责配置，可最小化到托盘。

## 一句话架构

```
ONVIF PullPoint 事件 ──► CameraWorker ──► ffmpeg 环形缓冲(2s TS 段)
                                    │            │
                                    │      事件结束 → concat 合成 MP4 + 缩略图
                                    ▼            ▼
                              UploadQueue ──► WebDAV（clips / thumbs / index）
                                                 │
                                    网页(Kestrel) ── 按需下载到本地 LRU 缓存 → Range 播放
```

## 项目结构

| 项目 | TFM | 职责 |
|---|---|---|
| `src/OnvifWebDav.Core` | net9.0 | 配置、日志、ONVIF SOAP、ffmpeg、WebDAV、索引、上传队列、CameraWorker |
| `src/OnvifWebDav.Web` | net9.0 | Kestrel 站点（`OutputType=Library`，只导出 `WebHostFactory.Build`） |
| `src/OnvifWebDav.App` | net9.0-windows | WinForm 配置界面 + 托盘 + 单实例 + 自启 |
| `tools/OnvifWebDav.Probe` | net9.0 | 无界面诊断 CLI（没有摄像头/NAS 时的主要验证手段） |

**不要引第三方 NuGet 包**：ONVIF、WebDAV、鉴权全部手写，理由是本地缓存里没有可用包 + `api.nuget.org` 会跳到 `nuget.azure.cn` 镜像，版本体验不稳定。当前全解决方案只有 FrameworkReference，可离线 restore。

## 配置契约

位置：`%LocalAppData%\OnvifWebDav\config.json`（原子写：tmp → `File.Replace`）
密码：DPAPI（`CryptProtectData`，当前用户作用域）→ Base64。换 Windows 用户后解不开，会退化为空密码。

```jsonc
{
  "cameras": [{
    "id": "cam1", "enabled": true, "name": "大门",
    "host": "192.168.1.64", "onvifPort": 2020,
    "username": "admin", "passwordEnc": "...",
    "rtspUrl": "",          // 留空 = ONVIF 自动获取；手填时不带账号密码
    "stream": "main",       // main / sub
    "rtspPort": 554, "snapshotUrl": ""
  }],
  "webDav": { "url": "", "username": "", "passwordEnc": "", "rootPath": "/onvif",
              "ignoreCertErrors": false, "timeoutSeconds": 100 },
  "recording": { "mode": "RingBuffer", "preRollSeconds": 8, "silentStopSeconds": 5,
                 "minSeconds": 5, "maxSeconds": 300, "segmentSeconds": 2,
                 "transcodeToH264": false, "autoTranscodeHevc": true, "recordAudio": true },
  "web": { "port": 8080, "passwordEnc": "", "cookieKey": "", "cacheGb": 2, "sessionDays": 30 },
  "system": { "autoStart": false, "logLevel": "Info", "spoolLimitGb": 20 }
}
```

## WebDAV 数据契约

```
{root}/{camId}/index/2026-11-09.json        ← 权威索引（最后一次写）
{root}/{camId}/clips/2026/11/09/153012_ab12cd.mp4
{root}/{camId}/thumbs/2026/11/09/153012_ab12cd.jpg
```

索引条目字段：`id, cameraId, startUtc, endUtc, durationMs, sizeBytes, path, thumb, videoCodec, hasAudio, peakMotion, eventCount, continuedFrom`。

**上传顺序不可颠倒**：视频 → 缩略图 → 索引。索引是"文件完整"的唯一标记：
- 索引有记录但文件 404 → 网页显示文件缺失
- 文件在但索引无记录 → 网页忽略（由 `RemoteClipIndex.RebuildDayAsync` 自愈）
- 视频先传 `*.mp4.part`，校验长度后 `MOVE`；服务端不支持 MOVE（405/501/403）时自动降级为直接 PUT
- 索引写入用 `If-Match` 乐观锁，412 重读合并，最多 3 次

路径全部 ASCII（`cam1/clips/...`），中文只出现在界面。

## 关键坑（改代码前先看这里）

1. **ONVIF 默认关闭**：TP-Link 必须在摄像头网页 → 设置 → 网络设置 → 网络服务 → ONVIF 手动开启。固件 1.6+ 用端口 80，旧版 2020，代码里两个都会试。
2. **`GetSystemDateAndTime` 必须匿名发**，带 Security 头部分固件直接 400。拿到设备时间后算偏移，之后所有 PasswordDigest 的 `Created` 都用校正时间。
3. **SOAPAction 旧版带 `Request` 后缀**：`.../events/wsdl/EventPortType/CreatePullPointSubscriptionRequest`。所以 `OnvifSoapClient.CallAsync` 接收 action 候选数组逐个试。
4. **PullPoint 返回的订阅地址常是 `localhost`**：必须用 `UriBuilder` 保留 path、把 Scheme/Host/Port 换成用户配置的地址，否则后续 PullMessages 全连不上。
5. **PullMessages 是长轮询**：`HttpClient.Timeout` 必须 `InfiniteTimeSpan`，超时用 CTS 控，否则 `TaskCanceledException` 和长轮询互相打架。
6. **订阅后 3 秒内的事件不触发录制**：设备会 dump 一批 `PropertyOperation="Initialized"` 的初始状态，里面可能带 `IsMotion=true`。
7. **ffmpeg 不能加 `-nostdin`**：要靠 stdin 写 `q` 优雅收尾，直接 Kill 会把最后一段 TS 写坏。
8. **静默卡死**：RTSP 断流时 ffmpeg 进程活着但不出帧。用 `-progress pipe:1` 的 `out_time=` 做心跳，20 秒无心跳判定卡死并重启。
9. **最新那个 TS 文件可能还在写**：`SegmentRing.Collect` 永远排除最后一个文件；合成前要等环形缓冲覆盖到结束时间。
10. **MP4 装不下 G.711**：TS 段可以 `-c copy` 保留，合成时必须 `-c:a aac`。
11. **H.265 浏览器放不了**：主码流默认可能是 HEVC。优先让用户在摄像头网页改 H.264；否则 `autoTranscodeHevc` 走 libx264 转码。直封 HEVC 必须加 `-tag:v hvc1`。
12. **Cookie 不能设 Secure**：局域网是 http，设了 Cookie 根本不生效（`SecurePolicy=None`）。
13. **`Microsoft.AspNetCore.App` 的 FrameworkReference 不要手动加**：`Sdk.Web` 隐式包含，手动加会刷 `NETSDK1086`。
14. **Web 项目必须 `OutputType=Library`**，否则它自己生成 `Main` 和 WinExe 冲突。
15. **`StaticWebAssetsEnabled=false`**：Web 项目已经关掉，靠显式 Content 把 `wwwroot` 拷到 App 输出目录；`WebHostFactory` 显式指定 `WebRootPath`。
16. **类名 `Directory`/`System` 会遮蔽命名空间**：`SegmentRing` 里属性改名 `DirPath`；`AppConfig.System` 属性的存在导致类内要用 `global::System.X`。
17. **DPAPI 在 net9.0 的 Core 里不可用**：`System.Security.Cryptography.ProtectedData` 只在 `Microsoft.WindowsDesktop.App` 引用包里，所以 `SecretProtector` 用 P/Invoke 调 `crypt32.dll`。
18. **页面播放不要直接透传 WebDAV 流**：先把文件落到本地 LRU 缓存，再用 `Results.File(..., enableRangeProcessing: true)`，否则手机拖进度条必失败。
19. **全局 NuGet 走 `nuget.azure.cn` 镜像**：新加包要先确认能还原，并且固定版本。
20. **文件行尾**：编辑工具可能把 LF 变 CRLF，改动后用 `git diff` 配合二进制核对（`b'\r\n' in open(p,'rb').read()`）。

## 常用命令

```powershell
# 首次：下载 ffmpeg 到 third_party\ffmpeg\bin
powershell -ExecutionPolicy Bypass -File scripts\get-ffmpeg.ps1

# 编译
dotnet build OnvifWebDav.sln -p:MSBuildEnableWorkloadResolver=false

# 不需要摄像头，验证「分段 → 合成 → 缩略图」
dotnet run --project tools/OnvifWebDav.Probe -- ffmpeg-selftest

# 起一个假 WebDAV，再测客户端
dotnet run --project tools/OnvifWebDav.Probe -- mock-dav --port 8089
dotnet run --project tools/OnvifWebDav.Probe -- dav-test --url http://localhost:8089 --user dav --pass dav

# 验证推送（Basic Notification）——设备支持 WSSubscriptionPolicySupport 时走这条更快
dotnet run --project tools/OnvifWebDav.Probe -- notify-test --host 192.168.1.64 --user admin --pass xxx --seconds 120

# 离线验证 Notify 解析（拿到原始 XML 就能验，不用摄像头）
dotnet run --project tools/OnvifWebDav.Probe -- parse-notify --file notify.xml

# 连摄像头：先验事件（最关键），再验编码
dotnet run --project tools/OnvifWebDav.Probe -- events --host 192.168.1.64 --user admin --pass xxx --seconds 60
dotnet run --project tools/OnvifWebDav.Probe -- stream --host 192.168.1.64 --user admin --pass xxx
```

## 验收顺序（不要跳步）

1. `ffmpeg-selftest` 通过
2. `mock-dav` + `dav-test` 通过
3. `device` 连通、时钟偏差 < 30s
4. **`events` 60 秒内至少 2 次 `IsMotion=true`** ← 不达标就别往下做，先换事件源
5. `stream` 确认编码（HEVC 要先处理）
6. 运行 App，人在镜头前走动 → 30 秒后网页出现片段
7. 手机上播放 + 拖动进度
8. WebDAV 服务端确认目录结构、无 `.part` 残留
9. 拔网线 60s 再插回：确认重连 + 断网期间录像补传
10. 强杀进程后重启：确认 spool 恢复、无重复上传

## 当前状态（本次交付）

**已验证**
- `dotnet build OnvifWebDav.sln`：0 错误 0 警告
- `ffmpeg-selftest`：20 秒测试流 → 10 个 2 秒 TS 段 → 合成 20.0s 可 seek MP4 + 缩略图，全通
- 主程序启动：Kestrel 绑定 `0.0.0.0:8080`；`/`、`/api/status`、`/api/cameras`、`/api/dates`、`/app.css`、`/app.js` 全部 200；
  未设密码时 `/login` 302 回首页（符合预期）
- 启动时崩过一个真 bug：`NumericUpDown` 先赋 `Value` 再设 `Minimum/Maximum` 会抛 `ArgumentOutOfRangeException`，
  已在 `FormBuilder.Num` 里调换顺序（**那个顺序不能反**）

**未验证（需要你的环境）**
- ONVIF 实机握手 / 事件订阅 —— 需要摄像头
- WebDAV 真实上传 —— 本沙箱禁止 .NET 进程直连本地端口（返回 403，服务端收不到请求），
  `mock-dav` + `dav-test` 请在你自己的机器上跑一遍
- 手机端播放与拖动进度

## 打包部署

```
dotnet publish src/OnvifWebDav.App -c Release -o publish

:: 然后把 ffmpeg 拷进去（目录名必须是 ffmpeg）
mkdir publish\ffmpeg
copy third_party\ffmpeg\bin\ffmpeg.exe  publish\ffmpeg\
copy third_party\ffmpeg\bin\ffprobe.exe publish\ffmpeg\
```

- 当前 `third_party\ffmpeg\bin` 是 BtbN 静态构建，`ffmpeg.exe` / `ffprobe.exe` **各约 170MB**（合计 340MB）。
  嫌大可以换 gyan.dev 的 essentials 共享版（exe 小，但要多带若干 dll），程序只认这两个文件名，不影响。
- 配置、日志、spool、播放缓存都在 `%LocalAppData%\OnvifWebDav`，卸载直接删目录。
- 分发只需要 `publish` 目录，无需安装包。

## 补充坑（写代码时又踩到的）

21. **`NumericUpDown.Value` 必须在 `Minimum`/`Maximum` 之后赋值**，否则构造时直接抛异常。
22. **`-f segment` 是按关键帧切的**：GOP 太长（如 250 帧）根本切不出 2 秒的段，实测 20 秒只切出 2 段。
    真实摄像头 GOP 大时预录精度会变差 —— 建议把 I 帧间隔调到 ≤ 2×帧率。自检脚本造流已固定 `-g 30`。
23. **`WebDavConfig.UseSystemProxy`** 默认 false（局域网 NAS 不该走代理）。公司网络必须经代理访问外网 WebDAV 时要打开，
    否则连接会被代理静默挡掉。
24. **`web.Start()` 必须在 `Application.Run()` 之前**，并且要 try/catch：端口被占用时 WinForms 会停在 MessageBox，
    录像服务仍能照跑，只是没有网页。

## 事件获取：推送 vs 轮询（2026-11-09 补）

摄像头 TL-IPC682XD-A4 的 Events 服务报告 **WSSubscriptionPolicySupport=true**，
所以支持 WS-BaseNotification 的 `Subscribe`：我们给设备一个回调地址，设备主动 POST 事件过来。

| 方式 | 实现 | 适用 |
|---|---|---|
| Basic Notification（推送） | `wsnt:Subscribe` + 本机 `/onvif/notify` 收 | 设备支持 WSSubscriptionPolicy；更实时，无长轮询 |
| PullPoint（轮询） | `CreatePullPointSubscription` + `PullMessages` | 兜底，兼容性最好 |

`CameraConfig.EventMode`：`Auto`（默认，按能力选）/ `Basic` / `PullPoint`。

**推送链路的三个前提**（任一不满足会自动退回轮询，不会中断录像）：
1. 网页服务在跑（Kestrel 监听 `0.0.0.0:端口`），因为回调地址就是 `http://本机IP:端口/onvif/notify`
2. 摄像头能访问到本机 IP 和该端口（Windows 防火墙要放行）
3. 取得到局域网 IP（`LocalAddress.Preferred()`，排除 169.254 和回环）

`/onvif/notify` 在 `AuthMiddleware` 里是**免鉴权**的（设备不会带我们的 Cookie），只做一件事：把 body 丢进 `NotifyHub`。

链路：

```
设备 --wsnt:Notify(POST)--> /onvif/notify --> NotifyHub(Channel) --> BasicNotificationMotionSource --> CameraWorker
```

- 订阅有效期申请 120 秒，每 40 秒 `wsnt:Renew`；续订失败即视为失效，上层重建
- `GetServiceCapabilities` 的原始 XML 在 UI「事件自检」页会打出来，方便确认设备到底声明了什么
- 订阅瞬间同样会有一批 `Initialized` 事件，前 3 秒不触发录制

**又踩到的坑（第 25 条）**
25. **Notify 里有两个 `Message` 元素**：外层是 `wsnt:Message` 包装，内层 `tt:Message` 才带 `UtcTime` 属性。
    直接 `First(descendants, "Message")` 会拿到包装元素，`UtcTime` 丢失后事件时间被当成"当前时间"，
    表现为录到的片段起点偏差几分钟。必须找"带 UtcTime 属性的那个 Message"。
    这个坑对 PullPoint 的 `PullMessages` 响应同样成立（两处共用 `ParseNotification`）。

## 设备能力探测（2026-11-09 补 · 实测 TL-IPC682XD-A4 / 固件 1.0.4 Build 251113）

连接流程改成：`GetSystemDateAndTime`（匿名校时）→ **`GetServices`** → 用服务清单里的 XAddr 定位 media/events（拿不到才退回 `GetCapabilities`）→ `GetDeviceInformation` → `GetProfiles`。

为什么用 GetServices：它直接列出设备**实际实现**的服务命名空间，是判断 ONVIF Profile 最权威的一手资料；
`GetCapabilities` 有时会漏写 Events。事件能力（`WSSubscriptionPolicySupport` 等）就在同一个响应的
`tev:Capabilities` 属性里，一次往返全拿到，不用再单独发 `GetServiceCapabilities`。

### Profile 推断规则（按服务命名空间，非官方认证）

| Profile | 判定依据 | 这台设备 |
|---|---|---|
| S | 有 `ver10/media` + `ver10/device` | ✅ |
| T | 有 `ver20/media` + `ver10/device` | ✅ |
| **G** | **有 `ver10/replay`** | ❌ |
| M | 有 `ver20/analytics` | ✅ |

实测 GetServices 返回 8 个服务：`device` / `media(ver10)` / `events` / `analytics` / `imaging` / `ptz` /
`media(ver20)` / `deviceIO`，**没有 replay → 不支持 Profile G**。

**Profile G 是什么**：设备端录像 + 检索回放（`FindRecordings` / `GetReplayUri` / `GetRecordingSummary`），
前提是设备自己插卡并开启了录像。**不支持 G 意味着拿不到设备端的历史录像**，
所以「自己录、存 WebDAV」不是可选项而是必须项 —— 当前方案正好是这个，不受影响。

### 这台设备能力的其他结论

- `SnapshotUri="false"`（ver10 和 ver20 都是）→ **ONVIF 层面没有快照**，UI 已自动禁用「抓一帧快照」按钮，
  `GetSnapshotJpegAsync` 也会提前抛中文异常而不是白跑一次请求
- ⚠ **连带影响：原计划的"快照差分"兜底方案不可行**。如果哪天真收不到运动事件，
  兜底要改成 **RTSP 帧差**：ffmpeg 拉子码流 `-vf fps=2,scale=320:-2 -f rawvideo` 管道到 C# 做分块灰度比对。
  `IMotionSource` 接口已经留好，换个实现即可
- `RTSPStreaming="true"`（ver20）与 `NoRTSPStreaming="true"`（ver10）**互相矛盾** —— 海思固件常把这个字段填反，
  所以解析时 ver20 优先，最终以 ffprobe 实测为准
- `WSSubscriptionPolicySupport="true"` → 推送可用；`WSPausableSubscriptionManagerInterfaceSupport="false"` → 不支持暂停（我们不用）
- `HttpDigest="false"` / `UsernameToken="true"` → 只认 WS-Security，不要做 HTTP Digest 回退
- 8 个服务共用 `http://ip:2020/onvif/service`，**和 device_service（`/onvif/device_service`）不是同一个 path**，
  所以 `Rewrite` 必须保留设备声明的 path，只换 host/port
- 设备 80 和 2020 都监听（工具走 80、XAddr 写 2020），配置填哪个都行

### 新增命令

```
# 在线：一次拿全设备能力
dotnet run --project tools/OnvifWebDav.Probe -- caps --host 192.168.1.64 --port 2020 --user admin --pass xxx

# 离线：把抓到的 GetServices 响应存成文件直接解析（不用连设备）
dotnet run --project tools/OnvifWebDav.Probe -- parse-caps --file getservices.xml
```

### 补充坑（26、27）

26. **`NoRTSPStreaming` 与 `RTSPStreaming` 可能同时为 true**（海思系固件填反）。解析时给 ver20 的 `RTSPStreaming`
    更高优先级，且只用于界面提示 —— 不要拿它当「能不能录像」的判断依据，以 ffprobe 实测为准。
27. **`GetServices` 要带 `IncludeCapability=true` 才返回服务能力**，否则只有 XAddr，拿不到 `WSSubscriptionPolicySupport`
    这些开关。老固件会忽略这个参数，此时要走 `GetServiceCapabilities` 兜底。
28. **`SubscriptionReference/Address` 的端口不能一概用配置端口重写**。有些机型 device_service 在 80、订阅管理器在 2020，
    硬改端口会连不上。现在的规则：只有「设备返回的 IP 正好等于我们连的 IP」才保留它声明的 host+port，
    localhost / 主机名 / 别的网卡 IP 才回落到配置地址。

## WebDAV 后端适配：messense/aliyundrive-webdav（阿里云盘）

用户实际用的是这个（Rust 版，把阿里云盘挂成 WebDAV）。它和标准 WebDAV 服务端有几处关键差异，
其中两条会**直接让流程跑不通**，已经适配：

### ① GET 返回 302（默认行为，最致命）

`--redirect` **默认开启**：客户端 GET 文件时，服务端返回 `302` 到阿里云盘 CDN 的**带签名直链**，
根本不转发内容。我们原来是 `AllowAutoRedirect = false`，结果就是"上传成功、网页播放黑屏"。

现在的做法（`WebDavClient.GetContentAsync`）：
- 正常请求用**不跟随重定向**的 client（保证 PUT/MOVE 的行为可控）
- GET 收到 3xx 时，取 `Location`，换用**独立的、不带 Authorization 头**的 `_redirectHttp` 去下载
- `ETag` 只从原始 200 响应取（CDN 不会回我们的 ETag）
- `GetStringWithEtagAsync` / `DownloadToFileAsync` 都走这个入口

> 带认证头去请求 CDN 直链会失败（签名 URL 不认这个头），所以必须是两个 client。

**备选方案**：服务端加 `--no-redirect` 让它中转下载。好处是行为完全标准（ETag 正常、Range 正常），
坏处是所有流量过 WebDAV 服务器。如果服务器带宽够、且网络到阿里云盘不稳，这个更省心。
代码两种情况都支持，不用改。

### ② 每个 MKCOL 都是一次真实 API 调用

阿里云盘的"目录"要调 `create_dir` API，有 QPS 限制。我们一条录像的路径是
`{root}/{cam}/clips/2026/11/09/` + `{root}/{cam}/thumbs/2026/11/09/`，**7~8 层**。
一天几百段录像 × 每次全量 MKCOL = 上万次多余请求，必然被限流。

已加进程内的目录创建缓存（`_ensuredDirs`），建过的直接跳过；遇到 409 不写缓存（下次重试）。

### ③ MOVE 支持不稳 → 已能"记住"结论

它有 `rename()` 实现，但上传功能作者自述"**测试不全面、没有持续迭代计划**"，MOVE 可能失败。
失败时我们降级成直接 PUT 正式文件名 —— 但那样等于**把几十 MB 的视频传两遍**（先 `.part` 再正式名）。

现在「测试 WebDAV」会把 MOVE 能力写进 `config.json` 的 `webDav.supportsMove`，
上传队列启动时直接按上次结论决定走不走 `.part`，不会重复浪费流量。

### ④ 其他注意

- **认证**：`--auth-user / --auth-password`，HTTP Basic。它不支持 Digest（我们的 Basic 优先策略正好）
- **`--skip-upload-same-size` 不要开**：开了之后同尺寸文件会被跳过不传。我们的文件名是 `HHmmss_随机6位`，理论不冲突，但没必要冒这个险
- **文件名**：我们的落盘名是纯 ASCII（`153012_ab12cd.mp4`），符合阿里云盘的字符限制。但 `RootPath` 建议也用 ASCII（`/onvif`），别用中文目录
- **上传并发**：`UploadQueue.Parallelism = 2`，对阿里云盘的 API 限流是安全的，别再往上加
- **302 模式下需要本机能访问外网**：直链指向 `aliyundrive.com` 的 CDN，如果这台机器只有内网，下载会失败 ——
  这种情况改用服务端 `--no-redirect` 中转
- **上传大文件会分片**（服务端 `upload_buffer_size` 默认 16MB），中断可能留残留分片，但对我们几 MB~几十 MB 的片段影响很小

### 验证方法

```bash
# 1. 起服务（示例）
aliyundrive-webdav --refresh-token <token> --auth-user onvif --auth-password <pwd> --port 8080

# 2. 先跑客户端自检：现在会做「PUT → HEAD → GET 回读 → MOVE → DELETE」完整往返
dotnet run --project tools/OnvifWebDav.Probe -- dav-test --url http://192.168.x.x:8080 --user onvif --pass <pwd> --root /onvif
```

`dav-test` 现在会做**回读校验**（`PUT` 后 `GET` 比对内容）——这一步就是为了抓 302 问题。
如果这里过了，网页播放的下载链路基本就没问题。

本地模拟 302 的假服务端：

```bash
dotnet run --project tools/OnvifWebDav.Probe -- mock-dav --port 8089 --redirect
```
（`--redirect` 会让 GET 返回 302 到 `/cdn/...`，等价于阿里云盘的默认行为）

### 补充坑（29~31）

29. **`Alibaba Cloud Drive` 的 302 直链不能用带认证的 HttpClient 去下**，必须换无认证的 client，
    否则签名 URL 会拒绝（或者把我们的 Basic 凭据泄露到第三方域名）。
30. **`GetStringWithEtagAsync` 在 302 模式下拿不到 ETag** → 索引的 `If-Match` 乐观锁退化为无条件覆盖。
    单实例写入时无影响；如果以后多实例共享同一个 WebDAV 根目录，要么改用 `--no-redirect`，要么换一套并发控制。
31. **`WebDavConfig.SupportsMove` 是可空布尔**：`null` = 没测过（首次会尝试 `.part` + MOVE），
    `true/false` = 已确认。只有点过「测试 WebDAV」才会写这个值，不要手动设成 false 来"优化"，
    否则服务端明明支持 MOVE 也会被降级成非原子上传。

## 修复：`上传成功但读不到文件长度`（2026-11-09 实机反馈）

用户点「测试 WebDAV」报这个错。根因不是上传失败，而是**测试逻辑太严**：

`aliyundrive-webdav` 的 HEAD 要么 405、要么 200 但不给 `Content-Length`、要么对 HEAD 也发 302。
原来的 `HeadLengthAsync` 只认 HEAD 的 `Content-Length`，拿不到就返回 null，
而 `TestAsync` 把这个 null 当成致命错误**直接 return**，后面的 GET 回读 / MOVE / DELETE 全没跑。

三个改动：

1. **`HeadLengthAsync` 增加 PROPFIND 回退**：HEAD 拿不到就查 `getcontentlength`；还拿不到就返回 null（调用方不判错）
2. **`TestAsync` 重写成分步诊断**：7 个步骤逐条记录（PROPFIND → MKCOL → PUT → 取长度 → GET 回读 → MOVE → DELETE），
   拿不到长度只记一条 ✘ 步骤但**继续往下走**，只有 PUT 失败和 GET 回读不一致才判失败
3. **上传队列的 `.part` 长度校验**：拿不到长度时从 WARN 降为 Debug（原来会误导成"长度不符"）

重跑后看到的应该是完整步骤表，哪一步红了一眼就知道。

## 顺带修掉的真 bug：根目录从来没被创建

`EnsureCollectionsAsync` 只从 `relativeDir` 开始逐层 MKCOL，**从来没有建过配置里的 `RootPath` 本身**。
Nextcloud 上 root 一般已经存在，所以一直没暴露；换成阿里云盘（云盘上没有 `/onvif` 这个目录）就会
MKCOL `/onvif/cam1` 返回 409 → 我们 `continue` → PUT 也 409 → 上传全挂。

现在 `EnsureCollectionsAsync` 第一步先 `EnsureRootAsync`：按 `/onvif` 的每一层发 MKCOL
（用 `includesRoot: true` 让 `BuildUri` 不再重复拼 root），成功才写缓存。

## 更正一个之前的错误结论

之前说"沙箱禁止 .NET 进程直连本地端口，返回 403"，**这个判断是错的**。

真实原因是：在 Git Bash 里传 `--root /onvif`，MSYS 把 `/onvif` 当 POSIX 路径转换成了 `C:/Program Files/Git/onvif`，
于是请求路径变成 `/C:/Program Files/...`，mock 服务器的路径穿越检查直接 403（该检查在
打日志之前，所以服务端日志里看不到请求，才误判成"请求没到达"）。

**教训**：在 Git Bash 里给参数传 POSIX 风格的绝对路径（`/xxx`）会被自动转换。要么设
`MSYS_NO_PATHCONV=1`，要么不传（默认值就是 `/onvif`），要么用 cmd 窗口跑。

`dav-test` 其实一直可以本地跑。现在用 mock 能完整验证：

```bash
# 模拟阿里云盘：GET 302 直链 + HEAD 405
dotnet run --project tools/OnvifWebDav.Probe -- mock-dav --port 8093 --redirect --no-head
dotnet run --project tools/OnvifWebDav.Probe -- dav-test --url http://127.0.0.1:8093 --user dav --pass dav
```

实测输出（全绿，`取文件长度` 走的是 PROPFIND 回退）：

```
✔ 连接正常（Basic，往返 210 ms）：上传✔ 下载✔ MOVE ✔
  ✔ PROPFIND 根目录：返回空列表
  ✔ MKCOL 根目录
  ✔ MKCOL .selftest
  ✔ PUT 上传：15 字节
  ✔ 取文件长度：15 字节
  ✔ GET 回读：内容一致      ← 302 跟随生效
  ✔ MOVE：支持
  ✔ DELETE 清理
```

服务端日志能同时看到 `[MKCOL] /onvif`（根目录自动创建）和 `[302] ... → /cdn/...`（重定向跟随）。

### 补充坑（32）

32. **`HEAD` 在网盘类 WebDAV 上不可靠**：不要用它判断文件是否存在/完整。要么用 PROPFIND，
    要么直接 GET 一段校验。

## 云端空间守卫（上传前查配额，2026-11-09 补）

背景：用户的阿里云盘只有 **105 GB**，而录像只增不减，早晚撑爆。撑爆的表现不是"报错"，而是
上传开始失败 → 本地积压 → 磁盘满 → 停止录制，排查起来很绕。

### 配置

```jsonc
"quota": {
  "enabled": true,
  "warnBelowGb": 10,            // 剩余低于此值：状态提示 + 30 分钟一条 WARN
  "pauseBelowGb": 3,            // 剩余低于此值：停止上传（0 = 不暂停）
  "autoDeleteOldest": false,    // 暂停前先自动删最旧的（不可逆，默认关）
  "autoDeleteTargetFreeGb": 15, // 自动清理的目标剩余空间
  "probeMinutes": 30            // 多久真查一次配额，中间用缓存
}
```

UI 在 **WebDAV 页 → 云端空间守卫** 分组里。

### 行为

每次上传前走 `UploadQueue.EnsureSpaceAsync`：

1. 配额超过 `probeMinutes` 才真查一次（缓存值复用，避免每个片段都发 PROPFIND）
2. 剩余 ≥ 告警线 → 正常上传
3. 剩余 < 告警线 → 只提示，继续传
4. 剩余 < 暂停线：
   - 开了自动清理 → 从最旧的一天开始整天删，删到剩余 ≥ `autoDeleteTargetFreeGb`
   - 清理后仍不够 / 没开自动清理 → **停传**，job 回到 `Pending`（不消耗重试次数），5 分钟后再看
5. 服务端不返回配额 → 一律放行（不能因为查不到就把功能卡死）

### 实现要点

- 配额用 RFC 4331：`PROPFIND Depth:0` 发到 **WebDAV 根**（不是我们的 `RootPath`），解析
  `DAV:quota-available-bytes` / `DAV:quota-used-bytes`
- 删除顺序：**录像文件 → 缩略图 → 索引文件**。索引最后删（它是"文件完整"的权威标记），
  中途失败时索引仍指向真实存在的文件，不会出现"索引说有、文件却没"的脏数据
- 按**整天**删而不是按片段删，保证索引和文件的一致性；日期来自 `PROPFIND {cam}/index/`

### 关键防护：清理前必须先判断

最初 `RetentionPruner` 是"删一天 → 查配额 → 够了就停"，意味着**即使空间充足也会先删掉一天**。
删除不可逆，这个误删代价太大。现在循环前先查一次：够了直接返回，一天都不删。

### 验证（mock 实测）

```bash
dotnet run --project tools/OnvifWebDav.Probe -- mock-dav --port 8095 --quota-gb 1

# 场景 A：空间充足（目标 0.001GB）→ 应当 0 删除
dotnet run --project tools/OnvifWebDav.Probe -- retention-test --url http://127.0.0.1:8095 --user dav --pass dav --seed 3 --target-free-gb 0.001

# 场景 B：目标超过配额（1.5GB > 1GB）→ 应当删光，ReachedTarget=False
dotnet run --project tools/OnvifWebDav.Probe -- retention-test --url http://127.0.0.1:8095 --user dav --pass dav --seed 3 --target-free-gb 1.5
```

实测结果：

| 场景 | 删除 | ReachedTarget | 说明 |
|---|---|---|---|
| A（0.001GB） | 0 天 / 0 段 | True | 空间够就不动，防误删 ✓ |
| B（1.5GB） | 3 天 / 3 段 | False | 删完仍不够，上层会走"暂停上传"分支 ✓ |

`dav-test` 也会顺带查一次配额，输出形如
`✔ 查配额：总 105 GB / 已用 0.0 GB / 剩余 105.0 GB`。

### 阿里云盘实操建议

- **`MaxTotalGb` 设 80 左右**，留 25G 给上传缓冲、索引、以及你自己放在云盘的其它文件
- **服务端加 `--no-trash`**：不加以，删除是"移到回收站"，容量**不会立即释放**，
  于是会出现"程序删了 50G、云盘还是满的"。不加就必须定期去 App 里清空回收站
- 想省空间优先调**码率/换子码流**，别调"单段最长时长"——后者只改变切片粒度，不减少总字节

### 补充坑（33、34）

33. **网盘的 DELETE 默认进回收站**，配额不释放。要么服务端 `--no-trash`，要么把清空回收站纳入运维流程。
34. **配额是账号级的**，必须查 WebDAV 根（`/`），不能查 `RootPath`。查 `RootPath` 只会返回该目录的信息，
    有些实现干脆返回 404。

## 修复：第二段起录像残片 / 无法播放（2026-11-09 实机反馈）

现象：第一段录像正常，从第二段开始点开不是黑屏就是一开就结束。

### 根因：同一时刻有"上一段在合成"和"下一段在录"，而状态放在共享字段里

**① `_jobStartLocal` 是 worker 字段，不是 job 自己的**

`EvaluateSessionAsync` 在锁内算出 `endLocal`，但 `FinalizeAsync` 里 `Collect` 读的是字段
（`_ring.Collect(_jobStartLocal, endLocal)`）。合成一个片段要 1~30 秒（转码更久），这期间`OnMotionStarted`
已经把 `_jobStartLocal` 覆盖成**下一段**的起点，于是上一段用错误窗口切段：头部被削，甚至切出空列表整段丢掉。

**② `ReleaseAll()` 会清掉"下一段刚设上的保护"**

`SegmentRing` 原来是单个 `_protectFrom`，`FinalizeAsync` 的 finally 无条件 `ReleaseAll()`。
上一段收尾时下一段已经在录，它的保护被一笔勾销，接着 `Trim(PreRoll+15)` 把下一段的早期分段删掉 ——
合出来就是没有开头的残片。

**③ 触发式录制共用 `_recorder` 字段**

上一段的 ffmpeg 在 `'q'` 之后要几秒才退出，这段窗口里下一段已经启动。上一段的收尾调 `StopRecorderAsync()`
停的是**字段里的当前进程**，也就是下一段的进程 → 新 MP4 没写 moov → 直接放不了。
另外 `EnsureTriggerRecording` 里 `if (_recorder is { IsAlive: true }) return;` 会让下一段**根本没有录制进程**。

### 改法

| 位置 | 改动 |
|---|---|
| `EvaluateSessionAsync` | 锁内把 `startLocal` 捕获成局部变量 |
| `FinalizeAsync` | 签名改为 `(job, startLocal, endLocal, forceSplit, ct)`，全程用参数，不再读字段 |
| `SegmentRing` | 保护改**令牌制**：`ProtectFrom` 返回 token，`Release(token)` 只放自己那个，`Trim` 取所有活动 token 里最早的 |
| `JobRecord` | 新增 `[JsonIgnore] RingToken`（保护令牌）和 `GapCount`（缺口数） |
| `CameraWorker` | 触发式录制按 `jobId` 存进程（`_triggerRecorders`），收尾只停自己的；`StopRecorderAsync()` 兜底清空全部 |
| `FinalizeAsync` | 新增残片判定与连续性告警（见下） |

### 新增的两道防线

```
窗口 <=1 段 && 窗口 >=15s  →  判定为残片，放弃这个片段（宁可不生成，也不生成"点开一闪就没"的假录像）
相邻段起点间隔 > max(4×段长, 12s)  →  WARN，并把缺口数写进 job/索引的 gapCount
```

`gapCount > 0` 说明这段录像中间会跳帧 —— 以前只会表现为"这段录像怪怪的"，现在日志和索引里都能查到。

### 回归测试（不需要摄像头、不需要 ffmpeg）

```bash
dotnet run --project tools/OnvifWebDav.Probe -- ring-test
```

造 60 秒假 TS 段，模拟"A 正在合成、B 已经开始录"，断言：

```
✔ 两个片段同时持有保护：2 个
✔ 释放 A 后 B 的保护仍在：1 个        ← 旧实现这里就断了
✔ B 的分段没被 Trim 删掉：2 段
✔ B 保留到了自己的起点
✔ 全部释放后保护归零：0 个
✔ 保护释放后旧段被回收：剩 10 段       ← 反向对照：没保护时 Trim 确实会删
```

**改 `SegmentRing` 之后必跑**，退出码非 0 即失败。

### 顺带明确：一段录像到底有多长

预录 8s、静默 5s、上限 300s 时的语义（`CameraWorker.EvaluateSessionAsync`）：

| 两个事件的间隔 | 结果 |
|---|---|
| < `SilentStopSeconds`(5s) | **合并成一段**（第二次事件只把 `_lastMotionLocal` 往后推） |
| 5~8s | **两段**，但第二段起点 = 事件时间 − 8s，会**回看进第一段的窗口**，重复几秒内容（日志有 "重叠 Ns" 提示） |
| > 8s | 两段不重叠 |

- 时长 = 最后一次运动 + `SilentStopSeconds` + 3s，再被 `MaxSeconds` 强制切片
- `endLocal > now` 时会夹到 `now`，所以片段不会"录到未来"
- 所以单段**可以**远超 8s（持续有人走动时会一直延长到 300s），这是设计行为；8s 只是预录回看量

### 补充坑（35~37）

35. **片段状态绝不能用 worker 级字段**。合成是异步的，任何"当前片段"的状态（起点、进程、保护令牌）
    都必须跟着 `JobRecord` 走。字段会被下一段覆盖，而且覆盖时机正好卡在"等最后一段写完"的那 2~4 秒里。
36. **`SegmentRing` 的保护必须支持多值**。收尾与新片段交替时会有两个活动保护，
    单个 `DateTime` 字段无论怎么写都会错一半。
37. **触发式录制的 ffmpeg 进程要按 jobId 存**。`'q'` 到进程真正退出之间有几秒，那几秒里两个片段的进程同时存在，
    用单个字段会导致"上一段停掉了下一段的进程"，产出一个没有 moov 的 MP4（浏览器报解码失败，ffprobe 也读不出时长）。

## 「一直录 + 按事件切」（2026-11-09）

用户问「能不能一直保存视频流，按事件时间戳截取」。答案是**可以，但只能存在本地**：

| 码率 | 占用 | 阿里云盘 105G 能存 |
|---|---|---|
| 主码流 3 Mbps | 22.5 MB/分 · 1.35 GB/时 · **32 GB/天** | **3.2 天** |
| 子码流 1 Mbps | 7.5 MB/分 · 450 MB/时 | 9.7 天 |

所以方案是 **本地环形缓冲保一段窗口 → 事件来了按时间窗切片段 → 只上传片段**。
这本来就是 RingBuffer 模式，改动只是把"窗口"从 `PreRoll + 15` 秒放开到独立可配。

### 新增/变更的配置

```jsonc
"recording": {
  "ringKeepSeconds": 600,      // 环形保留窗口（秒）。10 分钟 ≈ 200~700MB @3Mbps
  "ringLimitMb": 2048,         // 环形目录磁盘硬上限；0 = 只按时间
  "preRollSeconds": 8,         // 可放大到 30~60 秒，抗事件延迟
  "postRollSeconds": 3,        // 片尾余量，实际尾部 = silentStop + postRoll
  "topicFilter": ""            // 事件 topic 白名单（逗号分隔子串）；空 = 全部
}
```

`RecordingConfig.EffectiveRingKeepSeconds` 会自动保证窗口 ≥ `预录 + 静默 + 片尾 + 4 段`，
用户把环形调小也不会切出残片。

### 时长语义变化（重要）

以前：`endLocal = lastMotion + SilentStop + 3`，且 `> now` 时夹到 `now` —— 实际尾部就是"静默几秒"，
那 3 秒余量几乎永远被夹掉。现在：

```
tail      = SilentStop + PostRoll
结束判定  = 无事件满 tail 秒
endLocal  = lastMotion + tail（此时恰好 <= now，不再被夹）
```

所以片段尾部真正包含 `SilentStop + PostRoll` 秒，"人走出画面"和事件投递延迟都兜住了。

### 三个必须一起修的点

**① `CleanAll()` 不能每个会话都做**

`StartRingRecorder()` 原来每次都 `_ring.CleanAll()`。窗口只有 23 秒时看不出来；放开到 10 分钟后，
摄像头一次 RTSP 抖动就会走重连分支，把整段缓冲清空 → 事件来了切不到东西。
现在只在本次进程第一次启动时清（`_ringCleaned`），崩溃残留交给 `AppRuntime` 构造时调 `Spool.CleanRing()`。

**② 环形目录不计入本地配额**

`LocalSpool.TotalJobBytes()` 只数 `spool/jobs`，`spool/ring` 完全不算，`SpoolLimitGb` 管不到它。
窗口放开后如果 `Trim` 判错就会**静默吃满盘**。所以新增 `ringLimitMb`，
`SegmentRing.Trim(keepSeconds, maxBytes)` 在按时间淘汰后再按字节从最旧删（只删没保护的）。
受保护的段加起来就超上限时删不动，`TrimRing()` 会每 5 分钟告警一次。

**③ 事件类型要能过滤、也要落盘**

`IMotionSource.MotionStarted/MotionEnded` 的签名从 `Action<DateTime>` 改成 `Action<OnvifMotionEvent>`，
事件带 topic 才能按类型过滤。命中 `topicFilter` 才建/延长片段；不命中的只更新状态计数，不碰 `_lastMotionLocal`。
`JobRecord.Topics` → `ClipInfo.Topics` 写进每日索引，网页列表显示 `CellMotionDetector/Motion` 这样的短名。

> 时间精度提醒：切片精度 = **段长（默认 2 秒）**，不是事件时间戳的毫秒。`-c copy` 只能从关键帧切。
> 想更细只能重编码，不划算 —— 把预录放大到 30~60 秒更实际。

### 前端/接口

- `/api/status` 新增 `ringMb`（环形缓冲占用）
- `/api/clips` 新增 `gapCount`、`topics`
- 网页列表在时长后面追加事件类型短名；`gapCount > 0` 时追加「跳帧」

### 回归测试（新增场景 B）

```bash
dotnet run --project tools/OnvifWebDav.Probe -- ring-test
```

```
场景 B：环形保留放大到 10 分钟，再加磁盘上限兜底
  ✔ 初始占用统计正确：1200KB
  ✔ 窗口内不误删：剩 298 段
  · 收紧后 200KB，受保护窗口 14 段
  ✔ 按字节淘汰生效：200KB
  ✔ 受保护的分段一个都没删：14 段
  ✔ 释放后能继续压到上限以下：100KB
```

### 补充坑（38~40）

38. **环形窗口放开后，`CleanAll` 的调用时机比窗口大小更重要**。放在"每个会话"会随每次 RTSP 重连清空整段缓冲，
    表现为"明明设了 10 分钟，事件却切不到预录"。只能在进程首启清一次。
39. **`spool/ring` 不在 `TotalJobBytes()` 里**，`SpoolLimitGb` 对它无效。必须单独有字节上限，
    否则磁盘只剩几百 MB 时才会以"写入失败/ffmpeg 退出"的形式暴露出来。
40. **片段尾部余量不是加在 `endLocal` 上就行**：`endLocal > now` 会被夹到 `now`，
    所以要同时把"静默判定阈值"一起加上 `PostRollSeconds`，否则余量永远被夹掉。

## 环形缓冲写到内存盘（2026-11-10）

用户搞了 1GB RAM disk，要求「连续录像不写物理盘」。实现的是**把环形目录指到内存盘**，
不动录制管线 —— 因为 ffmpeg `-c copy` 分段本来就只是写文件，换目录就等于换介质。

### 配置

```jsonc
"recording": {
  "ringDirectory": "R:////onvif-ring",  // 空 = 数据目录\spool\ring
  "ringKeepSeconds": 600,
  "ringLimitMb": 2048                  // 实际还会被卷剩余空间压一层
}
```

界面在 **录制页 → 环形目录**（带「浏览…」和实时剩余空间显示）。

### 数据落盘的真实分布

| 内容 | 位置 | 说明 |
|---|---|---|
| 连续录像（环形缓冲） | `ringDirectory`（内存盘） | 全程在内存里滚，只保留时间窗 |
| 事件片段 MP4 | `spool\jobs`（物理盘） | **必须持久**：这是上传重试队列，WebDAV 挂了靠它 |
| 播放缓存 | `spool\cache` | 物理盘 |
| 配置/日志 | `%LocalAppData%\OnvifWebDav` | 物理盘 |

所以不是"0 写入"，而是"连续写入 0，只有片段那点写入"。绝对 0 写入会把重试能力做没。

### 三层容量保护

1. `EffectiveRingKeepSeconds`：窗口 ≥ 预录 + 静默 + 片尾 + 4 段
2. `ringLimitMb`：配置的字节上限
3. **卷剩余空间**：`生效上限 = min(配置上限, (剩余 + 当前占用) × 0.9)`

第 3 条是关键：如果只看"剩余"，环形越写越多、剩余越来越少，上限跟着缩小 → 刚删完又超限，
形成抖动。用「剩余 + 当前占用」才是一个稳定的预算值。

### 目录不可用时回落

`LocalSpool.SetRingRoot()` 先 `Directory.CreateDirectory()`，失败就回落到默认目录并记 ERROR，
**不会因为内存盘没挂载就停止录像**。注意 .NET 对不存在的盘符抛的是 `Access is denied`
（不是 DirectoryNotFoundException），所以回落原因要自己判断盘符是否存在再翻译成中文。

### 新增命令：ring-info

改配置前先验一遍，不用连摄像头也不用 ffmpeg：

```bash
dotnet run --project tools/OnvifWebDav.Probe -- ring-info --dir "R://onvif-ring" --limit-mb 900 --keep 600 --mbps 4
```

```
环形目录解析：
  配置值      : R://onvif-ring
  实际生效    : R://onvif-ring
  自定义目录  : 是
  卷容量      : 剩余 1.00 GB / 共 1.0 GB
容量估算：
  配置上限    : 2048 MB
  实际生效    : 921 MB
  码率 4Mbps 时，保留 600 秒需要 300 MB
  生效上限能存: 1842 秒（30.7 分钟）
✅ 环形目录可用
```

指定的目录被回落、或者容量不足（留 15% 余量）时返回码 1。

### 1GB 内存盘能存多久

| 码率 | 1GB ≈ | 建议 `ringKeepSeconds` |
|---|---|---|
| 4 Mbps | 30 分钟 | ≤ 1200（20 分钟） |
| 2 Mbps | 68 分钟 | ≤ 2400 |

`ringLimitMb` 填 900 左右比 2048 更准（默认会被卷空间自动压到 921，但显式填更直观）。

### 补充坑（41~43）

41. **盘符不存在时 .NET 抛的是 `Access is denied`**，不是"路径不存在"。回落提示要自己查
    `Path.GetPathRoot()` 是否存在再翻译，否则用户看到的是莫名的权限错误。
42. **环形字节上限必须用「剩余 + 当前占用」算预算**。只用剩余会形成"删了又超、超了又删"的抖动，
    而且随着环形增长上限一直在缩，最后永远追不上。
43. **内存盘数据重启即失**。`spool\jobs`（片段+重试队列）绝不能在内存盘上，否则重启丢的不只是缓冲，
    还有没传完的录像。

## 修复：合成失败 `Audio: aac ... 0 channels`（2026-11-10 实机报错）

线上日志：

```
[mpegts] start time for stream 1 is not set in estimate_timings_from_pts
[mpegts] stream 1 : no TS found at start of file, duration not set
[mpegts] Could not find codec parameters for stream 1 (Audio: aac, 0 channels): unspecified sample format
[concat] Could not find codec parameters for stream 1 (Audio: aac, 0 channels, fltp)
[graph_-1_in_0:1] Neither number of channels nor channel layout specified
Error initializing filters!
[out#0/mp4] Nothing was written into output file
```

### 根因

环形缓冲的**第一个分段里 PMT 声明了音频 PID，但一个音频包都没有**（音频 PTS 比视频晚，或分段
正好切在音频间隙）。于是：

1. ffprobe 只能报 `aac, 0 channels`，拿不到声道/采样率
2. 我们的合成命令固定 `-c:a aac -ar 16000 -ac 1`，重采样需要输入声道 → `Neither number of channels...`
3. 滤波器初始化失败 → 整个输出 0 字节 → `ComposeAsync` 返回失败 → `_spool.DeleteJob(job)`

**这类片段以前是直接丢掉的**，只有一行 ERROR 日志，所以表现是"某些时段莫名其妙没有录像"。

### 三层修复

| 层 | 做法 | 效果 |
|---|---|---|
| 1 | `ProbeInfo` 增加 `AudioChannels` / `AudioSampleRate`，参数不全就 `-an` | 从源头避免 0 声道进重采样 |
| 2 | 源音频是 AAC 时 `-c:a copy`（MP4 原生支持） | 少一次重编码，也不再依赖声道信息；顺带修掉了"强制 16kHz 单声道"的音质损失 |
| 3 | 带音频合成失败 → 自动 `-an` 重试一次 | **宁可丢声音也不丢录像**，这一层兜住所有音频畸形 |

另外给 concat 输入加了 `-analyzeduration 50M -probesize 50M`：ffmpeg 会往后多探几个文件，
很多情况下能从后面的分段里把音频参数捞出来，直接保住音轨。

### 回归测试（能真实复现线上报错）

`ffmpeg-selftest` 新增两个附加场景：

```
附加 A：源音频是 AAC 时应 -c:a copy（不重编码）…
   ✔ 490 KB 音频=aac 1声道 16000Hz
附加 B：首段只有视频、PMT 却声明了音频（线上那个 0 channels 报错）…
   ✔ 合成成功 287 KB 4.0s 音频=False
```

附加 B 的造法：`-itsoffset 5` 让音频晚 5 秒，按 2 秒切段，取前两个段 —— PMT 有音频 PID 但没有音频包。
用旧参数跑这批段能**一字不差**复现线上那段报错，改完后合成成功。

日志里现在会明确写出来：

```
[WARN] ffmpeg: 首段音频参数不全（aac，0 声道 / 0Hz），按无音频合成
[INFO] ffmpeg: 开始合成 2 个分段 → clip-gap.mp4（转码=False，音频=无）
```

### 补充坑（44~46）

44. **TS 的 PMT 声明 ≠ 该段真的有音频包**。`-f segment` 切成的小段经常"有音频 PID、零音频包"，
    此时 ffprobe 报 `aac, 0 channels`，任何需要声道参数的操作（重采样、滤镜）都会失败。
45. **探不出的参数不要硬猜**。`-ac 1 -ar 16000` 只解决输出端，输入端未知时 ffmpeg 直接报
    `Neither number of channels nor channel layout specified`，而且失败是"整段 0 字节"，不是"降级"。
46. **AAC 进 MP4 不需要重编码**。`-c:a copy` 同时解决"没有声道信息"和"16kHz 单声道音质损失"两个问题，
    只有 G.711/PCM 这类 MP4 装不下的才必须转码。

## 修复：上传任务被重复认领 → `FileNotFoundException: clip.mp4`（2026-11-10 线上）

线上日志：

```
[ERROR] upload: 上传任务 20261110_100352_a123fc 异常 :: FileNotFoundException:
        Could not find file '...\spool\jobs\20261110_100352_a123fc\clip.mp4'
[WARN] upload: 任务 ... 第 1 次失败 ...；5s 后重试
```

### 根因：`LoadJobs()` 把"正在上传"当成"上次崩溃残留"

旧代码在 `LocalSpool.LoadJobs()` 里无条件做了一件事：

```csharp
// 上次崩在"上传中"，重置为待上传
if (job.State == JobState.Uploading) job.State = JobState.Pending;
```

但 `LoadJobs()` 在运行期间也被调用（认领任务、`CountPending()`、每次 `Enqueue`）。
于是并发上传时：

1. worker A 认领任务 X：磁盘上写成 `Uploading`
2. worker B 调 `LoadJobs()` → 看到 X 是 `Uploading` → **在内存里改回 `Pending`** → 把 X 也认领了
3. 两个 worker 同时上传 X，往同一个远端路径 PUT
4. A 先完成 → `DeleteJob(X)` 删掉本地目录
5. B 此时去读 `clip.mp4` → `FileNotFoundException`，然后重试 12 次，每次都失败

`Parallelism = 2`，所以只要"上传过程中又来了新片段"，这个 race 几乎必现。

### 四处修复

| 位置 | 改动 |
|---|---|
| `LocalSpool.LoadJobs` | **删掉** Uploading→Pending 的重置，只返回磁盘上的真实状态 |
| `LocalSpool.RecoverInterruptedUploads` | 新增，崩溃恢复收口到这里，**只在 `UploadQueue.Start()` 调一次** |
| `UploadQueue` | 新增进程内 `_inFlight` 集合：认领时加入、`finally` 移除；认领条件加 `!_inFlight.Contains(id)` |
| `UploadQueue.ProcessAsync` / `ScheduleRetry` | 上传前检查 `File.Exists(job.ClipFile)`，缺失就丢弃并记 ERROR，不再空转 12 次重试 |

`_inFlight` 是第二道保险：即使哪天有人又在别处改了状态，同一进程内也不会重复认领。

### 顺带修的一个效率问题

原来 worker 处理完一个任务就回去 `WaitAsync(30s)`，只有 `Enqueue` 才发信号。
重启恢复 10 个任务时会变成"一个 30 秒"。现在：**上传成功（目录已删）且还有排队任务时补一个信号**；
失败/重试的任务目录还在，不发信号，避免把退避时间跳过。

### 新增两条回归测试

```bash
# 1) 状态机（不需要网络）：LoadJobs 绝不能改状态
dotnet run --project tools/OnvifWebDav.Probe -- spool-test

# 2) 并发压测（需要 mock-dav）
dotnet run --project tools/OnvifWebDav.Probe -- mock-dav --port 8097
MSYS_NO_PATHCONV=1 dotnet run --project tools/OnvifWebDav.Probe -- upload-race --url http://127.0.0.1:8097 --user dav --pass dav --root /onvif --jobs 6
```

实测：

```
✔ LoadJobs 不动 Uploading 状态：读到 Uploading
✔ 再读一次还是 Uploading：读到 Uploading
✔ 启动时崩溃恢复生效：恢复 1 个
✔ 恢复后变回 Pending：读到 Pending
✔ 重复调用不再计数
✅ 本地队列状态机正常

并发上传 6 个任务（2 个 worker）→ http://127.0.0.1:8097
  剩余未传：0   上传成功计数：6   日志中 ERROR 行：0
✅ 6 个任务各上传一次，无异常
```

> 注意：Git Bash 里传 `--root /onvif` 会被 MSYS 转成 `/C:/Program Files/Git/onvif`（
> 表现为 `创建根目录失败 /C: → HTTP 403`）。跑这些命令要加 `MSYS_NO_PATHCONV=1`，或者干脆不传（默认就是 `/onvif`）。

### 补充坑（47~49）

47. **`LoadJobs()` 这种"读取"方法绝不能有副作用**。它同时承担了"崩溃恢复"的职责，而它在运行期会被
    频繁调用（认领、计数、入队），副作用立刻变成并发 bug。恢复动作要显式命名、显式时机。
48. **磁盘上的状态字段不能当作"谁在持有"的凭据**。`Uploading` 只是"某个进程正在传"，
    要判断"是不是我，或者是不是活着"，必须有进程内集合（或租约/心跳）。
49. **重试前先检查数据还在不在**。`File.Exists` 一行能省掉 12 次注定失败的重试和一堆刷屏 ERROR。

## 修复：GOP 太长导致丢录像 / 索引刷屏（2026-11-10 线上）

线上日志两类症状：

```
[WARN] rec: 窗口 10:26:00–10:26:07 内没有可用分段，放弃这个片段
[INFO] ffmpeg: 开始合成 1 个分段 → clip.mp4（音频=无）
[INFO] ffmpeg: 合成完成：clip.mp4 0.8MB 2880x1620 h264 12.0s     ← 7 秒窗口出 12 秒片
```

```
[WARN] index: 读索引失败（将回退 PROPFIND）：读取失败 cam1/index/2026-11-10.json → HTTP 500
[WARN] index: 读索引失败（将回退 PROPFIND）：跟随重定向后下载失败（dl1-v6.aliyundrive.cloud → HTTP 403）
（一秒钟十几行，持续几分钟）
```

### 问题 1：摄像头 GOP ≈ 12 秒 → 2 秒窗口切不到段

`-f segment` 只能按关键帧切。摄像头 I 帧间隔 12 秒时，配置 `segmentSeconds=2` 也会切出 **12 秒的段**。
旧代码有三处把"配置的段长"当成真实段长：

| 位置 | 旧逻辑 | 后果 |
|---|---|---|
| `FinalizeAsync` 等待 | `LastCompleteSegmentStart() >= endLocal`，超时 `segment*2+12=16s` | 段长 12s 时要等 **17s** 才满足 → 16s 超时先到 → 放弃 |
| `Collect` 容差 | `startLocal - segmentSeconds(2)` | 窗口整段落在某个 12s 段内部 → 匹配不到 → 空列表 |
| 残片/连续性阈值 | 按 2s 算 | 1 段 12s 被误判成残片；间隔 12s 被误判成"缺口" |

修法：`SegmentRing` 增加 `ObservedSegmentSeconds()`（相邻段起点差的**中位数**，Trim 掉中间段时不会被带偏），
上面三处全部改用它。等待条件也从"完整段起点 >= endLocal"换成更精确的 `IsSegmentClosed(endLocal)`
（存在起点更晚的分段即视为写完，段长 12s 时能早约 10 秒拿到数据）。

另外首次发现 `observed > 配置 × 2` 时打一条 WARN，直接告诉用户去改 I 帧间隔。

> 结论：**GOP 12 秒时切片精度就是 12 秒，代码只能保证"不丢"，保证不了"精确"**。
> 想精确必须在摄像头端把 I 帧间隔改成 1~2 秒。

### 问题 2：索引被每个缩略图/每个 Range 请求打一遍

网页里 `<img src="/thumb/...">` 和 `<video>` 的每个 Range 请求都会走
`FindClipAsync → GetClipsAsync → 读远端索引（失败再 PROPFIND 重建）`。
一页 20 个片段 = 几十次 WebDAV 往返，阿里云盘直接 500/403，然后日志刷屏。

修法：
- `RecordingsRepository` 加**每日索引短 TTL 缓存**（5 秒）+ **失败冷却**（30 秒内不重试，返回旧数据）
- `RemoteClipIndex` 的失败日志改**节流**（每 (摄像头,日期) 2 分钟一条 WARN，其余降 Debug）

缓存 TTL 选 5 秒：新片段最多晚 5 秒出现，但把 WebDAV 请求量从"每张图一次"降到"每 5 秒一次"。

### 问题 3：每个片段都丢音轨

日志里每段都有 `首段音频参数不全（aac，0 声道 / 0Hz），按无音频合成`。
段首确实经常没有音频包，但**末段通常有**。所以：首段探不到音频参数时，再探一次末段，
能探到就按"有音频"处理，交给 `-analyzeduration 50M` 让 ffmpeg 往后找；仍失败还有"无音频重试"兜底。
`ProbeFile` 也补了 `-analyzeduration 5M -probesize 5M`。

### 回归测试（新增场景 C）

```
场景 C：摄像头 GOP 12 秒（配置 2 秒也切不出短段）时，7 秒窗口仍要切到内容
  ✔ 观测到真实段长 12s：12s
  ✔ 短窗口仍能切到内容：2 段
  ✔ 包含窗口末尾的段已写完
  ✔ 正在写的那段不算已写完
```

### 补充坑（50~52）

50. **配置的 `segmentSeconds` 只是"最小粒度意图"，不是事实**。`-f segment` 的实际段长由关键帧决定，
    所有按段长做的推算（等待超时、窗口容差、缺口判定、残片判定）都必须用**观测值**。
51. **`-reset_timestamps 1` 之后每个段自己从 0 开始计时**，所以"段长"只能靠文件名（起始时间）的差值估，
    不能靠段内 PTS。估的时候用中位数，别用最大值（中间被 Trim 掉一段就会虚高）。
52. **网页的每个资源请求都会触发一次"查元数据"**。任何 `FindClipAsync` 级别的远端读取都必须有缓存，
    否则一页缩略图就能把 WebDAV 打爆；日志也要节流，不然真正的错误会被自己的重试日志淹没。
