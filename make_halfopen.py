# -*- coding: utf-8 -*-
"""
从 BtBeam.cs.full 生成半开源版 BtBeam.cs

保留：UI 层、配置、枚举、P/Invoke、工具方法（含 Sys.SafeFileName）、全部注释与协议常量
清空：Link / Hotspot / Traffic / Wifi / Hotlink 的核心实现（方法体换成 throw）

改完 .full 后必须重跑本脚本，否则仓库里的 .cs 会和 exe 对不上。
"""
import io, re

FULL = r"D:\Projects\02-开发项目\lanbridge\BtBeam.cs.full"
OUT = r"D:\Projects\02-开发项目\lanbridge\BtBeam.cs"

NOTICE = """// ============================================================================
//  BtBeam —— 半开源版本
//
//  开源部分：
//    · UI 层（PopupForm / ToastForm / FloatingBall / 托盘图标与菜单 / 关于窗口）
//    · 配置读写、蓝牙设备枚举、文件名清洗、Win32 P/Invoke 声明
//    · 蓝牙协议与热点传输的 方法签名 / 常量 / 全部注释
//
//  未包含实现（方法体为 throw NotImplementedException）：
//    Link      蓝牙 RFCOMM：握手、角色协商、命令通道、文件收发
//    Hotspot   移动热点封装（NetworkOperatorTetheringManager）
//    Traffic   本机 Wi-Fi 实时流量采样
//    Wifi      Wi-Fi 连接、地址获取、profile 管理
//    Hotlink   热点通道 TCP 传输（LBBT1 协议）
//
//  完整功能请使用发行版： https://mosink.top/workshop/
//  授权：PolyForm Noncommercial 1.0.0 —— 仅限个人非商业使用，禁止倒卖与商业集成
// ============================================================================

"""

def strip_strings(line):
    s = re.sub(r'@"(?:[^"]|"")*"', '""', line)
    s = re.sub(r'\$@"(?:[^"]|"")*"', '""', s)
    s = re.sub(r'"(?:[^"\\]|\\.)*"', '""', s)
    s = re.sub(r"'(?:[^'\\]|\\.)'", "''", s)
    return s

TARGETS = [
    ("Link",    "public static bool Ping("),
    ("Link",    "public static string SendCommand("),
    ("Link",    "public static string PreparePeer("),
    ("Link",    "public static string QueryCapability("),
    ("Link",    "public static void SendFile("),
    ("Link",    "public static void Serve("),
    ("Hotspot", "static Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager Mgr("),
    ("Hotspot", "public static void Start("),
    ("Hotspot", "public static string HostIp("),
    ("Traffic", "public static double RateKBps("),
    ("Traffic", "public static bool IsBusy("),
    ("Wifi",    "static string Run("),
    ("Wifi",    "public static string GetHotspotIp("),
    ("Wifi",    "public static bool HasHotspotLink("),
    ("Wifi",    "public static string CurrentSsid("),
    ("Wifi",    "public static void Reconnect("),
    ("Wifi",    "static bool ProfileExists("),
    ("Wifi",    "static bool AlreadyOn("),
    ("Wifi",    "public static bool Connect("),
    ("Hotlink", "public static void Serve("),
    ("Hotlink", "static void ReceiveOne("),
    ("Hotlink", "public static void Send("),
    ("Hotlink", "static string ReadLine("),
]

def class_range(lines, name):
    start = -1
    for i, ln in enumerate(lines):
        if re.match(r'^\s*(static\s+|sealed\s+|public\s+)*class\s+' + name + r'\b', ln):
            start = i
            break
    if start < 0:
        return (-1, -1)
    depth, began = 0, False
    for i in range(start, len(lines)):
        s = strip_strings(lines[i])
        depth += s.count("{") - s.count("}")
        if "{" in s:
            began = True
        if began and depth == 0:
            return (start, i)
    return (start, len(lines) - 1)

def find_method(lines, lo, hi, sig):
    for i in range(lo, hi):
        if sig in lines[i]:
            j = i
            while j < hi and "{" not in strip_strings(lines[j]):
                if lines[j].rstrip().endswith(";"):
                    return (i, None, None)
                j += 1
            if j >= hi:
                return (i, None, None)
            depth = 0
            for k in range(j, hi):
                s = strip_strings(lines[k])
                depth += s.count("{") - s.count("}")
                if depth == 0 and k >= j:
                    return (i, j, k)
            return (i, j, None)
    return (-1, None, None)

def main():
    src = io.open(FULL, encoding="utf-8-sig").read()
    lines = src.split("\n")

    ranges = {}
    for cls in set(c for c, _ in TARGETS):
        ranges[cls] = class_range(lines, cls)

    edits = []
    for cls, sig in TARGETS:
        lo, hi = ranges[cls]
        if lo < 0:
            print("  [%s] 类未找到" % cls)
            continue
        si, bi, ci = find_method(lines, lo, hi, sig)
        if si < 0:
            print("  [%s] 未找到 %s" % (cls, sig[:40]))
            continue
        if bi is None or ci is None:
            print("  [%s] %s 非块体，跳过" % (cls, sig[:40]))
            continue
        edits.append((si, bi, ci, cls, sig))

    edits.sort(key=lambda x: x[0], reverse=True)
    for si, bi, ci, cls, sig in edits:
        indent = re.match(r'^(\s*)', lines[si]).group(1) + "    "
        head = lines[si:bi + 1]
        body = [
            indent + "// 核心实现未包含在开源部分。",
            indent + "// 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。",
            indent + 'throw new NotImplementedException("core implementation not included in the open-source part");',
        ]
        lines[si:ci + 1] = head + body + [lines[ci]]

    out = NOTICE + "\n".join(lines)
    io.open(OUT, "w", encoding="utf-8-sig", newline="\n").write(out)
    print("清空 %d 个方法 -> %s (%d 字符)" % (len(edits), OUT, len(out)))

if __name__ == "__main__":
    main()
