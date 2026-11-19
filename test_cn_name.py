# -*- coding: utf-8 -*-
"""
按 LBBT1 协议往本机 BtBeam 热点口发中文名文件，验证落盘文件名。
输出全部走 ASCII，避免 GBK 控制台编码错误。不删除任何已有文件，只比对前后新增。
"""
import socket, os, time, sys, io

HOST, PORT = "127.0.0.1", 53321
OUT = os.path.join(os.environ["USERPROFILE"], "Downloads")

CASES = [
    ("测试文档_中文名.txt", "hello"),
    ("带 空格 和 中文.doc", "space"),
    ("混合 mixed 名称 2026.pdf", "mix"),
    ("特殊(括号)[方框]{花}#井@at.txt", "special"),
]

def read_line(s):
    buf = bytearray()
    while len(buf) < 8192:
        c = s.recv(1)
        if not c:
            break
        if c == b"\n":
            break
        buf += c
    return bytes(buf).decode("utf-8", "replace").rstrip("\r")

before = set(os.listdir(OUT)) if os.path.isdir(OUT) else set()

print("target  : %s:%d" % (HOST, PORT))
print("outdir  : %s" % OUT)
print("files before: %d" % len(before))
print("-" * 60)

for name, payload in CASES:
    data = payload.encode("utf-8")
    header = 'LBBT1 {"name":"' + name + '","size":' + str(len(data)) + "}\n"
    label = name.encode("ascii", "backslashreplace").decode()
    try:
        s = socket.create_connection((HOST, PORT), timeout=8)
        s.sendall(header.encode("utf-8"))
        ack = read_line(s)
        print("send %-46s ack=%r" % (label, ack))
        if ack.strip() != "OK":
            s.close()
            continue
        s.sendall(data)
        time.sleep(0.3)
        try:
            s.shutdown(socket.SHUT_WR)
        except Exception:
            pass
        s.close()
    except Exception as e:
        print("send %-46s FAILED: %r" % (label, e))

time.sleep(1.5)

after = set(os.listdir(OUT)) if os.path.isdir(OUT) else set()
new = sorted(after - before)

print("-" * 60)
print("new files: %d" % len(new))
for f in new:
    full = os.path.join(OUT, f)
    sz = os.path.getsize(full) if os.path.isfile(full) else -1
    print("  %s  (%d bytes)" % (f.encode("ascii", "backslashreplace").decode(), sz))

print("-" * 60)
print("VERDICT")
allok = True
for name, _ in CASES:
    hit = name in after
    if not hit:
        allok = False
    print("  %-5s %s" % ("PASS" if hit else "FAIL",
                         name.encode("ascii", "backslashreplace").decode()))

# 顺便查有没有 mojibake 痕迹：UTF-8 被当 Latin-1 解出的典型字符
moji = [f for f in new if any(ch in f for ch in ("Ã", "Â", "æ", "è", "é", "ã"))]
if moji:
    print("  mojibake detected: %d" % len(moji))
    for f in moji[:5]:
        print("    %s" % f.encode("ascii", "backslashreplace").decode())

print("-" * 60)
print("RESULT:", "ALL PASS" if allok and not moji else "CHECK NEEDED")
