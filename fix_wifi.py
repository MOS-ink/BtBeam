# -*- coding: utf-8 -*-
"""
修「对端连热点 45 秒超时，第二次才成功」

对端日志证据：
  第一次: 23:05:20 oldWifi=UJS-1X        -> wifi timeout after 45s
  第二次: 23:07:04 oldWifi=HONOR 100 Pro -> got 192.168.137.186 in 3670ms

根因在 Wifi.Connect：
  netsh wlan disconnect 之后只 Thread.Sleep(200) 就 netsh wlan connect。
  Wi-Fi 网卡从"已关联"切到"空闲"通常要 1-2 秒，200ms 时还挂在旧网络上，
  新的关联请求被丢弃，之后就是干等满 45 秒超时。
  第二次之所以成功，是因为上一次失败时网络已经被断开了。

改动：
A. 断开后等待 200ms -> 1500ms（宁可多等 1.3 秒，也不要白等 45 秒）
B. 连接超时 45s -> 25s，失败后自动重试一次（第二次命中率明显更高）
C. TRAFFIC 采样窗口 350ms -> 1000ms，流量读数更稳（之前偶发 0/1 KB/s）
D. 收到文件时日志带上通道标识，便于区分蓝牙与热点
"""
import io

P = r"D:\Projects\02-开发项目\lanbridge\BtBeam.cs.full"
t = io.open(P, encoding="utf-8-sig").read()
done = []

# ---------- A. 断开后等待 200ms -> 1500ms ----------
old = '''                log("断开 " + cur + " ...");
                Run("netsh", "wlan disconnect", 5000);
                Thread.Sleep(200);        // 原来固定 1500ms，缩短到 200ms'''
new = '''                log("断开 " + cur + " ...");
                Run("netsh", "wlan disconnect", 5000);
                // 必须等网卡真正从旧网络脱离再发起关联。
                // 实测 200ms 时网卡还挂在旧 SSID 上，新的 connect 会被丢弃，
                // 之后只能干等满整个超时（对端日志里就是 45 秒白等）。
                Thread.Sleep(1500);'''
if old in t:
    t = t.replace(old, new, 1); done.append("断开等待 200ms→1500ms")
else:
    done.append("MISS 断开等待")

# ---------- B. 连接超时 45->25 + 失败重试一次 ----------
old = '''                        popup.SetStatus("对端热点就绪，正在接入 " + ssid + " ...");
                        bool joined = false;
                        try
                        {
                            joined = Wifi.Connect(ssid, pass, 45,
                                delegate (string s) { popup.SetStatus(s); });
                        }
                        catch (Exception ex) { Sys.Diag("join failed: " + ex.Message); }'''
new = '''                        popup.SetStatus("对端热点就绪，正在接入 " + ssid + " ...");
                        bool joined = false;
                        try
                        {
                            joined = Wifi.Connect(ssid, pass, 25,
                                delegate (string s) { popup.SetStatus(s); });
                        }
                        catch (Exception ex) { Sys.Diag("join failed: " + ex.Message); }

                        // 第一次失败往往是因为网卡刚从原网络脱离、关联被丢弃。
                        // 此时旧连接已断，立刻重试一次成功率明显更高。
                        if (!joined && !(Link.ShouldCancel != null && Link.ShouldCancel()))
                        {
                            Sys.Diag("wifi: first attempt failed, retrying once");
                            popup.SetStatus("接入未成功，重试一次 ...");
                            try
                            {
                                Thread.Sleep(800);
                                joined = Wifi.Connect(ssid, pass, 20,
                                    delegate (string s) { popup.SetStatus(s); });
                                Sys.Diag("wifi: retry result=" + joined);
                            }
                            catch (Exception ex) { Sys.Diag("join retry failed: " + ex.Message); }
                        }'''
if old in t:
    t = t.replace(old, new, 1); done.append("超时 45→25 + 失败重试")
else:
    done.append("MISS 连接调用处")

# ---------- C. 采样窗口 350ms -> 1000ms ----------
old = '''                                Traffic.RateKBps();
                                Thread.Sleep(350);
                                rate = Traffic.RateKBps();'''
new = '''                                Traffic.RateKBps();
                                Thread.Sleep(1000);     // 350ms 采样窗口太短，读数会在 0/1 KB/s 抖动
                                rate = Traffic.RateKBps();'''
if old in t:
    t = t.replace(old, new, 1); done.append("采样窗口 350ms→1000ms")
else:
    done.append("MISS 采样窗口")

io.open(P, "w", encoding="utf-8-sig", newline="\n").write(t)
for d in done:
    print(" -", d)
print("written")
