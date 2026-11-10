# ONVIF 运动录像 → WebDAV

支持 ONVIF 的摄像头检测到画面变化就自动录像，片段上传到云端，自带局域网网页按日期与时间回看。
Windows 下的 WinForm 程序负责配置与托盘常驻，**零第三方 NuGet 依赖**（ONVIF、WebDAV、鉴权全部手写）。

存储后端可选 **WebDAV**（群晖 / Nextcloud / 坚果云 / aliyundrive-webdav 等）或 **夸克网盘**，
两者共用同一套 `IRemoteStorage` 抽象，切换只需在「存储」页改选并重启录像服务。

> 适用于摄像头本身**不支持 Profile G**（拿不到设备端历史录像）的场合 —— 由本程序自建录像链路。

## 功能

| 能力 | 说明 |
|---|---|
| 事件驱动录制 | ONVIF Basic Notification（设备推送）优先，失败自动退回 PullPoint 轮询 |
| 预录 | 环形缓冲常驻录制，事件发生**前**的 N 秒一并保留，不会漏掉"人刚走进画面"那一瞬 |
| 按事件切片 | 从缓冲里按 `[事件时间 − 预录, 末次事件 + 静默 + 片尾]` 切出片段 |
| 事件类型过滤 | 按 topic 子串白名单，只录关心的事件 |
| 上传队列 | 落盘即队列，云端掉线不丢录像，指数退避重试，崩溃后自动恢复 |
| 存储后端 | WebDAV 或夸克网盘二选一（夸克走原生 API，需从客户端借一次登录会话） |
| 网页回看 | 按日期选、时间轴/列表选、Range 代理播放，手机端可用，零 CDN 依赖 |
| 托盘常驻 | 最小化隐藏、开机自启、单实例 |
| 云端配额守卫 | 上传前查剩余空间，低于阈值告警，可选自动清理最旧录像 |
| 诊断工具 | 无摄像头也能验证录制管线、WebDAV/夸克链路、清理逻辑 |

## 工作方式

```
ONVIF 事件 ──► CameraWorker ──► ffmpeg 环形缓冲（TS 分段）
     │                          │
     │                    事件结束 → concat 合成 MP4 + 缩略图
     ▼                          ▼
 UploadQueue ──────────► WebDAV（clips / thumbs / index）
                                │
                 网页(Kestrel) ── 按需下载到本地 LRU 缓存 → Range 播放
```

- 环形缓冲只在本机滚动，**云端只存切出来的片段**
- 每个片段合成后立刻入队上传，按 `年/月/日` 分目录，附一份每日索引 JSON 记录元数据
- 网页端优先读本地缓存，缺失时回源 WebDAV

## 项目结构

| 项目 | 目标框架 | 职责 |
|---|---|---|
| `src/OnvifWebDav.Core` | net9.0 | 配置、日志、ONVIF SOAP、ffmpeg 管线、WebDAV 客户端、索引、上传队列、录制编排 |
| `src/OnvifWebDav.Web` | net9.0 | Kestrel 站点（`OutputType=Library`，只导出 `WebHostFactory.Build`） |
| `src/OnvifWebDav.App` | net9.0-windows | WinForm 配置界面、托盘、单实例、开机自启 |
| `tools/OnvifWebDav.Probe` | net9.0 | 无界面诊断 CLI，回归测试与环境验证 |

## 环境要求

- Windows 10/11 或 Windows Server 2019+
- .NET SDK 9.0 或更高（本仓库用 SDK 10 也能编译 `net9.0-windows`）
- 摄像头支持 ONVIF（**需在摄像头网页里手动开启**，多数机型默认关闭）
- 一个 WebDAV 服务端（群晖、Nextcloud、坚果云、[aliyundrive-webdav](https://github.com/messense/aliyundrive-webdav) 等）
  —— 或者改选夸克网盘后端（需要客户端已登录，首次点「从浏览器获取会话」）

## 快速开始

```bash
git clone https://github.com/ubitsp/onvif-webdav.git
cd onvif-webdav

# 1. 拉 ffmpeg（约 340MB，不进仓库）
powershell -ExecutionPolicy Bypass -File scripts/get-ffmpeg.ps1

# 2. 编译
dotnet build OnvifWebDav.sln

# 3. 运行
dotnet run --project src/OnvifWebDav.App
```

首次进入程序后依次填：

1. **摄像头页** —— 地址、ONVIF 端口（TP-Link 常见 2020）、账号密码 → 点「测试 ONVIF 连接」
2. **存储页** —— 选后端（WebDAV / 夸克网盘）→ 填参数 → 点「测试连接」
   · WebDAV：地址、账号密码、根目录
   · 夸克：程序路径（绿色版必填）、调试端口、网盘根目录 → 点「从浏览器获取会话」
3. **录制页** —— 确认录制模式为「环形缓冲」，按需调整预录与环形保留
4. 启动服务，托盘出现图标即开始工作

## 摄像头侧准备

这一步不做，事件一条都收不到：

1. 摄像头网页 → 设置 → 网络 → **开启 ONVIF**，并**关闭时间校验**（时间不同步会导致鉴权失败）
2. 视频设置里**关闭「智能编码」** —— 它会在画面静止时拉长 I 帧间隔，直接损害切片精度
3. 移动侦测里勾选**报警上报**
4. 编码建议用 **H.264**（H.265 浏览器放不了，只能靠转码，4MP 以上开销明显）

详细步骤与验收清单见 [`docs/摄像头侧准备.md`](docs/摄像头侧准备.md)。

## 验证链路（不接摄像头也能跑）

`Probe` 工具覆盖了整条管线，建议按顺序验：

```bash
# 录制管线：造流 → 分段 → 合成 MP4 → 缩略图（含音频畸形场景）
dotnet run --project tools/OnvifWebDav.Probe -- ffmpeg-selftest

# 环形缓冲保护与容量淘汰
dotnet run --project tools/OnvifWebDav.Probe -- ring-test

# 上传队列状态机（并发不重复认领）
dotnet run --project tools/OnvifWebDav.Probe -- spool-test

# WebDAV 客户端全链路（起一个模拟阿里云盘行为的假服务端）
dotnet run --project tools/OnvifWebDav.Probe -- mock-dav --port 8089 --redirect --no-head
dotnet run --project tools/OnvifWebDav.Probe -- dav-test --url http://127.0.0.1:8089 --user dav --pass dav

# 环形目录可用性与容量估算（内存盘路径先验一遍）
dotnet run --project tools/OnvifWebDav.Probe -- ring-info --dir "R:\onvif-ring" --limit-mb 900 --keep 600

# 连真摄像头：设备能力 / 事件 / 码流
dotnet run --project tools/OnvifWebDav.Probe -- caps   --host <摄像头IP> --user <账号> --pass <密码>
dotnet run --project tools/OnvifWebDav.Probe -- events --host <摄像头IP> --user <账号> --pass <密码> --seconds 60
dotnet run --project tools/OnvifWebDav.Probe -- stream --host <摄像头IP> --user <账号> --pass <密码>
```

## 配置与数据位置

| 内容 | 路径 |
|---|---|
| 配置（含加密密码） | `%LocalAppData%\OnvifWebDav\config.json` |
| 日志 | `%LocalAppData%\OnvifWebDav\logs\` |
| 待上传队列 | `%LocalAppData%\OnvifWebDav\spool\jobs\` |
| 环形缓冲 | 默认 `spool\ring\`，**可指向内存盘** |
| 播放缓存 | `%LocalAppData%\OnvifWebDav\spool\cache\` |

密码用 DPAPI（当前用户作用域）加密，换 Windows 用户后解不开，会退化为空密码。

## 已知限制

- **Profile G 不支持时拿不到设备端历史**：本程序是唯一的录像来源，进程没跑就没有录像
- **切片精度 = 段长**：`-c copy` 只能按关键帧切，摄像头 GOP 多长，精度就是多长。建议把 I 帧间隔压到 1~2 秒
- **推送模式要求设备能连回本机**：回调地址必须在设备可达的网段，跨网段/防火墙会收不到（自动退回轮询）
- **不是取证级方案**：运动检测延迟 0.2~1 秒、切片有 1~2 秒误差，不适合需要帧级对齐的场景

## 更多文档

- [`AGENTS.md`](AGENTS.md) —— 架构细节、接口契约、**52 条踩坑记录**（GOP、音频畸形、并发竞态、阿里云盘适配等）
- [`docs/摄像头侧准备.md`](docs/摄像头侧准备.md) —— 摄像头配置步骤与分阶段验收清单

## 许可

未指定。使用前请自行确认与摄像头、云存储服务商的相关条款。
