#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
btbridge — 蓝牙点对点直传：不走 IP、不走 Wi-Fi、不需要 AP/路由器/任何服务器。

链路形态
--------
    [PC-A] --- RFCOMM (蓝牙 L2CAP 之上的串口仿真) --- [PC-B]
              无 IP 地址、无 DHCP、无 DNS、无需同一 Wi-Fi

  Windows: ctypes 直调 ws2_32 的 AF_BTH (family=32) + BTPROTO_RFCOMM(3)
           —— CPython 3.14 的 socket 模块没有导出 AF_BTH，所以走底层 Winsock
  Linux  : 标准库 socket.AF_BLUETOOTH + BTPROTO_RFCOMM

用法
----
    python btbridge.py listen                     # 开服务端（默认 channel 25）
    python btbridge.py listen -c 25 -d D:\\Drop
    python btbridge.py send AA:BB:CC:DD:EE:FF a.zip b.mp4
    python btbridge.py probe                      # 自检：蓝牙射频是否可用

真机前置条件
------------
1. 接收端蓝牙必须打开（设置 → 蓝牙和其他设备）。
2. 两台设备需要配对过一次（Windows 首次连接会弹配对确认，Just Works 点一下即可）。
   这一步是"蓝牙版拧到同一个频道"，之后每次连接都是秒连。

实测（本机 Intel AX211，2026-10-18）
------------------------------------
  蓝牙 radio = Off  →  socket() 成功、bind() = WSAENETDOWN(10050)
  打开 radio 后同一份代码即可 bind/listen（WSA 层已通到协议栈）
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import sys
import threading
import time
from pathlib import Path

# --------------------------------------------------------------------------- #
# 蓝牙地址工具
# --------------------------------------------------------------------------- #
def parse_mac(s: str) -> int:
    parts = s.replace("-", ":").replace(".", ":").split(":")
    if len(parts) != 6:
        raise ValueError(f"蓝牙地址格式错误：{s}（应为 AA:BB:CC:DD:EE:FF）")
    v = 0
    for p in parts:
        v = (v << 8) | int(p, 16)
    return v


def fmt_mac(v: int) -> str:
    return ":".join(f"{(v >> (8 * i)) & 0xFF:02X}" for i in range(5, -1, -1))


# --------------------------------------------------------------------------- #
# Windows: AF_BTH over ws2_32
# --------------------------------------------------------------------------- #
AF_BTH = 32
BTPROTO_RFCOMM = 3
SOCK_STREAM = 1

WSA_MSG = {
    10048: "channel 已被占用，换一个 (-c)",
    10049: "地址不可用",
    10050: "蓝牙无线电已关闭 —— 去 设置 > 蓝牙和其他设备 打开蓝牙",
    10051: "网络不可达 —— 对端不在范围内或未配对",
    10060: "连接超时 —— 对端没在监听这个 channel，或未配对",
    10061: "连接被拒绝 —— 对端 channel 不对 / 没有服务在监听",
    10065: "主机不可达 —— 先配对一次再连",
}


class WinBtSocket:
    """把 Winsock 的 AF_BTH 包装成和 Python socket 相近的接口。"""

    _ws2 = None
    _inited = False

    @classmethod
    def _load(cls):
        if cls._ws2 is None:
            import ctypes
            ws2 = ctypes.WinDLL("ws2_32")
            ws2.WSAGetLastError.restype = ctypes.c_int
            ws2.socket.restype = ctypes.c_void_p
            ws2.socket.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_int]
            ws2.bind.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int]
            ws2.listen.argtypes = [ctypes.c_void_p, ctypes.c_int]
            ws2.accept.restype = ctypes.c_void_p
            ws2.accept.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
            ws2.connect.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int]
            ws2.send.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_int]
            ws2.recv.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_int]
            ws2.getsockname.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_int)]
            ws2.closesocket.argtypes = [ctypes.c_void_p]
            cls._ws2 = ws2
        return cls._ws2

    @classmethod
    def _struct(cls):
        import ctypes

        class SOCKADDR_BTH(ctypes.Structure):
            _pack_ = 1                      # ws2bth.h 用 pshpack1.h 包裹，sizeof = 30
            _fields_ = [
                ("addressFamily", ctypes.c_ushort),
                ("btAddr", ctypes.c_ulonglong),
                ("serviceClassId", ctypes.c_byte * 16),
                ("port", ctypes.c_ulong),
            ]
        return SOCKADDR_BTH

    def __init__(self, fd=None):
        import ctypes
        self.ctypes = ctypes
        self.ws2 = self._load()
        self.SA = self._struct()
        if fd is None:
            fd = self.ws2.socket(AF_BTH, SOCK_STREAM, BTPROTO_RFCOMM)
            if fd is None or fd == ctypes.c_void_p(-1).value:
                raise self._err("socket()")
        self.fd = fd

    def _err(self, what: str) -> OSError:
        code = self.ws2.WSAGetLastError()
        hint = WSA_MSG.get(code, "")
        return OSError(f"{what} 失败 (WSA {code}) {hint}".strip())

    def _addr(self, bdaddr: int, channel: int):
        a = self.SA()
        a.addressFamily = AF_BTH
        a.btAddr = bdaddr
        a.port = channel
        return a

    # -- 服务端 --
    def bind(self, bdaddr: int = 0, channel: int = 0) -> None:
        a = self._addr(bdaddr, channel)
        if self.ws2.bind(self.fd, self.ctypes.byref(a), self.ctypes.sizeof(a)) != 0:
            raise self._err(f"bind(channel={channel})")

    def getsockname(self) -> tuple[int, int]:
        a = self.SA()
        n = self.ctypes.c_int(self.ctypes.sizeof(a))
        if self.ws2.getsockname(self.fd, self.ctypes.byref(a), self.ctypes.byref(n)) != 0:
            raise self._err("getsockname()")
        return a.btAddr, a.port

    def listen(self, backlog: int = 1) -> None:
        if self.ws2.listen(self.fd, backlog) != 0:
            raise self._err("listen()")

    def accept(self) -> tuple["WinBtSocket", int]:
        a = self.SA()
        n = self.ctypes.c_int(self.ctypes.sizeof(a))
        fd = self.ws2.accept(self.fd, self.ctypes.byref(a), self.ctypes.byref(n))
        if fd is None or fd == self.ctypes.c_void_p(-1).value:
            raise self._err("accept()")
        return WinBtSocket(fd), a.btAddr

    # -- 客户端 --
    def connect(self, bdaddr: int, channel: int) -> None:
        a = self._addr(bdaddr, channel)
        if self.ws2.connect(self.fd, self.ctypes.byref(a), self.ctypes.sizeof(a)) != 0:
            raise self._err(f"connect({fmt_mac(bdaddr)} ch={channel})")

    # -- 数据 --
    def sendall(self, data: bytes) -> None:
        mv = memoryview(data)
        sent = 0
        while sent < len(data):
            n = self.ws2.send(self.fd, mv[sent:].tobytes(), len(data) - sent, 0)
            if n <= 0:
                raise self._err("send()")
            sent += n

    def recv(self, n: int) -> bytes:
        buf = self.ctypes.create_string_buffer(n)
        r = self.ws2.recv(self.fd, buf, n, 0)
        if r == 0:
            return b""
        if r < 0:
            raise self._err("recv()")
        return buf.raw[:r]

    def close(self) -> None:
        if self.fd:
            self.ws2.closesocket(self.fd)
            self.fd = None


class LinuxBtSocket:
    """Linux 上标准库原生支持，直接用。"""

    def __init__(self, fd=None):
        self.sock = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_STREAM, socket.BTPROTO_RFCOMM) if fd is None else fd

    def bind(self, bdaddr: int = 0, channel: int = 0):
        self.sock.bind((socket.BDADDR_ANY, channel))

    def getsockname(self):
        addr, ch = self.sock.getsockname()
        return (parse_mac(addr) if isinstance(addr, str) else addr), ch

    def listen(self, backlog=1):
        self.sock.listen(backlog)

    def accept(self):
        c, a = self.sock.accept()
        return LinuxBtSocket(c), (parse_mac(a[0]) if isinstance(a[0], str) else a[0])

    def connect(self, bdaddr: int, channel: int):
        self.sock.connect((fmt_mac(bdaddr), channel))

    def sendall(self, data: bytes):
        self.sock.sendall(data)

    def recv(self, n: int):
        return self.sock.recv(n)

    def close(self):
        self.sock.close()


def BtSocket(fd=None):
    return WinBtSocket(fd) if sys.platform == "win32" else LinuxBtSocket(fd)


# --------------------------------------------------------------------------- #
# 应用层协议
#   客户端 → LBBT1 {"name":..,"size":..}\n
#   服务端 → OK\n
#   客户端 → <size 字节裸数据>
#   服务端 → DONE <落盘文件名>\n
# --------------------------------------------------------------------------- #
HANDSHAKE = b"LBBT1 "


def _readline(s, limit=4096) -> bytes:
    buf = b""
    while len(buf) < limit:
        ch = s.recv(1)
        if not ch:
            break
        if ch == b"\n":
            break
        buf += ch
    return buf


def serve(channel: int, outdir: Path, name: str) -> int:
    outdir.mkdir(parents=True, exist_ok=True)

    # Windows 的 RFCOMM bind(port=0) 不会自动分配（getsockname 仍回 0），
    # 所以必须显式挑一个 channel；1 通常被系统 SDP 占用，2..30 可用。
    srv = None
    order = [channel] + [c for c in range(30, 1, -1) if c != channel]
    last_exc: OSError | None = None
    for ch in order:
        s = BtSocket()
        try:
            s.bind(0, ch)
            srv, channel = s, ch
            break
        except OSError as exc:
            last_exc = exc
            s.close()
    if srv is None:
        print(f"[!] 没有可用 channel：{last_exc}")
        return 2

    srv.listen(1)
    local_mac = srv.getsockname()[0]
    print(f"\n  btbridge · {name}")
    print(f"  本机蓝牙地址   = {fmt_mac(local_mac)}")
    print(f"  RFCOMM channel = {channel}")
    print(f"  保存目录       = {outdir}")
    print(f"\n  对端执行：")
    print(f"    python btbridge.py send {fmt_mac(local_mac)} <文件> -c {channel}")
    print("\n  等待连接 ...（Ctrl-C 退出）\n")

    try:
        while True:
            conn, peer = srv.accept()
            t_conn = time.time()
            print(f"[+] 已连接：{fmt_mac(peer)}  @ {time.strftime('%H:%M:%S')} —— 等待握手 ...")
            try:
                line = _readline(conn, 4096)
                if not line:
                    print("[.] 对端连上后一个字节都没发就断了。")
                    print("    这通常说明它连的不是 btbridge 客户端 —— 可能只是配对/系统服务连接，")
                    print("    或者对端 App 走的是 SPP 服务（UUID 00001101），不是 raw channel。")
                    continue
                if line.startswith(HANDSHAKE):
                    _handle_file(conn, line, outdir)
                else:
                    print(f"[?] 收到的不是 btbridge 协议，转入 raw 模式。首包 {len(line)} 字节：")
                    _raw_session(conn, line)
            except (OSError, ValueError, KeyError) as exc:
                print(f"[!] 传输中断：{exc}")
            finally:
                conn.close()
                print(f"[.] 连接关闭（持续 {time.time() - t_conn:.1f}s）\n")
    except KeyboardInterrupt:
        print("\n[.] 退出")
    finally:
        srv.close()
    return 0


def _show(b: bytes) -> str:
    txt = b.decode("utf-8", "replace")
    printable = "".join(c if c.isprintable() or c in "\r\n\t" else "." for c in txt)
    return f"{printable.rstrip()}   [{len(b)}B | hex {b[:24].hex()}{'...' if len(b) > 24 else ''}]"


def _raw_session(conn, first: bytes = b"") -> None:
    """把蓝牙连接当一根管道：对端发什么就打印什么，本地敲什么就回发什么。"""
    print("    ── raw 模式：对端数据实时显示，输入内容回车即回发，Ctrl-C 退出 ──")
    if first:
        print("    << " + _show(first))
    stop = threading.Event()

    def _reader():
        while not stop.is_set():
            try:
                data = conn.recv(4096)
            except OSError as exc:
                print(f"\n    [.] 读异常：{exc}")
                break
            if not data:
                print("\n    [.] 对端已断开")
                break
            print("    << " + _show(data))
            sys.stdout.flush()
        stop.set()

    threading.Thread(target=_reader, daemon=True).start()
    if not sys.stdin.isatty():
        print("    (stdin 不是终端：只收不发，Ctrl-C 退出)")
        while not stop.is_set():
            time.sleep(0.2)
        return
    try:
        while not stop.is_set():
            line = sys.stdin.readline()
            if not line:
                break
            conn.sendall(line.encode("utf-8"))
            print("    >> " + _show(line.encode("utf-8")))
    except KeyboardInterrupt:
        print("\n    [.] 离开 raw 模式")
    stop.set()


def _handle_file(conn, line: bytes, outdir: Path) -> None:
    meta = json.loads(line[len(HANDSHAKE):].decode("utf-8"))
    fname = Path(meta["name"]).name
    size = int(meta["size"])
    dest = outdir / fname
    i = 1
    while dest.exists():
        dest = outdir / f"{Path(fname).stem} ({i}){Path(fname).suffix}"
        i += 1
    print(f"    文件：{fname}  {size} B")
    conn.sendall(b"OK\n")
    got = 0
    t0 = time.time()
    tmp = dest.with_name(dest.name + ".part")
    with tmp.open("wb") as f:
        while got < size:
            chunk = conn.recv(min(1 << 16, size - got))
            if not chunk:
                break
            f.write(chunk)
            got += len(chunk)
            pct = got * 100.0 / size if size else 100
            sys.stdout.write(f"\r    {fname[:40]:<40} {pct:5.1f}%  {got/1024:.0f}KB")
            sys.stdout.flush()
    tmp.replace(dest)
    dt = max(time.time() - t0, 1e-6)
    print(f"\r    {fname}  {got} B  {got/dt/1024:.0f} KB/s{' ' * 20}")
    conn.sendall(b"DONE " + dest.name.encode("utf-8") + b"\n")


def send(mac: str, files: list[str], channel: int) -> int:
    bdaddr = parse_mac(mac)
    rc = 0
    for raw in files:
        p = Path(raw).expanduser()
        if not p.is_file():
            print(f"[!] 不是文件：{raw}")
            rc = 1
            continue
        size = p.stat().st_size
        s = BtSocket()
        try:
            s.connect(bdaddr, channel)
        except OSError as exc:
            print(f"[!] {exc}")
            s.close()
            return 2
        header = HANDSHAKE + json.dumps({"name": p.name, "size": size}, ensure_ascii=False).encode("utf-8") + b"\n"
        s.sendall(header)
        ack = _readline(s)
        if ack.strip() != b"OK":
            print(f"[!] 对端拒绝：{ack[:60]!r}")
            s.close()
            rc = 1
            continue
        t0 = time.time()
        sent = 0
        with p.open("rb") as f:
            while True:
                buf = f.read(1 << 16)
                if not buf:
                    break
                s.sendall(buf)
                sent += len(buf)
                sys.stdout.write(f"\r    {p.name[:40]:<40} {sent*100.0/size:5.1f}%  {sent/1024:.0f}KB")
                sys.stdout.flush()
        line = _readline(s)
        dt = max(time.time() - t0, 1e-6)
        ok = line.startswith(b"DONE")
        print(f"\r    {'[OK]' if ok else '[X]'} {p.name}  {size} B  {size/dt/1024:.0f} KB/s   {line.decode('utf-8', 'replace').strip()}")
        s.close()
        if not ok:
            rc = 1
    return rc


def probe() -> int:
    print(f"平台: {sys.platform}")
    if sys.platform != "win32":
        print("Linux: 需要 bluez 与已配对设备；用 `bluetoothctl` 查看。")
        return 0
    print("RFCOMM: socket(family=32=AF_BTH, proto=3=BTPROTO_RFCOMM)")

    s = BtSocket()
    try:
        s.bind(0, 0)
        mac, _ = s.getsockname()
    except OSError as exc:
        print(f"  [X] {exc}")
        if "10050" in str(exc):
            print("      → 蓝牙开关没开：设置 > 蓝牙和其他设备（或笔记本 Fn 键 / 物理开关）")
        return 1
    finally:
        s.close()

    print(f"  [OK] 蓝牙射频可用")
    print(f"       本机蓝牙地址 = {fmt_mac(mac)}")

    free, taken = [], []
    for ch in range(1, 31):
        t = BtSocket()
        try:
            t.bind(0, ch)
            free.append(ch)
        except OSError:
            taken.append(ch)
        finally:
            t.close()
    print(f"       空闲 channel = {free}")
    if taken:
        print(f"       已占用       = {taken}")

    if not free:
        print("  [X] 没有可用 channel")
        return 1
    t = BtSocket()
    try:
        t.bind(0, free[0])
        t.listen(1)
        print(f"  [OK] listen 成功 @ channel {free[0]} —— 蓝牙链路就绪")
        return 0
    except OSError as exc:
        print(f"  [X] listen 失败：{exc}")
        return 1
    finally:
        t.close()


def main(argv=None) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError, OSError):
            pass
    ap = argparse.ArgumentParser(prog="btbridge", description="蓝牙点对点直传（无 IP / 无 Wi-Fi / 无 AP）")
    ap.add_argument("-n", "--name", default=socket.gethostname())
    sub = ap.add_subparsers(dest="cmd", required=True)

    l = sub.add_parser("listen", help="开 RFCOMM 服务端")
    l.add_argument("-c", "--channel", type=int, default=25)
    l.add_argument("-d", "--dir", default=".", help="保存目录")
    l.set_defaults(func=lambda a: serve(a.channel, Path(a.dir).expanduser().resolve(), a.name))

    s = sub.add_parser("send", help="连到对端并发文件")
    s.add_argument("mac")
    s.add_argument("files", nargs="+")
    s.add_argument("-c", "--channel", type=int, default=25)
    s.set_defaults(func=lambda a: send(a.mac, a.files, a.channel))

    p = sub.add_parser("probe", help="自检蓝牙射频/RFCOMM 是否可用")
    p.set_defaults(func=lambda a: probe())

    args = ap.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
