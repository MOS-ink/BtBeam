# BtBeam

两台 Windows 电脑，靠近就能互传文件 —— **不需要同一个网络，不需要账号，不经第三方服务器**。

> **半开源项目**：本仓库公开 UI 层、配置、设备枚举与全部协议的**方法签名和注释**；
> 蓝牙握手、热点协商、流量判断、传输协议等核心实现**不包含在源码中**（方法体为
> `NotImplementedException`）。完整功能请使用 [发行版](https://mosink.top/workshop/)。
> **授权：仅限个人非商业使用**，详见 [LICENSE.md](LICENSE.md)。

```
   蓝牙（RFCOMM）          热点（TCP 53321）
  握手 / 协商角色    →     实际传输数据
   ~1 秒，0.13 MB/s        15～20 MB/s
```

---

## 它解决什么问题

| 方案 | 硬伤 |
|---|---|
| **LocalSend / 飞秋** | 必须同一个局域网 —— 两台机器不在一个网里就没法用 |
| **AirDrop** | 只在苹果生态，Windows 用不了 |
| **Windows 就近共享** | 依赖 WiFi Direct 驱动，实测经常连不上 |
| **微信 / 网盘** | 要账号、要互联网，上传再下载，速度打对折 |

**BtBeam 不需要同一个网络**：它不是去找网络，而是**自己造一个** —— 蓝牙把两台机器"叫到一起"，然后一方开移动热点，另一方连上去，链路就成立了。

适用于：两台机器不在同一个网、根本没有可用网络（机房/展会/临时办公点）、网络有 AP 隔离（很多校园网和公司网禁止客户端互通）。

---

## 快速开始

1. 两台机器都跑 `BtBeam.exe`（托盘常驻，双击即可）
2. 各自连一次蓝牙配对（Windows 设置 → 蓝牙 → 添加设备）
3. 在浮窗里选中对方，把文件拖进去

接收端**无需任何确认** —— 它一直在监听，发出去就到。

- 浮窗：`Ctrl + Alt + B`
- 接收目录：默认 `%USERPROFILE%\Downloads`，可在设置里改

---

## 工作方式

### 一、蓝牙：握手与角色协商

两端都在 RFCOMM 上监听（默认 `channel = 20`）。发送前会问对端两件事：

```
TRAFFIC   → RATE <KB/s>          对端当前网络占用，用来决定谁该断网
PREPARE   → READY <ssid>,<pass>,<ip>    请对端开热点并告诉我地址
HOSTING   → READY / MY_IP <ip>   反向托管：由我开热点、对方连我
PING      → PONG                 存活探测（必须收到回包才算通）
```

### 二、谁断网：比较双方流量

```
myRate   = 本机 Wi-Fi 实时流量（400ms 采样）
peerRate = 通过蓝牙问到的对端流量

谁更闲，谁去当 STA（连热点、承受 2～3 秒断网）
更忙的一方开热点，保持在线
```

三道硬约束，防止把自己锁死：

1. **已经在当热点主机（有客户端连着）→ 绝不去当 STA**，否则热点会被驱动关掉，客户端全部掉线
2. **已经是对端热点的客户端 → 绝不开自己的热点**，否则本机同时有 `192.168.137.1`（我的 AP）和 `192.168.137.207`（对端 STA），往 `.1` 发包会走本地回环，文件发给自己
3. **热点主机收到 `HOSTING` → 回 `READY` 而非 `MY_IP`**，因为两端热点地址都是 `192.168.137.1`，`MY_IP` 无法区分彼此

### 三、传输

```
LBBT1 {"name":"文件.txt","size":12345}\n
<二进制数据>
→ OK
```

- 端口 `53321`，**始终监听**（不管本机开不开热点 —— 纯客户端模式也要能收）
- 重名自动加 `(1)`、`(2)`
- 速率实测 9.5～19.9 MB/s（30 MB 文件约 1.5～3 秒）

### 四、随时可停

传输中浮窗会出现「■ 停止传输」。点击后：

```
所有阻塞操作在每个 channel 之间检查取消标志
  → 蓝牙命令最多等 1.5 秒就返回
  → Wi-Fi 恢复交给 finally，无论成败都恢复原网络
```

**这是踩过坑的地方**：早期版本点停止后会继续扫完 30 个 channel（18.5 秒），因为 `SendCommand` 内部完全不看取消标志。

---

## 编译

需要 .NET Framework 4.8 和 Windows 10 SDK（`csc.exe` 直接调用，不用 MSBuild）：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1              # 编译 + 签名 + 部署
powershell -ExecutionPolicy Bypass -File build.ps1 -NoDeploy    # 只编译
powershell -ExecutionPolicy Bypass -File build.ps1 -NoSign      # 不签名
```

产物：`BtBeam.exe`（约 105 KB，单文件，零运行时依赖）

> 注：编译需要完整实现文件 `BtBeam.cs.full`（不在本仓库中）。
> 本仓库的 `BtBeam.cs` 是半开源删减版，只能通过语法检查，无法产出可运行的程序。

图标由 `make_icon.py` 生成 `.ico`，但**运行时图标是代码画的**（`DrawGlyph`），因为托盘图标需要多尺寸，`Icon` 从文件加载只能拿到一个尺寸。

```powershell
python make_icon.py        # 生成 BtBeam.ico + 预览图
```

---

## 已知限制

**① 需要一方能开移动热点**
Windows 移动热点依赖「Internet 连接配置文件」（`NetworkOperatorTetheringManager`）。如果两台机器都只有无外网的局域网，热点开不起来，只能退回蓝牙（0.13 MB/s）。这是系统限制，绕不过。

**② 两端不能同时开热点**
Windows 移动热点网段固定为 `192.168.137.0/24`，主机地址永远是 `.1`。两端同时开会造成路由冲突。代码里有三道守卫保证同一时刻只有一方是主机。

**③ 蓝牙探测约 1 秒**
蓝牙链路建立（寻呼 + 认证）本身就要 1.1～1.4 秒，这部分优化不掉。但只在发送前发生一次，不影响传输速度。

**④ 只做 Windows**
没有移动端。两端都用 Windows 10 / 11。

---

## 配置文件

`%USERPROFILE%\Desktop\btbeam.ini`（或 exe 同目录）：

```ini
outdir=C:\Users\你\Downloads
channel=20          # 两端必须一致
autostart=0
showall=0
lastmac=F0:B6:1E:7C:23:23
```

**`channel` 两端必须相同** —— 不一致会导致发送方盲扫 channel，而同进程连续 connect 时前一次的寻呼还占着蓝牙链路，后续连接全部超时。

---

## 目录

```
BtBeam.cs            主程序（约 3900 行，单文件）
BtBeam.ico           图标
BtBeam.manifest      UAC 清单
build.ps1            构建脚本
sign.ps1             自签名脚本
make_icon.py         图标生成器
btcli.cs             蓝牙诊断工具（扫描 channel、发原始命令）
btbridge.py          Python 版蓝牙 RFCOMM 封装
hotspot-on.cs        移动热点开关（on/off/status）
send-to-hotspot.ps1  按 LBBT1 协议发文件
RESEARCH.md          开发过程中的技术验证记录
```

---

## 开发记录

`RESEARCH.md` 里有整个开发过程中的技术验证 —— 哪些方案被证伪（WiFi Direct P2P）、哪些坑踩过（热点网段冲突、`InvokeRequired` 在句柄未创建时恒为 false、`ReadLine` 分不清空行和断连）。

---

## 开源范围

| 部分 | 是否开源 | 说明 |
|---|---|---|
| UI 层（`PopupForm` / `ToastForm` / `FloatingBall` / 托盘） | **是** | 完整实现 |
| 配置读写 `Cfg`、蓝牙枚举 `BtEnum`、`WdtDevice` | **是** | 完整实现 |
| Win32 / Winsock P/Invoke 声明 `Wsa`、`Net` | **是** | 完整实现 |
| `Link` 蓝牙 RFCOMM（握手 / 角色协商 / 命令通道 / 文件收发） | **仅签名** | 方法体未包含 |
| `Hotspot` 移动热点封装 | **仅签名** | 方法体未包含 |
| `Wifi` Wi-Fi 连接与地址获取 | **仅签名** | 方法体未包含 |
| `Traffic` 实时流量采样 | **仅签名** | 方法体未包含 |
| `Hotlink` 热点通道 TCP 传输（LBBT1） | **仅签名** | 方法体未包含 |

协议常量、注释、调用关系全部保留 —— 想自己实现的话，签名和语义都是完整的。

## License

**PolyForm Noncommercial License 1.0.0** —— 允许个人学习、研究、业余项目使用，
**禁止任何商业用途**。

完整条款见 [LICENSE.md](LICENSE.md)（中文摘要附在文末，以英文原文为准）。

简单说：

- ✅ 自己用、改、学习、fork 研究
- ✅ 教育机构、慈善组织、政府机构使用
- ❌ 卖钱、商业产品集成、收费服务、企业内部营利部署

Copyright (c) 2026 MOS-ink (墨溟)
