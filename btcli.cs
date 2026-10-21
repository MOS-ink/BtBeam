// btcli.cs —— 独立蓝牙命令行客户端（用于远程诊断 BtBeam 的 Serve）
// 用法:
//   btcli.exe probe  --mac XX:XX:XX:XX:XX:XX --ch 6           连接并发送 PING
//   btcli.exe traffic --mac XX:XX:XX:XX:XX:XX --ch 6          连接并发送 TRAFFIC
//   btcli.exe sweep  --mac XX:XX:XX:XX:XX:XX --from 4 --to 10 扫描哪个 channel 可连
//   btcli.exe listen --ch 8 --seconds 20                      本机监听，看能否被连上
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class BtCli
{
    const int AF_BTH = 32;
    const int SOCK_STREAM = 1;
    const int BTPROTO_RFCOMM = 3;
    const int SOL_SOCKET = 0xFFFF;
    const int SO_RCVTIMEO = 0x1006;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct SOCKADDR_BTH
    {
        public ushort addressFamily;
        public ulong btAddr;
        public Guid serviceClassId;
        public uint port;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WSADATA
    {
        public ushort wVersion, wHighVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string szDescription;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string szSystemStatus;
        public ushort iMaxSockets, iMaxUdpDg;
        public IntPtr lpVendorInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TIMEVAL { public int tv_sec; public int tv_usec; }

    [StructLayout(LayoutKind.Sequential)]
    struct FD_SET
    {
        public uint fd_count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public IntPtr[] fd_array;
    }

    [DllImport("ws2_32.dll")] static extern int WSAStartup(ushort v, ref WSADATA d);
    [DllImport("ws2_32.dll")] static extern IntPtr socket(int af, int t, int p);
    [DllImport("ws2_32.dll")] static extern int connect(IntPtr s, ref SOCKADDR_BTH n, int l);
    [DllImport("ws2_32.dll")] static extern int bind(IntPtr s, ref SOCKADDR_BTH n, int l);
    [DllImport("ws2_32.dll")] static extern int listen(IntPtr s, int back);
    [DllImport("ws2_32.dll")] static extern IntPtr accept(IntPtr s, IntPtr a, IntPtr l);
    [DllImport("ws2_32.dll")] static extern int send(IntPtr s, byte[] b, int l, int f);
    [DllImport("ws2_32.dll")] static extern int recv(IntPtr s, byte[] b, int l, int f);
    [DllImport("ws2_32.dll")] static extern int closesocket(IntPtr s);
    [DllImport("ws2_32.dll")] static extern int WSAGetLastError();
    [DllImport("ws2_32.dll")] static extern int ioctlsocket(IntPtr s, int cmd, ref int argp);
    [DllImport("ws2_32.dll")] static extern int setsockopt(IntPtr s, int lv, int opt, ref int v, int len);
    [DllImport("ws2_32.dll")] static extern int select(int n, IntPtr r, ref FD_SET w, IntPtr e, ref TIMEVAL t);
    [DllImport("ws2_32.dll")] static extern int getsockopt(IntPtr s, int lv, int opt, ref int v, ref int len);

    static bool Ready;

    static void Init()
    {
        if (Ready) return;
        WSADATA d = new WSADATA();
        WSAStartup(0x0202, ref d);
        Ready = true;
    }

    static ulong ParseMac(string mac)
    {
        string[] p = mac.Replace("-", ":").Split(':');
        if (p.Length != 6) throw new Exception("bad mac: " + mac);
        ulong v = 0;
        for (int i = 0; i < 6; i++)
            v |= ((ulong)Convert.ToByte(p[i], 16)) << ((5 - i) * 8);
        return v;
    }

    static bool ConnectTimeout(IntPtr s, ref SOCKADDR_BTH a, int ms, out string err)
    {
        err = "";
        int nb = 1;
        ioctlsocket(s, unchecked((int)0x8004667E), ref nb);
        int r = connect(s, ref a, Marshal.SizeOf(typeof(SOCKADDR_BTH)));
        if (r == 0) { Restore(s); return true; }
        int e = WSAGetLastError();
        if (e != 10035) { err = "err=" + e; return false; }

        FD_SET ws = new FD_SET();
        ws.fd_count = 1;
        ws.fd_array = new IntPtr[64];
        ws.fd_array[0] = s;
        TIMEVAL tv = new TIMEVAL();
        tv.tv_sec = ms / 1000;
        tv.tv_usec = (ms % 1000) * 1000;

        int sel = select(0, IntPtr.Zero, ref ws, IntPtr.Zero, ref tv);
        if (sel <= 0) { err = "timeout"; return false; }

        int soErr = 0, soLen = 4;
        getsockopt(s, SOL_SOCKET, 0x1007, ref soErr, ref soLen);
        if (soErr != 0) { err = "soError=" + soErr; return false; }
        Restore(s);
        return true;
    }

    static void Restore(IntPtr s)
    {
        int zero = 0;
        ioctlsocket(s, unchecked((int)0x8004667E), ref zero);
    }

    static void SetRecvTimeout(IntPtr s, int ms)
    {
        int v = ms;
        setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, ref v, 4);
    }

    static string ReadLine(IntPtr s)
    {
        List<byte> buf = new List<byte>();
        byte[] one = new byte[1];
        bool got = false;
        while (buf.Count < 8192)
        {
            int n = recv(s, one, 1, 0);
            if (n <= 0) { if (!got) return null; break; }
            got = true;
            if (one[0] == (byte)'\n') break;
            buf.Add(one[0]);
        }
        if (buf.Count == 0) return "";
        return Encoding.UTF8.GetString(buf.ToArray()).TrimEnd('\r');
    }

    /// 连通性 + 命令测试；返回 null 表示连不上
    static string Probe(string mac, int ch, string cmd, int connectMs, int replyMs, out int elapsedMs)
    {
        Init();
        elapsedMs = 0;
        DateTime t0 = DateTime.UtcNow;
        IntPtr s = socket(AF_BTH, SOCK_STREAM, BTPROTO_RFCOMM);
        if (s.ToInt64() == -1) { elapsedMs = 0; return null; }
        try
        {
            SOCKADDR_BTH a = new SOCKADDR_BTH();
            a.addressFamily = AF_BTH;
            a.btAddr = ParseMac(mac);
            a.port = (uint)ch;

            string err;
            if (!ConnectTimeout(s, ref a, connectMs, out err))
            {
                elapsedMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                return null;
            }
            SetRecvTimeout(s, replyMs);
            byte[] q = Encoding.UTF8.GetBytes(cmd + "\n");
            send(s, q, q.Length, 0);
            string reply = ReadLine(s);
            elapsedMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            return reply == null ? "(null)" : reply;
        }
        catch (Exception ex)
        {
            elapsedMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            return "(EX:" + ex.Message + ")";
        }
        finally { try { closesocket(s); } catch { } }
    }

    static void Main(string[] args)
    {
        try
        {
            string mode = args.Length > 0 ? args[0] : "probe";
            string mac = "80:9D:65:4D:06:C7";
            int ch = 6, from = 4, to = 10, seconds = 20;
            int connectMs = 4000, replyMs = 3000;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--mac": mac = args[++i]; break;
                    case "--ch": ch = int.Parse(args[++i]); break;
                    case "--from": from = int.Parse(args[++i]); break;
                    case "--to": to = int.Parse(args[++i]); break;
                    case "--seconds": seconds = int.Parse(args[++i]); break;
                    case "--connect-ms": connectMs = int.Parse(args[++i]); break;
                    case "--reply-ms": replyMs = int.Parse(args[++i]); break;
                }
            }

            if (mode == "raw")
            {
                string cmd = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "PING";
                // raw 模式下第一个非 -- 参数是命令
                for (int k = 1; k < args.Length; k++)
                {
                    if (!args[k].StartsWith("--") && k == 1) { cmd = args[k]; }
                }
                int ms2;
                string r2 = Probe(mac, ch, cmd, connectMs, replyMs, out ms2);
                Console.WriteLine(string.Format("[{0}] -> {1}  ({2}ms)", cmd, r2 == null ? "CONNECT_FAIL" : r2, ms2));
            }
            else if (mode == "probe" || mode == "traffic")
            {
                string cmd = mode == "probe" ? "PING" : "TRAFFIC";
                int ms;
                string r = Probe(mac, ch, cmd, connectMs, replyMs, out ms);
                Console.WriteLine(string.Format("{0} ch={1} -> {2}  ({3}ms)",
                    cmd, ch, r == null ? "CONNECT_FAIL" : r, ms));
            }
            else if (mode == "sweep")
            {
                for (int c = from; c <= to; c++)
                {
                    int ms;
                    string r = Probe(mac, c, "PING", connectMs, replyMs, out ms);
                    Console.WriteLine(string.Format("  ch={0,-3} {1,-10} {2}ms",
                        c, r == null ? "FAIL" : r, ms));
                }
            }
            else if (mode == "listen")
            {
                Init();
                IntPtr srv = socket(AF_BTH, SOCK_STREAM, BTPROTO_RFCOMM);
                SOCKADDR_BTH a = new SOCKADDR_BTH();
                a.addressFamily = AF_BTH;
                a.btAddr = 0;
                a.port = (uint)ch;
                if (bind(srv, ref a, Marshal.SizeOf(typeof(SOCKADDR_BTH))) != 0)
                {
                    Console.WriteLine("BIND_FAIL err=" + WSAGetLastError());
                    return;
                }
                if (listen(srv, 4) != 0)
                {
                    Console.WriteLine("LISTEN_FAIL err=" + WSAGetLastError());
                    return;
                }
                int nb = 1;
                ioctlsocket(srv, unchecked((int)0x8004667E), ref nb);
                Console.WriteLine("LISTENING ch=" + ch);

                DateTime end = DateTime.UtcNow.AddSeconds(seconds);
                int hit = 0;
                while (DateTime.UtcNow < end)
                {
                    IntPtr c = accept(srv, IntPtr.Zero, IntPtr.Zero);
                    if (c.ToInt64() == -1) { Thread.Sleep(80); continue; }
                    hit++;
                    SetRecvTimeout(c, 2000);
                    string line = ReadLine(c);
                    Console.WriteLine("  CONN#" + hit + " got=[" + (line == null ? "null" : line) + "]");
                    if (line != null && line.Trim() == "PING")
                    {
                        byte[] pong = Encoding.UTF8.GetBytes("PONG\n");
                        send(c, pong, pong.Length, 0);
                    }
                    closesocket(c);
                }
                closesocket(srv);
                Console.WriteLine("TOTAL_CONN=" + hit);
            }
            else
            {
                Console.WriteLine("unknown mode: " + mode);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
