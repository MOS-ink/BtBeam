# 物理相邻设备互传文件：方案调研与技术可行性

> 采集时间：2026-10-18（GitHub 数据为当日实时 API 读取）
> 结论先行：**这类东西已经非常成熟，不需要自己造。** 真要从零写，可行，且核心只有 ~300 行。

---

## 一、现成的轮子（GitHub 实测）

### A. 全平台局域网互传 —— 最贴合"两台 PC + 手机"

| 项目 | 语言 | Stars | 覆盖端 | 要点 |
|---|---|---|---|---|
| [localsend/localsend](https://github.com/localsend/localsend) | Dart/Flutter | ~94.7k | Win / macOS / Linux / Android / iOS | **首选**。协议公开（LocalSend Protocol），UDP 组播发现 + HTTPS `53317`，无服务器无账号 |
| [linuxmint/warpinator](https://github.com/linuxmint/warpinator) | C / Python | ~1.6k | Linux / Win / Android | Mint 出品，组播发现，界面朴实 |
| [LANDrop/LANDrop](https://github.com/LANDrop/LANDrop) | C++ | ~6.0k | 三端 + 移动 | 2024-08 后基本停更 |
| [marchellodev/sharik](https://github.com/marchellodev/sharik) | Dart | ~1.3k | 三端 + 移动 | 一方开热点，另一方扫码用浏览器收 |

### B. 零安装（手机什么都不装）

| 项目 | 语言 | Stars | 要点 |
|---|---|---|---|
| [schlagmichdoch/PairDrop](https://github.com/schlagmichdoch/PairDrop) | JavaScript | ~11.6k | 浏览器对浏览器，WebRTC + 4 位短码配对，同一 Wi-Fi 自动互相看见；可自托管 |
| [claudiodangelis/qrcp](https://github.com/claudiodangelis/qrcp) | Go | ~10.5k | CLI 起 HTTP 服务 + 终端里画二维码，手机扫码上下传 |

### C. 不要求同网段的点对点（跨网也能用，同为局域网时直连）

| 项目 | 语言 | Stars | 要点 |
|---|---|---|---|
| [schollz/croc](https://github.com/schollz/croc) | Go | ~40.6k | 口令派生的 PAKE 加密，中继兜底；一段 `croc send` / `croc <code>` 就能用 |
| [magic-wormhole/magic-wormhole](https://github.com/magic-wormhole/magic-wormhole) | Python | ~23k | 同上思路，更偏库/协议参考实现 |
| [n0-computer/sendme](https://github.com/n0-computer/sendme) | Rust | ~1.2k | 基于 iroh 的 QUIC，NAT 打洞，同一局域网会直接走本地链路 |

### D. 生态绑定 / 协议逆向

| 项目 | 语言 | Stars | 说明 |
|---|---|---|---|
| [KDE/kdeconnect-kde](https://github.com/KDE/kdeconnect-kde) + [android](https://github.com/KDE/kdeconnect-android) | C++ / Kotlin | ~4.1k + 1.5k | 文件之外还有剪贴板、通知、媒体控制；同 Wi-Fi 自动发现 |
| [grishka/NearDrop](https://github.com/grishka/NearDrop) | Swift | ~6.3k | 逆向 Google Nearby Share/Quick Share 的 macOS 实现 |
| [seemoo-lab/owl](https://github.com/seemoo-lab/owl) | C | ~1.6k | 逆向 AirDrop 的 AWDL 链路层，Linux 上跑通 AirDrop 的基石 |

### E. 系统自带（不装任何东西）

- **Windows 就近共享**：蓝牙发现 + Wi-Fi 直连，**只支持 Windows ↔ Windows**。
- **Quick Share（Android）**：Google 官方有 Windows 客户端，Android ↔ Windows 可用。
- **AirDrop**：Apple 生态内。
- **Wi-Fi Aware（NAN）**：Android 8+ / iOS 26 起支持，是未来跨平台近场互传的公共底座；Windows 桌面侧目前没有可用的公开 API。

### F. 结论

- 只要两端**在同一个 Wi-Fi/路由器**下：`LocalSend` 装上就能用，双 PC + 手机全包。
- 一点软件都不想装（比如对方是别人的电脑/手机）：`PairDrop` 开网页，或自己起个 HTTP 服务让对方浏览器打开。
- 完全不在同一网络：`croc` / `sendme`。

**所以"有没有"这个问题的答案是：有，而且不止一个成熟方案。**

---

## 二、如果自己实现：技术拆解

一个近场传输系统只有三件事：**发现 → 配对 → 搬字节**。

### 1. 发现（Find）

| 方案 | 实现成本 | 覆盖 | 备注 |
|---|---|---|---|
| UDP 组播 / 广播 | 极低（10 行） | 同一广播域 | 224.0.0.x + 心跳；Windows 上多网卡/VPN 会捣乱，要按接口各发一份 |
| mDNS / DNS-SD | 中（要处理 `_svc._tcp.local` 与 PTR/SRV/TXT） | 同网段 | 苹果生态标准；Android 有 `NsdManager`，Windows 需自带实现或用 Bonjour |
| BLE 广播 | 中 | 无网络也能发现 | 只广播几十字节的"我在这" |
| Wi-Fi Direct / NAN | 高 | 无 AP 也行 | Android 好用；Windows 只有 UWP 的 `WiFiDirect` API，桌面程序接入别扭 |
| 二维码 | 极低 | 只要有网 | 最可靠：绕开发现机制，直接告诉你 IP:PORT |

### 2. 配对（Pair）

- **无验证**：同网段即信任。最省事，公共 Wi-Fi 下不安全。
- **PIN / 短码**：接收端显示 6 位，发送端输入。实现成本低，体验可接受。
- **二维码内嵌一次性 token**：扫码即授权，体验最好。
- **证书指纹校验**：LocalSend 的做法——HTTPS 自签证书，首次连接比对 SHA-256 指纹。
- **PAKE / SPAKE2**：croc、wormhole 的做法，口令本身不落地，能防中间人。

### 3. 搬字节（Transfer）

| 通道 | 实测/理论 | 适用 |
|---|---|---|
| **HTTP(S) over TCP** | 千兆/ Wi-Fi 6 下 50–110 MB/s | ★ 首选：通用、能断点续传、浏览器直接可用 |
| 裸 TCP 自定义帧 | 略高于 HTTP（省掉头部开销） | 追求极限时 |
| QUIC | 与 HTTP/3 相当，弱网更好 | 需要 NAT 穿透时 |
| WebRTC DataChannel | 20–100 MB/s（局域网直连） | 浏览器 ↔ 浏览器，PairDrop 用的就是它 |
| **经典蓝牙 OBEX/RFCOMM** | 100 KB/s – 2 MB/s | 只适合小文件/应急 |
| **BLE** | 10–100 KB/s | 只适合控制信令，不要用来传文件 |
| Wi-Fi Direct socket | 20–60 MB/s | 无 AP 场景 |

---

## 三、蓝牙到底行不行？

这是最容易被误解的一点，分两层看：

**做传输通道：不行（大文件）。**
- 经典蓝牙 EDR 理论 2.1 Mbps ≈ 260 KB/s 上限，实测 OBEX OPP 常在 100–300 KB/s，好环境能到 1–2 MB/s。
- 传一张 5 MB 照片：20 秒到 1 分钟。传一段 500 MB 视频：半小时起步。
- BLE 更慢，2M PHY 的应用层吞吐也就百 KB/s 量级。
- Windows 侧链路仍在：`C:\Windows\System32\fsquirt.exe`（蓝牙文件传输向导）存在，`bthserv` 服务运行中，说明系统能力没被砍，只是入口藏得深。本次环境实测：`fsquirt.exe` = True，`bthserv` = Running。

**做发现 + 配对：非常好，而且是行业标准做法。**
- AirDrop 用蓝牙做设备发现，用 **AWDL**（苹果私有的 Wi-Fi 点对点链路）搬数据。
- Google Nearby / Quick Share 用蓝牙（或 BLE）发现，用 Wi-Fi 直连搬数据。
- 你的直觉"蓝牙或者其他 p2p"其实正是正确答案的形状：**蓝牙负责"看见彼此"，Wi-Fi 负责"搬东西"**。

---

## 四、没有路由器 / 没有 Wi-Fi 怎么办

按可靠性排序：

1. **一方开热点**：Windows「移动热点」→ 另一台连上 → 走普通局域网协议。最省心。
2. **网线直连**：现代网卡都支持 Auto-MDIX，网线对插后双方自动拿 `169.254.x.x`（APIPA）链路本地地址，直接 HTTP/SMB 就能传。**这是最被低估的方案**，千兆线直连能跑满 110 MB/s。
3. **Wi-Fi Direct**：Android 侧成熟；Windows 桌面接入成本高。
4. **Wi-Fi Aware / NAN**：设备支持就行，无需 AP；跨平台生态还在长（iOS 26 已跟进）。
5. **蓝牙**：能通，但慢，只当兜底。
6. **U 盘**：别笑，很多场景下这是最优解。

---

## 五、把上面这套做出来的最小实现

见同目录 `lanbridge.py`（单文件、纯标准库、约 900 行）：

- 发现：UDP 组播 `224.0.0.167:53998`，3s 心跳 / 12s 判死，按网卡逐个发（规避 VPN 抢默认路由）
- 传输：HTTP/1.1 裸 body（不解析 multipart，省掉一个大坑），支持 `Range` 断点
- 手机端：内置网页，扫码即用，可上传/下载/**把文件转发给局域网内另一台已发现设备**
- 安全：可选 PIN，PIN 只在本机打印，不进组播；文件名强制 basename + `resolve()` 越界校验
- 实测：本机 64 MB 走真实网卡 **97.6 MB/s**；SHA-256 前后一致；`../../` 穿越返回 404；PIN 保护生效

技术栈全部是操作系统自带能力，没有任何第三方依赖——这也反过来证明：**这件事的技术门槛在"打磨"，不在"能不能"。**
