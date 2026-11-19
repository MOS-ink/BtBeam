// ============================================================================
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

// BtBeam.cs — 蓝牙点对点文件传输（托盘常驻版）
// 编译：
//   csc /nologo /target:winexe /optimize+ /out:BtBeam.exe ^
//       /r:System.Windows.Forms.dll /r:System.Drawing.dll BtBeam.cs
//
// 设计：
//   * 全程只用 BD_ADDR + RFCOMM，不碰 IP、不碰 Wi-Fi
//   * 托盘常驻，左键点击弹出浮窗，失焦自动隐藏
//   * 后台线程常驻监听，空闲时阻塞在 accept，CPU 占用 0
//   * 设备按 Class of Device 过滤，只显示电脑和手机

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("BtBeam")]
[assembly: AssemblyDescription("蓝牙点对点文件传输")]
[assembly: AssemblyProduct("BtBeam")]
[assembly: AssemblyCompany("mosink.top")]
[assembly: AssemblyCopyright("Copyright (c) 2026 MOS-ink · mosink.top · PolyForm Noncommercial 1.0.0")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace BtBeam
{
    // =====================================================================
    // Winsock: AF_BTH / RFCOMM
    // =====================================================================
    static class Wsa
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct SOCKADDR_BTH
        {
            public ushort addressFamily;
            public ulong btAddr;
            public Guid serviceClassId;
            public uint port;
        }

        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern IntPtr socket(int af, int type, int protocol);
        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern int bind(IntPtr s, ref SOCKADDR_BTH name, int namelen);
        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern int listen(IntPtr s, int backlog);
        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern IntPtr accept(IntPtr s, ref SOCKADDR_BTH addr, ref int addrlen);
        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern int connect(IntPtr s, ref SOCKADDR_BTH name, int namelen);
        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern int send(IntPtr s, IntPtr buf, int len, int flags);
        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern int recv(IntPtr s, byte[] buf, int len, int flags);
        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern int closesocket(IntPtr s);
        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern int getsockname(IntPtr s, ref SOCKADDR_BTH name, ref int namelen);
        [DllImport("ws2_32.dll")]
        public static extern int WSAGetLastError();

        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern int setsockopt(IntPtr s, int level, int optname, ref int optval, int optlen);

        /// 设置接收超时（毫秒）。0 表示无限等待。
        public static void SetRecvTimeout(IntPtr s, int ms)
        {
            try
            {
                int v = ms;
                setsockopt(s, 0xFFFF, 0x1006, ref v, 4);   // SOL_SOCKET, SO_RCVTIMEO
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WsaTimeval { public int tv_sec; public int tv_usec; }

        [StructLayout(LayoutKind.Sequential)]
        public struct WsaFdSet
        {
            public uint fd_count;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public IntPtr[] fd_array;
        }

        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern int ioctlsocket(IntPtr s, int cmd, ref int argp);

        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern int select(int nfds, IntPtr readfds, ref WsaFdSet writefds, IntPtr exceptfds, ref WsaTimeval timeout);

        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern int getsockopt(IntPtr s, int level, int optname, ref int optval, ref int optlen);

        /// 带超时的 RFCOMM 连接（Windows 默认 connect 可能阻塞 30 秒以上）
        /// 带超时的 RFCOMM 连接。返回 true 时 socket 已恢复为阻塞模式。
        public static bool ConnectWithTimeout(IntPtr s, ref SOCKADDR_BTH addr, int timeoutMs)
        {
            int nonBlocking = 1;
            int ioc = ioctlsocket(s, unchecked((int)0x8004667E), ref nonBlocking);   // FIONBIO
            Sys.Diag("    cwt ioctlsocket=" + ioc);

            int r = connect(s, ref addr, Marshal.SizeOf(typeof(SOCKADDR_BTH)));
            int err = WSAGetLastError();
            Sys.Diag("    cwt connect=" + r + " err=" + err);

            if (r == 0)
            {
                RestoreBlocking(s);
                return true;
            }
            if (err != 10035) return false;

            WsaFdSet ws = new WsaFdSet();
            ws.fd_count = 1;
            ws.fd_array = new IntPtr[64];
            ws.fd_array[0] = s;

            WsaTimeval tv = new WsaTimeval();
            tv.tv_sec = timeoutMs / 1000;
            tv.tv_usec = (timeoutMs % 1000) * 1000;

            int sel = select(0, IntPtr.Zero, ref ws, IntPtr.Zero, ref tv);
            Sys.Diag("    cwt select=" + sel + " wsaErr=" + WSAGetLastError());
            if (sel <= 0) return false;

            int soErr = 0;
            int soLen = 4;
            int gso = getsockopt(s, 0xFFFF, 0x1007, ref soErr, ref soLen);
            Sys.Diag("    cwt getsockopt=" + gso + " soError=" + soErr);
            if (soErr != 0) return false;

            RestoreBlocking(s);
            return true;
        }

        /// 连接完成后必须恢复阻塞，否则后续 recv 会立刻返回 WSAEWOULDBLOCK
        static void RestoreBlocking(IntPtr s)
        {
            int blocking = 0;
            ioctlsocket(s, unchecked((int)0x8004667E), ref blocking);
        }
        [DllImport("ws2_32.dll", CharSet = CharSet.Ansi)]
        public static extern int WSAStartup(ushort wVersionRequested, byte[] lpWSAData);

        public const int AF_BTH = 32;
        public const int SOCK_STREAM = 1;
        public const int BTPROTO_RFCOMM = 3;

        static bool _ready = false;

        public static void Ensure()
        {
            if (_ready) return;
            byte[] d = new byte[512];
            int r = WSAStartup(0x0202, d);
            if (r != 0) throw new Exception("WSAStartup 失败：" + r);
            _ready = true;
        }

        public static string FmtMac(ulong v)
        {
            return string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                (v >> 40) & 0xFF, (v >> 32) & 0xFF, (v >> 24) & 0xFF,
                (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        }

        public static ulong ParseMac(string mac)
        {
            string[] p = mac.Replace("-", ":").Replace(".", ":").Split(':');
            if (p.Length != 6) throw new Exception("蓝牙地址格式应为 AA:BB:CC:DD:EE:FF");
            ulong v = 0;
            for (int i = 0; i < 6; i++) v = (v << 8) | Convert.ToUInt64(p[i], 16);
            return v;
        }

        public static string WsaText(int code)
        {
            switch (code)
            {
                case 10013: return "channel 被系统占用";
                case 10048: return "channel 已在用";
                case 10049: return "channel 无效（1-30）";
                case 10050: return "蓝牙已关闭";
                case 10051: return "网络不可达，请先配对";
                case 10060: return "连接超时，对端未监听或未配对";
                case 10061: return "连接被拒绝，channel 不对";
                case 10065: return "主机不可达，请先配对";
                default: return "错误码 " + code;
            }
        }

        public static void SendAll(IntPtr s, byte[] buf, int off, int count)
        {
            GCHandle h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                long b = h.AddrOfPinnedObject().ToInt64();
                int sent = 0;
                while (sent < count)
                {
                    int n = send(s, new IntPtr(b + off + sent), count - sent, 0);
                    if (n <= 0) throw new Exception("发送失败：" + WsaText(WSAGetLastError()));
                    sent += n;
                }
            }
            finally { h.Free(); }
        }

        /// 读一行（按字节收集，最后用 UTF-8 解码，避免中文文件名乱码）
        /// 读一行。返回 null 表示"连接已关闭"，返回 "" 表示"空行但连接仍在"。
        /// 这个区分很关键：Serve 用 null 判断退出，避免在死连接上空转。
        public static string ReadLine(IntPtr s)
        {
            List<byte> buf = new List<byte>();
            byte[] one = new byte[1];
            bool gotAny = false;
            while (buf.Count < 8192)
            {
                int n = recv(s, one, 1, 0);
                if (n <= 0)
                {
                    // 一个字节都没读到就断了 -> 连接关闭
                    if (!gotAny) return null;
                    break;
                }
                gotAny = true;
                if (one[0] == (byte)'\n') break;
                buf.Add(one[0]);
            }
            if (buf.Count == 0) return "";
            string text = Encoding.UTF8.GetString(buf.ToArray());
            return text.TrimEnd('\r');
        }

        public static string LocalMac()
        {
            Ensure();
            IntPtr s = socket(AF_BTH, SOCK_STREAM, BTPROTO_RFCOMM);
            if (s.ToInt64() == -1) throw new Exception(WsaText(WSAGetLastError()));
            try
            {
                SOCKADDR_BTH a = new SOCKADDR_BTH();
                a.addressFamily = AF_BTH;
                if (bind(s, ref a, Marshal.SizeOf(typeof(SOCKADDR_BTH))) != 0)
                    throw new Exception(WsaText(WSAGetLastError()));
                SOCKADDR_BTH me = new SOCKADDR_BTH();
                int n = Marshal.SizeOf(typeof(SOCKADDR_BTH));
                getsockname(s, ref me, ref n);
                return FmtMac(me.btAddr);
            }
            finally { closesocket(s); }
        }
    }

    // =====================================================================
    // 系统 API：下载目录 / 图标
    // =====================================================================
    static class Sys
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        static readonly Guid FOLDERID_Downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        public static string DownloadsDir()
        {
            try
            {
                IntPtr p;
                Guid g = FOLDERID_Downloads;
                int hr = SHGetKnownFolderPath(ref g, 0, IntPtr.Zero, out p);
                if (hr == 0 && p != IntPtr.Zero)
                {
                    string s = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(s) && Directory.Exists(s)) return s;
                }
            }
            catch { }
            string fb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(fb)) return fb;
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        /// 把对端送来的文件名收拾成能落盘的：去路径、换非法字符、截长度。
        /// 顺带兜住极端情况的空名与纯点名（"." ".."）。
        public static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            try
            {
                string only = Path.GetFileName(name);
                if (!string.IsNullOrEmpty(only)) name = only;
            }
            catch { }

            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            }

            string r = sb.ToString().Trim();
            r = r.TrimEnd('.');
            if (r.Length == 0) r = "unnamed";
            if (r.Length > 180)
            {
                string ext = Path.GetExtension(r);
                if (ext.Length > 16) ext = "";
                r = r.Substring(0, 180 - ext.Length) + ext;
            }
            return r;
        }

        public static string ExeDir()
        {
            return Path.GetDirectoryName(Application.ExecutablePath);
        }

        public static void OpenPath(string p)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + p + "\""); }
            catch { }
        }

        public static void OpenPairing()
        {
            try
            {
                string w = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DevicePairingWizard.exe");
                if (File.Exists(w)) System.Diagnostics.Process.Start(w);
                else System.Diagnostics.Process.Start("ms-settings:bluetooth");
            }
            catch { }
        }

        public static void OpenTraySettings()
        {
            try { System.Diagnostics.Process.Start("ms-settings:taskbar"); } catch { }
        }

        static readonly object logLock = new object();
        static bool logInit = false;

        public static void Diag(string s)
        {
            try
            {
                lock (logLock)
                {
                    string p = Path.Combine(ExeDir(), "btbeam.log");
                    string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n";
                    if (!logInit)
                    {
                        File.WriteAllText(p, line, new UTF8Encoding(true));
                        logInit = true;
                    }
                    else File.AppendAllText(p, line, new UTF8Encoding(true));
                }
            }
            catch { }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr h, uint acc, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr tok, int cls, IntPtr buf, int len, out int ret);
        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetModuleFileName(IntPtr hModule, StringBuilder lpFilename, int nSize);

        /// 完整性级别低于「中」时，Shell_NotifyIcon 一定会失败
        public static string IntegrityLevel()
        {
            try
            {
                IntPtr tok;
                if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out tok)) return "?";
                try
                {
                    int len;
                    GetTokenInformation(tok, 25, IntPtr.Zero, 0, out len);
                    if (len <= 0) return "?";
                    IntPtr buf = Marshal.AllocHGlobal(len);
                    try
                    {
                        if (!GetTokenInformation(tok, 25, buf, len, out len)) return "?";
                        IntPtr pSid = Marshal.ReadIntPtr(buf, 0);
                        byte count = Marshal.ReadByte(pSid, 1);
                        int off = 8 + (count - 1) * 4;
                        int rid = Marshal.ReadInt32(pSid, off);
                        if (rid == 0x1000) return "Low";
                        if (rid == 0x2000) return "Medium";
                        if (rid == 0x3000) return "High";
                        if (rid == 0x4000) return "System";
                        return "0x" + rid.ToString("X");
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { CloseHandle(tok); }
            }
            catch { return "?"; }
        }

        public static string ExePath()
        {
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                GetModuleFileName(IntPtr.Zero, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return "?"; }
        }
    }

    // =====================================================================
    // 全局热键 + 跨进程唤醒
    // Windows 11 常把新程序的托盘图标折叠进溢出区，所以入口不能只靠托盘
    // =====================================================================
    static class Net
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_NOREPEAT = 0x4000;
        public const int VK_B = 0x42;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string lpString);

        public const int HWND_BROADCAST = 0xFFFF;
        public static readonly int WM_SHOW = RegisterWindowMessage("BtBeam_Show_Popup_v2");
        public static readonly int WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");
    }

    class MessageHost : Form
    {
        public Action OnHotkey;
        public Action OnShow;
        public Action<int> OnTrayMouse;
        public Action OnTaskbarCreated;

        public MessageHost()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);
            ClientSize = new Size(1, 1);
            Opacity = 0;
            Visible = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Net.WM_HOTKEY)
            {
                if (OnHotkey != null) OnHotkey();
                return;
            }
            if (Net.WM_SHOW != 0 && m.Msg == Net.WM_SHOW)
            {
                if (OnShow != null) OnShow();
                return;
            }
            if (m.Msg == Tray.WM_TRAY)
            {
                int ev = (int)(m.LParam.ToInt64() & 0xFFFF);
                if (OnTrayMouse != null) OnTrayMouse(ev);
                return;
            }
            if (Net.WM_TASKBARCREATED != 0 && m.Msg == Net.WM_TASKBARCREATED)
            {
                if (OnTaskbarCreated != null) OnTaskbarCreated();
                return;
            }
            base.WndProc(ref m);
        }
    }

    // =====================================================================
    // 原生托盘图标：绕过 WinForms NotifyIcon（它会把 Shell_NotifyIcon 的失败吞掉）
    // =====================================================================
    static class Tray
    {
        public const uint NIM_ADD = 0;
        public const uint NIM_MODIFY = 1;
        public const uint NIM_DELETE = 2;

        public const uint NIF_MESSAGE = 0x01;
        public const uint NIF_ICON = 0x02;
        public const uint NIF_TIP = 0x04;
        public const uint NIF_STATE = 0x08;
        public const uint NIS_HIDDEN = 0x01;

        public const int WM_TRAY = 0x8000 + 1;   // WM_APP + 1

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        public static NOTIFYICONDATA Build(IntPtr hWnd, uint id, IntPtr hIcon, string tip)
        {
            NOTIFYICONDATA d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = hWnd;
            d.uID = id;
            d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            d.uCallbackMessage = WM_TRAY;
            d.hIcon = hIcon;
            d.szTip = tip;
            d.dwState = 0;                 // 0 = 不隐藏
            d.dwStateMask = NIS_HIDDEN;    // 显式声明"我要改的是隐藏位"
            return d;
        }
    }

    // =====================================================================
    // 设备枚举（带 Class of Device 分类）
    // =====================================================================
    class BtDevice
    {
        public string Name;
        public ulong Addr;
        public uint Cod;
        public bool Connected;

        public int MajorClass { get { return (int)((Cod >> 8) & 0x1F); } }

        public bool IsComputer { get { return MajorClass == 1; } }
        public bool IsPhone { get { return MajorClass == 2; } }

        public string KindName
        {
            get
            {
                switch (MajorClass)
                {
                    case 0: return "设备";
                    case 1: return "电脑";
                    case 2: return "手机";
                    case 3: return "网关";
                    case 4: return "音频";
                    case 5: return "外设";
                    case 6: return "影像";
                    case 7: return "穿戴";
                    case 8: return "玩具";
                    case 9: return "健康";
                    default: return "其他";
                }
            }
        }

        public string Glyph
        {
            get
            {
                if (IsComputer) return "\uE7F4";   // Segoe MDL2: Devices
                if (IsPhone) return "\uE8EA";      // CellPhone
                return "\uE772";                   // Generic
            }
        }
    }

    static class BtEnum
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BLUETOOTH_FIND_RADIO_PARAMS { public int dwSize; }

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct BLUETOOTH_DEVICE_INFO
        {
            public int dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
            [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
            [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
            public SYSTEMTIME stLastSeen;
            public SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public int dwSize;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
            [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr phRadio);
        [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS p, ref BLUETOOTH_DEVICE_INFO info);
        [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO info);
        [DllImport("bthprops.cpl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool BluetoothFindDeviceClose(IntPtr hFind);
        [DllImport("bthprops.cpl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool BluetoothFindRadioClose(IntPtr hFind);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);

        public static List<BtDevice> List(bool includeUnknown)
        {
            List<BtDevice> result = new List<BtDevice>();
            BLUETOOTH_FIND_RADIO_PARAMS rp = new BLUETOOTH_FIND_RADIO_PARAMS();
            rp.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS));
            IntPtr hRadio;
            IntPtr hFindRadio = BluetoothFindFirstRadio(ref rp, out hRadio);
            if (hFindRadio == IntPtr.Zero) throw new Exception("找不到蓝牙无线电，请先打开蓝牙");

            try
            {
                BLUETOOTH_DEVICE_SEARCH_PARAMS sp = new BLUETOOTH_DEVICE_SEARCH_PARAMS();
                sp.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS));
                sp.fReturnAuthenticated = true;
                sp.fReturnRemembered = true;
                sp.fReturnConnected = true;
                sp.fReturnUnknown = includeUnknown;
                sp.fIssueInquiry = false;
                sp.cTimeoutMultiplier = 1;
                sp.hRadio = hRadio;

                BLUETOOTH_DEVICE_INFO info = new BLUETOOTH_DEVICE_INFO();
                info.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));

                IntPtr hFind = BluetoothFindFirstDevice(ref sp, ref info);
                if (hFind != IntPtr.Zero)
                {
                    try
                    {
                        int guard = 0;
                        do
                        {
                            BtDevice d = new BtDevice();
                            d.Name = string.IsNullOrEmpty(info.szName) ? "(无名设备)" : info.szName;
                            d.Addr = info.Address;
                            d.Cod = info.ulClassofDevice;
                            d.Connected = info.fConnected;
                            bool dup = false;
                            for (int i = 0; i < result.Count; i++)
                                if (result[i].Addr == d.Addr) { dup = true; break; }
                            if (!dup) result.Add(d);
                            guard++;
                        }
                        while (guard < 200 && BluetoothFindNextDevice(hFind, ref info));
                    }
                    finally { BluetoothFindDeviceClose(hFind); }
                }
                if (hRadio != IntPtr.Zero) CloseHandle(hRadio);
            }
            finally { BluetoothFindRadioClose(hFindRadio); }

            result.Sort(delegate (BtDevice a, BtDevice b)
            {
                if (a.Connected != b.Connected) return a.Connected ? -1 : 1;
                return string.Compare(a.Name, b.Name, true);
            });
            return result;
        }
    }

    // =====================================================================
    // 传输协议：LBBT1 {"name":..,"size":..}\n -> OK\n -> 裸字节 -> DONE x\n
    // =====================================================================
    static class Link
    {
        const string HS = "LBBT1 ";

        /// 实际绑定成功的 RFCOMM channel（1-3 被系统 SDP/SPP 占用，从 4 起）
        public static int ActiveChannel = 0;
        /// 最近一次成功连上的 channel（发送端用）
        public static int LastConnectedChannel = 0;

        /// 依次尝试的 channel 顺序：优先配置值，然后 4..30
        /// 蓝牙 channel 是设备内局部编号，本机在 X 监听不影响去连对端的 X，
        /// 所以这里不能跳过自己的 channel，否则会错过对端最常用的那个。
        public static int[] ChannelOrder(int preferred)
        {
            List<int> list = new List<int>();
            if (preferred >= 4 && preferred <= 30) list.Add(preferred);

            // 默认通道紧跟其后。
            // PreparePeer 只试 MaxChannels(=4) 个通道，把 20 排第 2 位，
            // 两端 ini 不一致（一边 4 一边 20）时也能在前两次尝试里命中；
            // 否则老配置只盲扫 4/5/6/7，够不着 20，直接报"联系不上对端"。
            const int DefaultChannel = 20;
            if (preferred != DefaultChannel) list.Add(DefaultChannel);

            for (int i = 4; i <= 30; i++)
                if (i != preferred && i != DefaultChannel) list.Add(i);
            return list.ToArray();
        }

        /// BtBeam 在这里提供自己的能力描述（由 TrayApp 注入）
        public static Func<string> CapabilityProvider;

        /// 让对端按需开热点，返回 "ssid,pass,ip"；失败返回 null
        public static Func<string> HotspotStarter;

        /// 反向托管时，对端连上我方热点后回报的地址
        public static string LastPeerIp = null;

        /// 由 TrayApp 注入：返回 true 表示用户已点"停止"。
        /// 所有可能阻塞的蓝牙操作在每个 channel 之间都要问一次，
        /// 否则点了停止还要等它把 30 个 channel 全扫完。
        public static Func<bool> ShouldCancel;

        /// 快速可达性探测：必须收到应用层回包才算通。
        /// 只判断 connect 是否成功是不可靠的 —— Windows 对没有监听者的
        /// RFCOMM channel 会先让 connect 成功、随后再 RST。
        public static bool Ping(ulong addr, int channel, int maxTries, int timeoutMs)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        /// 通用命令通道：发一行、收一行（HOSTING 等控制命令用）
        /// connectTimeoutMs / replyTimeoutMs 都尽量短，避免界面卡住
        public static string SendCommand(ulong addr, int channel, string command,
                                         int connectTimeoutMs, int replyTimeoutMs)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        /// 通过蓝牙问对端支持哪些通道，返回对端的 CAP 字符串（失败返回 null）
        /// 通过蓝牙请求对端开热点，返回 "READY ssid,pass,ip" 或 null（超时 40 秒，对方要开机热点）
        public static string PreparePeer(ulong addr, int channel, int timeoutMs)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        public static string QueryCapability(ulong addr, int channel)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        /// 从 CAP 字符串里取字段值
        public static string CapField(string cap, string key)
        {
            if (string.IsNullOrEmpty(cap)) return null;
            string pat = "\"" + key + "\":";
            int i = cap.IndexOf(pat);
            if (i < 0) return null;
            i += pat.Length;
            if (i >= cap.Length) return null;
            if (cap[i] == '"')
            {
                int j = cap.IndexOf('"', i + 1);
                if (j < 0) return null;
                return cap.Substring(i + 1, j - (i + 1));
            }
            else
            {
                int j = i;
                while (j < cap.Length && (char.IsDigit(cap[j]) || cap[j] == '-' || cap[j] == '.')) j++;
                return cap.Substring(i, j - i);
            }
        }

        public static void SendFile(ulong addr, int channel, string path, Action<long, long> progress)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        public static void Serve(int channel, string outDir, bool[] running,
                                 Action<string> log, Action<string> onFile, Action<long, long> progress)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }
    }

    // =====================================================================
    // 热点通道：系统移动热点（内部就是 Wi-Fi Direct GO）+ TCP
    // 与蓝牙通道共用 LBBT1 线协议
    // =====================================================================
    static class Hotspot
    {
        static Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager _mgr;

        static Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager Mgr()
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        public static bool IsOn
        {
            get { try { return Mgr().TetheringOperationalState.ToString() == "On"; } catch { return false; } }
        }

        public static int ClientCount
        {
            get { try { return (int)Mgr().ClientCount; } catch { return 0; } }
        }

        public static string Ssid
        {
            get { try { return Mgr().GetCurrentAccessPointConfiguration().Ssid; } catch { return ""; } }
        }

        public static string Pass
        {
            get { try { return Mgr().GetCurrentAccessPointConfiguration().Passphrase; } catch { return ""; } }
        }

        /// 热点网段的本地地址（Windows 默认 192.168.137.1）
        public static string HostIp()
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        /// 关键顺序：若已开启先关闭 -> 配置 SSID -> 启动
        /// （顺序错了会把 ICS 服务 ipnathlp.dll 打死）
        public static void Start(string ssid, string pass)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        public static void Stop()
        {
            try
            {
                Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager m = Mgr();
                if (m.TetheringOperationalState.ToString() == "On")
                    m.StopTetheringAsync().AsTask().Wait(20000);
            }
            catch { }
        }
    }

    // =====================================================================
    // WLAN 连接（用 netsh，避免 WinRT WiFiAdapter 在非 UWP 宿主的坑）
    // =====================================================================
    // =====================================================================
    // Wi-Fi 流量采样：判断本机是否"忙"，决定谁当 STA（谁断网）
    // =====================================================================
    static class Traffic
    {
        static long _lastRx, _lastTx;
        static DateTime _lastTime = DateTime.MinValue;

        public static double RateKBps()
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        /// 采样 600ms 判断是否繁忙
        public static bool IsBusy(double thresholdKBps)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }
    }

    static class Wifi
    {
        static string Run(string exe, string args, int timeoutMs)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        /// 目前能认出的热点网段地址（Windows 移动热点固定 192.168.137.x）
        /// 客户端在热点网段拿到的地址（排除本机作为 AP 的 192.168.137.1）
        public static string GetHotspotIp()
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        /// 已关联热点但地址还没分配时的宽松探测（返回任意 192.168.137.x）
        public static bool HasHotspotLink()
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        /// 当前关联的 Wi-Fi SSID（未连接返回 null）
        public static string CurrentSsid()
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        public static void Reconnect(string ssid)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        /// 用户级 WLAN 配置是否已存在
        static bool ProfileExists(string ssid)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        /// 当前接口是否已关联到该 SSID
        static bool AlreadyOn(string ssid)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        /// 连接热点。优化点：
        ///  1. profile 已存在就复用（省 ~1s）
        ///  2. 已经连上同一个 SSID 就直接返回（省 ~4s）
        ///  3. 只在必须切换时才 disconnect（省 1.5s 固定等待）
        ///  4. 100ms 轮询等地址，拿到立刻返回
        public static bool Connect(string ssid, string pass, int timeoutSec, Action<string> log)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }
    }

    // =====================================================================
    // 热点链路上的 TCP 传输（协议与蓝牙一致）
    // =====================================================================
    static class Hotlink
    {
        const string HS = "LBBT1 ";

        /// 实际绑定成功的 RFCOMM channel（1-3 被系统 SDP/SPP 占用，从 4 起）
        public static int ActiveChannel = 0;
        /// 最近一次成功连上的 channel（发送端用）
        public static int LastConnectedChannel = 0;

        /// 依次尝试的 channel 顺序：优先指定值，然后 4..30
        public static int[] ChannelOrder(int preferred)
        {
            List<int> list = new List<int>();
            if (preferred >= 4 && preferred <= 30) list.Add(preferred);
            for (int i = 4; i <= 30; i++)
                if (i != preferred) list.Add(i);
            return list.ToArray();
        }

        public static void Serve(int port, string outDir, bool[] running,
                                 Action<string> log, Action<string> onFile, Action<long, long> progress)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        static string ReadLine(System.IO.Stream s)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        static void ReceiveOne(System.Net.Sockets.TcpClient cli, string outDir,
                               Action<string> log, Action<string> onFile, Action<long, long> progress)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }

        public static void Send(string host, int port, string path, Action<long, long> progress)
        {
            // 核心实现未包含在开源部分。
            // 完整逻辑见发行版 BtBeam.exe；此处保留签名以说明架构与协议约定。
            throw new NotImplementedException("core implementation not included in the open-source part");
        }
    }

    // =====================================================================
    // 配置
    // =====================================================================
    class Cfg
    {
        public string OutDir = Sys.DownloadsDir();
        public int Channel = 20;
        public bool AutoStart = false;
        public bool ShowAll = false;
        public bool HotspotAlways = false;   // 启动即开热点，让对端随时能连
        public string LastMac = "";

        static string PathFile()
        {
            return Path.Combine(Sys.ExeDir(), "btbeam.ini");
        }

        public static Cfg Load()
        {
            Cfg c = new Cfg();
            try
            {
                if (!File.Exists(PathFile())) return c;
                foreach (string raw in File.ReadAllLines(PathFile()))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = raw.Substring(0, eq).Trim();
                    string v = raw.Substring(eq + 1).Trim();
                    if (k == "outdir" && v.Length > 0)
                    {
                        string full = v;
                        if (!Path.IsPathRooted(full)) full = Path.Combine(Sys.ExeDir(), full);
                        // 只接受真实存在的绝对路径，否则回落到系统下载目录
                        if (Directory.Exists(full)) c.OutDir = full;
                    }
                    else if (k == "channel") { int x; if (int.TryParse(v, out x) && x >= 1 && x <= 30) c.Channel = x; }
                    else if (k == "autostart") c.AutoStart = v == "1";
                    else if (k == "showall") c.ShowAll = v == "1";
                    else if (k == "hotspotalways") c.HotspotAlways = v == "1";
                    else if (k == "lastmac") c.LastMac = v;
                }
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(PathFile(),
                    "outdir=" + OutDir + "\r\n" +
                    "channel=" + Channel + "\r\n" +
                    "autostart=" + (AutoStart ? "1" : "0") + "\r\n" +
                    "showall=" + (ShowAll ? "1" : "0") + "\r\n" +
                    "hotspotalways=" + (HotspotAlways ? "1" : "0") + "\r\n" +
                    "lastmac=" + LastMac + "\r\n");
            }
            catch { }
        }

        public void ApplyAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (AutoStart)
                        k.SetValue("BtBeam", "\"" + Application.ExecutablePath + "\" --tray");
                    else
                        k.DeleteValue("BtBeam", false);
                }
            }
            catch { }
        }
    }

    // =====================================================================
    // 配色
    // =====================================================================
    // =====================================================================
    // DPI 工具
    //
    // 代码里所有坐标都是 96 DPI 的设计像素。缩放分两条路，各只做一次：
    //
    //   1. 构造期创建的静态控件：在构造函数「最后」设置
    //          AutoScaleDimensions = new SizeF(96F, 96F);
    //          AutoScaleMode = AutoScaleMode.Dpi;
    //      WinForms 会把窗体与所有子控件的边界按 当前DPI/96 缩放一次。
    //      注意顺序必须是「先 Dimensions 再 Mode」，且必须放在所有静态控件
    //      和 ClientSize 都设置完之后 —— 否则会在空窗体上把缩放用掉。
    //
    //   2. 运行期 new 出来的控件（设备卡片、状态行等）：WinForms 不会再自动
    //      缩放，必须用 ScaleOf(窗体) 手动换算边界。
    //
    // 字号永远不要乘缩放系数：GDI/GDI+ 已经按屏幕 DPI 渲染 pt 字号，
    // 再乘一次就是平方，会导致文字撑破控件（正是本次要修的问题）。
    // =====================================================================
    static class Ui
    {
        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

        /// 该控件当前所在显示器的缩放系数（192 DPI → 2.0）
        public static float ScaleOf(Control c)
        {
            try
            {
                if (c != null && c.IsHandleCreated)
                {
                    uint dpi = GetDpiForWindow(c.Handle);
                    if (dpi >= 48) return dpi / 96f;
                }
                ContainerControl cc = c as ContainerControl;
                if (cc != null && cc.AutoScaleDimensions.Width > 96.5f)
                    return cc.AutoScaleDimensions.Width / 96f;

                // 构造期尚无窗口句柄，用屏幕 DPI 兜底（关键：否则永远返回 1）
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float d = g.DpiX / 96f;
                    if (d >= 0.5f && d <= 4f) return d;
                }
            }
            catch { }
            return 1f;
        }

        /// 96 DPI 设计像素 → 当前 DPI 物理像素
        public static int Px(float scale, int design)
        {
            return (int)Math.Round(design * scale);
        }
    }
    static class Pal
    {
        public static readonly Color Bg = Color.FromArgb(248, 249, 251);
        public static readonly Color Card = Color.White;
        public static readonly Color Line = Color.FromArgb(228, 231, 236);
        public static readonly Color Text = Color.FromArgb(24, 28, 35);
        public static readonly Color Sub = Color.FromArgb(120, 128, 140);
        public static readonly Color Accent = Color.FromArgb(10, 132, 255);
        public static readonly Color AccentSoft = Color.FromArgb(232, 243, 255);
        public static readonly Color Hover = Color.FromArgb(242, 245, 249);
        public static readonly Color Ok = Color.FromArgb(48, 178, 108);
    }

    static class Draw
    {
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
        {
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, Rectangle r, int radius, Color c, float w)
        {
            using (GraphicsPath p = Round(r, radius))
            using (Pen pen = new Pen(c, w))
                g.DrawPath(pen, p);
        }
    }

    // =====================================================================
    // 设备条目控件
    // =====================================================================
    class DeviceItem : Control
    {
        public BtDevice Dev;
        public bool Selected;
        bool hot;

        public DeviceItem(BtDevice d, float scale)
        {
            Dev = d;
            // 运行期创建的控件不受 AutoScale 管辖，尺寸在这里显式换算。
            // 宽度比 body 少 20 设计像素：留出垂直滚动条的位置，
            // 否则设备多到出现滚动条时会再冒出一条横向滚动条。
            Width = Ui.Px(scale, 326);
            Height = Ui.Px(scale, 56);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Pal.Bg);
            if (Width < 20 || Height < 20) return;

            // 画布上的硬编码像素也按设计高度换算；DPI 变化后 Height 会被
            // WinForms 缩放，这里自动跟着变，不需要缓存系数。
            float k = Height / 56f;
            if (k <= 0f) k = 1f;

            Rectangle r = new Rectangle(0, Ui.Px(k, 2), Width - 1, Height - Ui.Px(k, 5));
            Color bg = Selected ? Pal.AccentSoft : (hot ? Pal.Hover : Pal.Card);
            Draw.FillRound(g, r, Ui.Px(k, 10), bg);
            Draw.StrokeRound(g, r, Ui.Px(k, 10), Selected ? Pal.Accent : Pal.Line, 1f * k);

            // 图标底座
            int boxSize = Ui.Px(k, 34);
            Rectangle box = new Rectangle(Ui.Px(k, 10), (Height - boxSize) / 2, boxSize, boxSize);
            Draw.FillRound(g, box, Ui.Px(k, 9), Selected ? Pal.Accent : Color.FromArgb(238, 242, 247));
            using (Font fi = new Font("Segoe MDL2 Assets", 13f))
            using (SolidBrush fb = new SolidBrush(Selected ? Color.White : Pal.Accent))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(Dev.Glyph, fi, fb, box, sf);
                sf.Dispose();
            }

            int tx = Ui.Px(k, 54);
            int tw = Width - tx - Ui.Px(k, 10);

            using (Font f1 = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular))
            using (SolidBrush b1 = new SolidBrush(Pal.Text))
            {
                StringFormat sf = new StringFormat();
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(Dev.Name, f1, b1,
                    new RectangleF(tx, Ui.Px(k, 9), tw, Ui.Px(k, 20)), sf);
                sf.Dispose();
            }

            string sub = Dev.KindName + "  ·  " + Wsa.FmtMac(Dev.Addr);
            using (Font f2 = new Font("Consolas", 8f))
            using (SolidBrush b2 = new SolidBrush(Pal.Sub))
            {
                StringFormat sf = new StringFormat();
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(sub, f2, b2,
                    new RectangleF(tx, Ui.Px(k, 30), tw, Ui.Px(k, 18)), sf);
                sf.Dispose();
            }

            if (Dev.Connected)
            {
                using (SolidBrush bd = new SolidBrush(Pal.Ok))
                    g.FillEllipse(bd, Width - Ui.Px(k, 18), Ui.Px(k, 12), Ui.Px(k, 6), Ui.Px(k, 6));
            }
        }
    }

    // =====================================================================
    // 收到文件的横幅提醒（自绘，不依赖系统通知中心）
    // =====================================================================
    class ToastForm : Form
    {
        static ToastForm current;

        System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        string title;
        string body;
        Action onClick;
        int phase = 0;      // 0=淡入 1=停留 2=淡出
        int counter = 0;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                return cp;
            }
        }

        public ToastForm(string title, string body, Action onClick)
        {
            this.title = title;
            this.body = body;
            this.onClick = onClick;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(330, 88);
            BackColor = Color.White;
            DoubleBuffered = true;
            Opacity = 0.0;
            Cursor = Cursors.Hand;

            Click += delegate { if (onClick != null) onClick(); CloseToast(); };
            foreach (Control c in Controls) c.Click += delegate { if (onClick != null) onClick(); CloseToast(); };

            tick.Interval = 40;
            tick.Tick += delegate
            {
                counter++;
                if (phase == 0)
                {
                    Opacity = Math.Min(1.0, Opacity + 0.22);
                    if (Opacity >= 1.0) { phase = 1; counter = 0; }
                }
                else if (phase == 1)
                {
                    if (counter >= 95) { phase = 2; counter = 0; }   // ~3.8s
                }
                else
                {
                    Opacity = Math.Max(0.0, Opacity - 0.12);
                    if (Opacity <= 0.01) CloseToast();
                }
            };

            // 静态布局全部就位后再缩放一次（顺序不能颠倒）
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            float k = Ui.ScaleOf(this);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - Ui.Px(k, 16),
                                 wa.Bottom - Height - Ui.Px(k, 18));
        }

        public void Popup()
        {
            if (current != null && current != this)
            {
                try { current.tick.Stop(); current.Close(); } catch { }
            }
            current = this;
            Show();
            tick.Start();
        }

        void CloseToast()
        {
            tick.Stop();
            if (current == this) current = null;
            try { Close(); } catch { }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 2 || Height <= 2) return;
            float k = Width / 330f;
            using (GraphicsPath p = Draw.Round(new Rectangle(0, 0, Width, Height), Ui.Px(k, 12)))
                Region = new Region(p);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);

            float k = Width / 330f;
            if (k <= 0f) k = 1f;

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Draw.StrokeRound(g, r, Ui.Px(k, 12), Pal.Line, 1f * k);

            int boxSize = Ui.Px(k, 40);
            Rectangle box = new Rectangle(Ui.Px(k, 14), (Height - boxSize) / 2, boxSize, boxSize);
            Draw.FillRound(g, box, Ui.Px(k, 11), Pal.AccentSoft);
            using (Font fi = new Font("Segoe MDL2 Assets", 16f))
            using (SolidBrush fb = new SolidBrush(Pal.Accent))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString("\uE896", fi, fb, box, sf);   // Download
                sf.Dispose();
            }

            using (Font f1 = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
            using (SolidBrush b1 = new SolidBrush(Pal.Text))
            using (Font f2 = new Font("Microsoft YaHei UI", 8.5f))
            using (SolidBrush b2 = new SolidBrush(Pal.Sub))
            {
                StringFormat sf = new StringFormat();
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                int tx = Ui.Px(k, 66);
                int tw = Width - Ui.Px(k, 82);
                g.DrawString(title, f1, b1, new RectangleF(tx, Ui.Px(k, 22), tw, Ui.Px(k, 20)), sf);
                g.DrawString(body, f2, b2, new RectangleF(tx, Ui.Px(k, 44), tw, Ui.Px(k, 20)), sf);
                sf.Dispose();
            }
        }
    }

    // =====================================================================
    // 托盘注册失败时的兜底入口：一个可拖动的悬浮球
    // =====================================================================
    class FloatingBall : Form
    {
        Action onClick;
        bool dragging;
        Point dragOff;
        bool moved;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ClassStyle |= 0x00020000;
                return cp;
            }
        }

        public FloatingBall(Action onClick)
        {
            this.onClick = onClick;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(52, 52);
            Opacity = 0.9;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;

            // 静态布局全部就位后再缩放一次（顺序不能颠倒）
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            float k = Ui.ScaleOf(this);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int margin = Ui.Px(k, 20);
            Location = new Point(wa.Right - Width - margin, wa.Bottom - Height - margin);

            MouseDown += delegate (object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                dragging = true;
                moved = false;
                dragOff = new Point(e.X, e.Y);
            };
            MouseMove += delegate (object s, MouseEventArgs e)
            {
                if (!dragging) return;
                Point p = PointToScreen(e.Location);
                Location = new Point(p.X - dragOff.X, p.Y - dragOff.Y);
                moved = true;
            };
            MouseUp += delegate (object s, MouseEventArgs e)
            {
                dragging = false;
                if (!moved && onClick != null) onClick();
            };
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 2 || Height <= 2) return;
            using (GraphicsPath p = Draw.Round(new Rectangle(0, 0, Width, Height), Math.Min(Width, Height) / 2))
                Region = new Region(p);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(10, 132, 255));

            float k = Width / 32f;
            using (Pen p = new Pen(Color.White, Math.Max(1.6f, 2.8f * k)))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLine(p, 16 * k, 6 * k, 16 * k, 26 * k);
                g.DrawLine(p, 16 * k, 6 * k, 23 * k, 12 * k);
                g.DrawLine(p, 23 * k, 12 * k, 9 * k, 21 * k);
                g.DrawLine(p, 16 * k, 26 * k, 23 * k, 20 * k);
                g.DrawLine(p, 23 * k, 20 * k, 9 * k, 11 * k);
            }
        }
    }

    // =====================================================================
    // 浮窗
    // =====================================================================
    class PopupForm : Form
    {
        TrayApp app;
        Panel header = new Panel();
        Panel body = new Panel();
        Panel dropZone = new Panel();
        Label lblTitle = new Label();
        Label lblClose = new Label();
        Label lblPin = new Label();
        Label lblDrop = new Label();
        Label lblStatus = new Label();
        ProgressBar prog = new ProgressBar();
        System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        bool dragging = false;
        bool pinned = false;
        Point dragOffset;

        public PopupForm(TrayApp owner)
        {
            app = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(360, 520);
            BackColor = Pal.Bg;
            DoubleBuffered = true;
            AllowDrop = true;
            KeyPreview = true;
            Font = new Font("Microsoft YaHei UI", 9f);

            lblTitle.Text = "BtBeam";
            lblTitle.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            lblTitle.ForeColor = Pal.Text;
            lblTitle.SetBounds(16, 13, 120, 24);
            lblTitle.BackColor = Color.Transparent;

            lblClose.Text = "\uE8BB";   // Segoe MDL2: Cancel
            lblClose.Font = new Font("Segoe MDL2 Assets", 10f);
            lblClose.ForeColor = Pal.Sub;
            lblClose.SetBounds(330, 14, 20, 20);
            lblClose.TextAlign = ContentAlignment.MiddleCenter;
            lblClose.Cursor = Cursors.Hand;
            lblClose.Click += delegate { Hide(); };

            lblPin.Text = "\uE840";     // Segoe MDL2: Pinned
            lblPin.Font = new Font("Segoe MDL2 Assets", 10f);
            lblPin.ForeColor = Color.FromArgb(190, 196, 206);
            lblPin.SetBounds(304, 14, 20, 20);
            lblPin.TextAlign = ContentAlignment.MiddleCenter;
            lblPin.Cursor = Cursors.Hand;
            lblPin.Click += delegate
            {
                pinned = !pinned;
                lblPin.ForeColor = pinned ? Pal.Accent : Color.FromArgb(190, 196, 206);
                lblPin.Text = pinned ? "\uE840" : "\uE718";   // Pinned / Unpin
            };
            ToolTip tipPin = new ToolTip();
            tipPin.SetToolTip(lblPin, "钉住（不再自动隐藏）");

            header.SetBounds(0, 0, 360, 46);
            header.BackColor = Pal.Bg;
            header.Controls.Add(lblTitle);
            header.Controls.Add(lblClose);
            header.Controls.Add(lblPin);
            header.MouseDown += HeaderDown;
            header.MouseMove += HeaderMove;
            header.MouseUp += delegate { dragging = false; };

            body.SetBounds(8, 46, 344, 380);
            body.BackColor = Pal.Bg;
            body.AutoScroll = true;

            dropZone.SetBounds(8, 428, 344, 58);
            dropZone.BackColor = Pal.Bg;
            dropZone.AllowDrop = true;
            dropZone.Paint += DropPaint;
            dropZone.DragEnter += DragEnterH;
            dropZone.DragLeave += delegate { dropZone.Invalidate(); };
            dropZone.DragDrop += DragDropH;
            dropZone.Cursor = Cursors.Hand;
            dropZone.Click += delegate { PickFiles(); };

            lblDrop = new Label();
            lblDrop.Text = "把文件拖到这里，或点击选择";
            lblDrop.SetBounds(0, 0, 344, 58);
            lblDrop.TextAlign = ContentAlignment.MiddleCenter;
            lblDrop.ForeColor = Pal.Sub;
            lblDrop.BackColor = Color.Transparent;
            lblDrop.Cursor = Cursors.Hand;
            lblDrop.Click += delegate { PickFiles(); };
            dropZone.Controls.Add(lblDrop);

            prog.SetBounds(8, 492, 344, 4);
            prog.Style = ProgressBarStyle.Continuous;
            prog.Value = 0;

            lblStatus.SetBounds(8, 498, 344, 18);
            lblStatus.Text = "就绪";
            lblStatus.ForeColor = Pal.Sub;
            lblStatus.Font = new Font("Microsoft YaHei UI", 8f);

            Controls.Add(header);
            Controls.Add(body);
            Controls.Add(dropZone);
            Controls.Add(prog);
            Controls.Add(lblStatus);

            DragEnter += DragEnterH;
            DragLeave += delegate { dropZone.Invalidate(); };
            DragDrop += DragDropH;
            // 不自动隐藏：点外部、切换窗口都保持显示，只有点 ✕ 或按 Esc 才收起
            KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) HidePopup(); };

            refreshTimer.Interval = 4000;
            refreshTimer.Tick += delegate { if (Visible) RebuildIfChanged(); };

            // 静态布局全部就位后再缩放一次（必须放在 ClientSize / 子控件之后，
            // 且先设置 Dimensions 再设置 Mode，否则缩放会在空窗体上被用掉）
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
                return cp;
            }
        }

        bool dpiApplied = false;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                float k = Ui.ScaleOf(this);
                Sys.Diag("popup: dpi=" + (int)Math.Round(k * 96)
                         + " scale=" + k.ToString("0.##")
                         + " size=" + Width + "x" + Height);

                // 句柄创建后才拿得到真实 DPI：这里把窗体按比例放大，
                // 再用新系数重建一次动态控件（设备卡片等）。
                if (k > 1.01f && !dpiApplied)
                {
                    dpiApplied = true;
                    ClientSize = new Size(Ui.Px(k, 360), Ui.Px(k, 520));
                    Sys.Diag("popup: resized to " + ClientSize.Width + "x" + ClientSize.Height);
                    Rebuild();
                }
            }
            catch (Exception ex) { Sys.Diag("popup: dpi apply failed " + ex.Message); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = Width / 360f;
            if (k <= 0f) k = 1f;
            using (GraphicsPath p = Draw.Round(new Rectangle(0, 0, Width - 1, Height - 1), Ui.Px(k, 14)))
            using (Pen pen = new Pen(Pal.Line, 1f * k))
                g.DrawPath(pen, p);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 2 || Height <= 2) return;
            float k = Width / 360f;
            using (GraphicsPath p = Draw.Round(new Rectangle(0, 0, Width, Height), Ui.Px(k, 14)))
                Region = new Region(p);
        }

        void HeaderDown(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragging = true;
            dragOffset = new Point(e.X, e.Y);
        }

        void HeaderMove(object s, MouseEventArgs e)
        {
            if (!dragging) return;
            Point p = header.PointToScreen(e.Location);
            Location = new Point(p.X - dragOffset.X, p.Y - dragOffset.Y);
        }

        void DropPaint(object s, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = dropZone.Width / 344f;
            if (k <= 0f) k = 1f;
            Rectangle r = new Rectangle(0, 0, dropZone.Width - 1, dropZone.Height - 1);
            bool active = dropZone.AllowDrop && app.DragHot;
            Draw.FillRound(g, r, Ui.Px(k, 12), active ? Pal.AccentSoft : Color.FromArgb(252, 252, 253));
            using (GraphicsPath p = Draw.Round(r, Ui.Px(k, 12)))
            using (Pen pen = new Pen(active ? Pal.Accent : Pal.Line, (active ? 2f : 1f) * k))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawPath(pen, p);
            }
        }

        void DragEnterH(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                app.DragHot = true;
                dropZone.Invalidate();
            }
            else e.Effect = DragDropEffects.None;
        }

        void DragDropH(object s, DragEventArgs e)
        {
            app.DragHot = false;
            dropZone.Invalidate();
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) app.SendFiles(files);
        }

        void PickFiles()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要发送的文件";
                dlg.Multiselect = true;
                if (dlg.ShowDialog(this) == DialogResult.OK) app.SendFiles(dlg.FileNames);
            }
        }

        public int uiThreadId = 0;

        string _lastUiSig = null;

        /// 设备列表 + 关键状态的指纹：没变就不用重建控件（避免每 4 秒闪一下）
        string UiSignature()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (BtDevice d in app.VisibleDevices())
                {
                    sb.Append(d.Addr).Append('|')
                      .Append(d.Name == null ? "" : d.Name).Append('|')
                      .Append(d.Connected ? '1' : '0').Append(';');
                }
                sb.Append("#sel=").Append(app.Selected == null ? "0" : Wsa.FmtMac(app.Selected.Addr));
                sb.Append("#sending=").Append(app.Sending ? '1' : '0');
                sb.Append("#cancel=").Append(app.CancelRequested ? '1' : '0');
                sb.Append("#hs=").Append(app.HotspotOn ? '1' : '0');
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// 定时刷新专用：内容没变化就什么也不做
        public void RebuildIfChanged()
        {
            string sig = UiSignature();
            if (sig == _lastUiSig) return;
            _lastUiSig = sig;
            Rebuild();
        }

        public void Rebuild()
        {
            if (uiThreadId == 0) uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            // 这个方法会被后台线程调用（热点启停、消息回调），必须封送到 UI 线程
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(Rebuild)); } catch { }
                return;
            }
            if (IsDisposed) return;

            // 句柄没建时 InvokeRequired 恒为 false，后台线程会直接操作控件集合，
            // 抛 ArgumentException 把进程干掉。这里一律封送到 UI 线程再建句柄。
            if (!IsHandleCreated)
            {
                if (System.Threading.Thread.CurrentThread.ManagedThreadId != uiThreadId)
                {
                    Sys.Diag("Rebuild: handle missing, deferring");
                    return;
                }
            }

            // 运行期 new 出来的控件不受 WinForms AutoScale 管辖：
            // 坐标按 96 DPI 设计值写，边界乘 k；字号保持 pt 不变。
            float k = Ui.ScaleOf(this);

            body.SuspendLayout();
            Net.SendMessage(body.Handle, 0x000B /* WM_SETREDRAW */, IntPtr.Zero, IntPtr.Zero);
            foreach (Control c in body.Controls) c.Dispose();
            body.Controls.Clear();

            List<BtDevice> list = app.VisibleDevices();
            int y = 0;   // 设计像素
            if (list.Count == 0)
            {
                Label empty = new Label();
                empty.Text = app.cfg.ShowAll
                    ? "没有已配对的设备\r\n点右下角 + 去配对"
                    : "没有找到电脑或手机\r\n点右下角 + 配对，或打开“显示全部设备”";
                empty.ForeColor = Pal.Sub;
                empty.SetBounds(Ui.Px(k, 8), Ui.Px(k, 24), Ui.Px(k, 318), Ui.Px(k, 60));
                empty.TextAlign = ContentAlignment.MiddleCenter;
                body.Controls.Add(empty);
                y = 96;
            }
            else
            {
                foreach (BtDevice d in list)
                {
                    DeviceItem it = new DeviceItem(d, k);
                    it.Left = 0;
                    it.Top = Ui.Px(k, y);
                    it.Selected = app.Selected != null && app.Selected.Addr == d.Addr;
                    BtDevice cap = d;
                    it.Click += delegate
                    {
                        app.Selected = cap;
                        app.cfg.LastMac = Wsa.FmtMac(cap.Addr);
                        app.cfg.Save();
                        Rebuild();
                    };
                    body.Controls.Add(it);
                    y += 60;
                }
            }

            // 底部：添加设备 + 设置
            Button add = FlatBtn("＋ 添加设备", Pal.Accent, Color.White);
            add.SetBounds(Ui.Px(k, 0), Ui.Px(k, y + 4), Ui.Px(k, 159), Ui.Px(k, 34));
            add.Click += delegate { Sys.OpenPairing(); };
            body.Controls.Add(add);

            Button settings = FlatBtn("⚙ 设置", Pal.Card, Pal.Text);
            settings.SetBounds(Ui.Px(k, 167), Ui.Px(k, y + 4), Ui.Px(k, 159), Ui.Px(k, 34));
            settings.Click += delegate { app.ShowSettings(this); };
            body.Controls.Add(settings);

            y += 44;

            // 传输中：显示醒目的"停止"按钮（随时可中断，避免断网后卡死）
            if (app.Sending)
            {
                Button stopBtn = FlatBtn("■ 停止传输", Color.FromArgb(214, 69, 65), Color.White);
                stopBtn.SetBounds(Ui.Px(k, 0), Ui.Px(k, y + 2), Ui.Px(k, 326), Ui.Px(k, 38));
                stopBtn.Click += delegate { app.CancelSend(); Rebuild(); };
                body.Controls.Add(stopBtn);
                y += 46;
            }

            // 通道状态（自动模式，无按钮）
            Label modeLine = new Label();
            modeLine.Text = "通道：自动协商（对端支持热点时优先走热点）";
            modeLine.ForeColor = Pal.Sub;
            modeLine.Font = new Font("Microsoft YaHei UI", 8f);
            modeLine.SetBounds(Ui.Px(k, 2), Ui.Px(k, y + 4), Ui.Px(k, 324), Ui.Px(k, 18));
            body.Controls.Add(modeLine);

            y += 22;

            // 来源标识：改不掉的那种 —— 倒手转卖的人最烦这一行
            Label srcLine = new Label();
            srcLine.Text = "mosink.top/workshop   ·   免费下载 · 禁止商用";
            srcLine.ForeColor = Pal.Sub;
            srcLine.Font = new Font("Consolas", 7.5f);
            srcLine.SetBounds(Ui.Px(k, 2), Ui.Px(k, y), Ui.Px(k, 324), Ui.Px(k, 15));
            srcLine.Cursor = Cursors.Hand;
            srcLine.Click += delegate { try { Sys.OpenPath("https://mosink.top/workshop/"); } catch { } };
            body.Controls.Add(srcLine);

            y += 20;

            if (app.HotspotOn)
            {
                Label hsLine = new Label();
                hsLine.Text = "热点待命  " + app.HotspotSsid + " / " + app.HotspotPass;
                hsLine.ForeColor = Pal.Ok;
                hsLine.Font = new Font("Consolas", 8f);
                hsLine.SetBounds(Ui.Px(k, 2), Ui.Px(k, y), Ui.Px(k, 324), Ui.Px(k, 16));
                body.Controls.Add(hsLine);

                Label hsIpLine = new Label();
                hsIpLine.Text = "本机热点地址 " + Hotspot.HostIp() + ":" + app.HotspotPort;
                hsIpLine.ForeColor = Pal.Sub;
                hsIpLine.Font = new Font("Consolas", 8f);
                hsIpLine.SetBounds(Ui.Px(k, 2), Ui.Px(k, y + 16), Ui.Px(k, 324), Ui.Px(k, 16));
                body.Controls.Add(hsIpLine);

                y += 34;
            }

            Label tip = new Label();
            tip.Text = "本机 " + app.LocalMac + "   ·   channel " + app.cfg.Channel;
            tip.ForeColor = Pal.Sub;
            tip.Font = new Font("Consolas", 8f);
            tip.SetBounds(Ui.Px(k, 2), Ui.Px(k, y + 2), Ui.Px(k, 324), Ui.Px(k, 18));
            body.Controls.Add(tip);

            Label tip2 = new Label();
            tip2.Text = "随时唤起：Ctrl+Alt+B  或  再双击一次 BtBeam.exe";
            tip2.ForeColor = Color.FromArgb(160, 168, 180);
            tip2.Font = new Font("Microsoft YaHei UI", 7.5f);
            tip2.SetBounds(Ui.Px(k, 2), Ui.Px(k, y + 22), Ui.Px(k, 324), Ui.Px(k, 16));
            body.Controls.Add(tip2);

            Label tip3 = new Label();
            tip3.Text = app.TrayStatusText;
            tip3.ForeColor = app.TrayOk ? Color.FromArgb(150, 160, 172) : Color.FromArgb(220, 120, 60);
            tip3.Font = new Font("Microsoft YaHei UI", 7.5f);
            tip3.SetBounds(Ui.Px(k, 2), Ui.Px(k, y + 38), Ui.Px(k, 324), Ui.Px(k, 16));
            body.Controls.Add(tip3);

            Net.SendMessage(body.Handle, 0x000B /* WM_SETREDRAW */, (IntPtr)1, IntPtr.Zero);
            body.ResumeLayout();
            body.Invalidate(true);
            body.Update();
            _lastUiSig = UiSignature();   // 手动重建后同步签名，避免定时器再补一次
            try { if (IsHandleCreated) lblStatus.Text = lastStatus; } catch { }
        }

        Button FlatBtn(string text, Color back, Color fore)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Pal.Line;
            b.FlatAppearance.BorderSize = 1;
            b.BackColor = back;
            b.ForeColor = fore;
            b.Font = new Font("Microsoft YaHei UI", 9f);
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            return b;
        }

        string lastStatus = "就绪";

        public void SetStatus(string s)
        {
            lastStatus = s;
            try
            {
                if (!IsHandleCreated) return;
                if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatus), s); return; }
                lblStatus.Text = s;
            }
            catch { }
        }

        public void SetProgress(long got, long total)
        {
            try
            {
                if (!IsHandleCreated) return;
                if (InvokeRequired) { BeginInvoke(new Action<long, long>(SetProgress), got, total); return; }
                int p = total <= 0 ? 0 : (int)(100 * got / total);
                prog.Value = p > 100 ? 100 : p;
            }
            catch { }
        }

        public void ResetProgress()
        {
            try
            {
                if (!IsHandleCreated) return;
                if (InvokeRequired) { BeginInvoke(new Action(ResetProgress)); return; }
                prog.Value = 0;
            }
            catch { }
        }

        public void ShowNearTray()
        {
            if (IsDisposed) return;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            ShowInTaskbar = false;
            Rebuild();
            float k = Ui.ScaleOf(this);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Point m = Cursor.Position;
            int gapX = Ui.Px(k, 24);
            int gapY = Ui.Px(k, 16);
            int edge = Ui.Px(k, 8);
            int x = m.X - Width + gapX;
            int y = m.Y - Height - gapY;
            if (x < wa.Left + edge) x = wa.Left + edge;
            if (y < wa.Top + edge) y = wa.Top + edge;
            if (x + Width > wa.Right - edge) x = wa.Right - Width - edge;
            if (y + Height > wa.Bottom - edge) y = wa.Bottom - Height - edge;
            Location = new Point(x, y);
            Show();
            Activate();
            refreshTimer.Start();
        }

        public void HidePopup()
        {
            refreshTimer.Stop();
            Hide();
        }
    }

    // =====================================================================
    // 托盘控制器
    // =====================================================================
    class TrayApp : ApplicationContext
    {
        public Cfg cfg;
        public BtDevice Selected;
        public string LocalMac = "未知";
        public bool DragHot = false;

        Icon trayIcon;
        bool trayAdded = false;
        int trayErr = 0;
        ContextMenuStrip menu;
        PopupForm popup;
        MessageHost msgWin;
        Thread listenThread;
        bool[] running = new bool[] { true };
        List<BtDevice> all = new List<BtDevice>();
        object sync = new object();
        public bool HotkeyOk = false;

        public TrayApp(bool startHidden)
        {
            cfg = Cfg.Load();
            cfg.ApplyAutoStart();

            try { LocalMac = Wsa.LocalMac(); } catch { }

            trayIcon = MakeIcon();
            menu = BuildMenu();

            popup = new PopupForm(this);
            // 让 Link 里的阻塞操作能感知"停止"
            Link.ShouldCancel = delegate { return CancelRequested; };
            // 在 UI 线程上就把浮窗句柄建好（不显示）。
            // 否则托盘模式下句柄为空 -> InvokeRequired 恒为 false ->
            // 后台线程直接操作控件集合 -> ArgumentException -> 进程终止。
            try
            {
                IntPtr popupHandle = popup.Handle;
                popup.uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                Sys.Diag("popup: handle pre-created hwnd=0x" + popupHandle.ToInt64().ToString("X")
                         + " uiThread=" + popup.uiThreadId);
            }
            catch (Exception pex) { Sys.Diag("popup handle pre-create failed: " + pex.Message); }

            // 独立的消息窗口：热键 + 跨进程唤醒 + 托盘鼠标事件，不依赖 WinForms 的 NotifyIcon
            msgWin = new MessageHost();
            IntPtr hostHandle = msgWin.Handle;   // 强制创建窗口句柄（不显示）
            msgWin.OnHotkey = delegate { TogglePopup(); };
            msgWin.OnShow = delegate { RefreshDevices(); popup.ShowNearTray(); };
            msgWin.OnTrayMouse = delegate (int ev)
            {
                if (ev == 0x0201) TogglePopup();                       // WM_LBUTTONDOWN
                else if (ev == 0x0205 || ev == 0x0204)                 // WM_RBUTTONUP / DOWN
                {
                    try { menu.Show(Cursor.Position); } catch { }
                }
            };
            HotkeyOk = Net.RegisterHotKey(msgWin.Handle, 1,
                Net.MOD_CONTROL | Net.MOD_ALT | Net.MOD_NOREPEAT, Net.VK_B);

            // explorer 重启后 Shell 会重建任务栏，旧图标句柄作废，必须重新注册
            msgWin.OnTaskbarCreated = delegate
            {
                trayAdded = false;
                AddTray();
            };

            // 告诉对端：我能走蓝牙，热点开没开，热点凭据是什么
            Link.CapabilityProvider = delegate
            {
                bool wfd = HotspotOn;
                return "CAP {\"bt\":1,\"ch\":" + Link.ActiveChannel + ",\"wfd\":" + (wfd ? "1" : "0")
                     + ",\"ssid\":\"" + HotspotSsid + "\""
                     + ",\"pass\":\"" + HotspotPass + "\""
                     + ",\"ip\":\"" + Hotspot.HostIp() + "\"}";
            };

            // 对端通过蓝牙遥控：按需开热点并回报凭据
            Link.HotspotStarter = delegate
            {
                try { return EnsureHotspot(); }
                catch (Exception ex)
                {
                    Sys.Diag("ensuring hotspot failed: " + ex.Message);
                    return null;
                }
            };

            RefreshDevices();
            StartListening();

            // 等消息泵跑起来再挂图标；再补一次强制刷新，防止 explorer 尚未就绪
            pendingShow = !startHidden;
            Application.Idle += OnFirstIdle;

            System.Windows.Forms.Timer restore = new System.Windows.Forms.Timer();
            restore.Interval = 1500;
            restore.Tick += delegate
            {
                restore.Stop();
                ReAddTray();
                restore.Dispose();
            };
            restore.Start();

            Sys.Diag("[start] exe=" + Sys.ExePath()
                     + " | IntegrityLevel=" + Sys.IntegrityLevel()
                     + " | hotkey=" + HotkeyOk
                     + " | startHidden=" + startHidden);
        }

        bool idleDone = false;
        bool pendingShow = false;

        void OnFirstIdle(object sender, EventArgs e)
        {
            if (idleDone) return;
            idleDone = true;
            Application.Idle -= OnFirstIdle;

            // 确保 popup 的窗口句柄已创建，否则后台线程无法 BeginInvoke 进来
            try { popup.CreateControl(); } catch { }

            AddTray();
            // RestoreBall();   // 用户只要托盘版，不启用悬浮球兜底
            if (pendingShow)
            {
                pendingShow = false;
                try { TogglePopup(); } catch { }
            }
        }

        FloatingBall ball;

        /// 托盘注册失败时，改用可拖动悬浮球当入口
        void RestoreBall()
        {
            if (trayAdded || ball != null) return;
            try
            {
                ball = new FloatingBall(delegate { TogglePopup(); });
                ball.Show();
                Sys.Diag("ball: 悬浮球已显示（托盘不可用）");
            }
            catch (Exception ex) { Sys.Diag("ball exception: " + ex.Message); }
        }

        void AddTray()
        {
            try
            {
                Tray.NOTIFYICONDATA d = Tray.Build(msgWin.Handle, 1, trayIcon.Handle, "BtBeam 蓝牙直传 · mosink.top");
                bool ok = Tray.Shell_NotifyIcon(Tray.NIM_ADD, ref d);
                int err = Marshal.GetLastWin32Error();
                trayAdded = ok;
                trayErr = ok ? 0 : err;
                Sys.Diag("NIM_ADD ok=" + ok + " cbSize=" + d.cbSize
                         + " hwnd=0x" + msgWin.Handle.ToInt64().ToString("X")
                         + " hicon=0x" + trayIcon.Handle.ToInt64().ToString("X")
                         + " err=" + err);
            }
            catch (Exception ex) { Sys.Diag("AddTray exception: " + ex.Message); }
        }

        void ReAddTray()
        {
            if (trayAdded) return;   // 已成功注册就别碰它：DELETE 之后再 ADD 一旦失败，图标就真的没了
            try
            {
                Tray.NOTIFYICONDATA d = Tray.Build(msgWin.Handle, 1, trayIcon.Handle, "BtBeam 蓝牙直传 · mosink.top");
                bool ok = Tray.Shell_NotifyIcon(Tray.NIM_ADD, ref d);
                int e2 = Marshal.GetLastWin32Error();
                trayAdded = ok;
                trayErr = ok ? 0 : e2;
                Sys.Diag("NIM_DELETE+ADD ok=" + ok + " err=" + e2);
                // RestoreBall();   // 用户只要托盘版，不启用悬浮球兜底
            }
            catch (Exception ex) { Sys.Diag("ReAddTray exception: " + ex.Message); }
        }

        public bool TrayOk { get { return trayAdded; } }

        public string TrayStatusText
        {
            get
            {
                if (trayAdded) return "托盘：已注册";
                return "托盘：注册失败" + (trayErr != 0 ? "（err=" + trayErr + "）" : "") + "，Ctrl+Alt+B 仍可用";
            }
        }

        public List<BtDevice> VisibleDevices()        {
            List<BtDevice> list = new List<BtDevice>();
            lock (sync)
            {
                foreach (BtDevice d in all)
                {
                    if (cfg.ShowAll) list.Add(d);
                    else if (d.IsComputer || d.IsPhone || d.MajorClass == 0) list.Add(d);
                }
            }
            if (string.IsNullOrEmpty(cfg.LastMac)) return list;
            list.Sort(delegate (BtDevice a, BtDevice b)
            {
                bool la = Wsa.FmtMac(a.Addr) == cfg.LastMac;
                bool lb = Wsa.FmtMac(b.Addr) == cfg.LastMac;
                if (la != lb) return la ? -1 : 1;
                return 0;
            });
            return list;
        }

        public void RefreshDevices()
        {
            try
            {
                List<BtDevice> l = BtEnum.List(cfg.ShowAll);
                lock (sync) { all = l; }
                if (Selected != null)
                {
                    BtDevice keep = null;
                    foreach (BtDevice d in l) if (d.Addr == Selected.Addr) { keep = d; break; }
                    Selected = keep;
                }
                if (Selected == null && !string.IsNullOrEmpty(cfg.LastMac))
                {
                    foreach (BtDevice d in l)
                        if (Wsa.FmtMac(d.Addr) == cfg.LastMac) { Selected = d; break; }
                }
                if (Selected == null && l.Count > 0) Selected = l[0];
            }
            catch { }
        }

        void TogglePopup()
        {
            if (popup.Visible) popup.HidePopup();
            else { RefreshDevices(); popup.ShowNearTray(); }
        }

        ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add("打开", null, delegate { RefreshDevices(); popup.ShowNearTray(); });
            m.Items.Add("刷新设备", null, delegate { RefreshDevices(); popup.SetStatus("已刷新"); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("接收目录…", null, delegate { PickOutDir(); });
            m.Items.Add("打开接收目录", null, delegate { Sys.OpenPath(cfg.OutDir); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("关于 BtBeam", null, delegate { ShowAbout(); });
            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem auto = new ToolStripMenuItem("开机自启");
            auto.Checked = cfg.AutoStart;
            auto.Click += delegate
            {
                cfg.AutoStart = !cfg.AutoStart;
                cfg.Save();
                cfg.ApplyAutoStart();
                auto.Checked = cfg.AutoStart;
            };
            m.Items.Add(auto);

            ToolStripMenuItem showAll = new ToolStripMenuItem("显示全部蓝牙设备");
            showAll.Checked = cfg.ShowAll;
            showAll.Click += delegate
            {
                cfg.ShowAll = !cfg.ShowAll;
                cfg.Save();
                showAll.Checked = cfg.ShowAll;
                RefreshDevices();
                popup.Rebuild();
            };
            m.Items.Add(showAll);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("托盘图标设置（找不到图标时用）", null, delegate { Sys.OpenTraySettings(); });
            m.Items.Add("退出", null, delegate { ExitApp(); });
            return m;
        }

        public void ShowSettings(Form owner)
        {
            ContextMenuStrip m = BuildMenu();
            m.Show(owner, new Point(owner.Width - 180, owner.Height - 60));
        }

        public void PickOutDir()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "选择接收目录";
                d.SelectedPath = cfg.OutDir;
                if (d.ShowDialog() == DialogResult.OK)
                {
                    cfg.OutDir = d.SelectedPath;
                    cfg.Save();
                    popup.SetStatus("接收目录：" + cfg.OutDir);
                    RestartListening();
                }
            }
        }

        void StartListening()
        {
            running[0] = true;
            int ch = cfg.Channel;
            listenThread = new Thread(delegate ()
            {
                while (running[0])
                {
                    try
                    {
                        Sys.Diag("bt: starting Serve ch=" + ch + " outDir=" + cfg.OutDir);
                        Link.Serve(ch, cfg.OutDir, running,
                            delegate (string s) { popup.SetStatus(s); Sys.Diag("bt: " + s); },
                            delegate (string path) { OnFileReceived(path); },
                            delegate (long a, long b) { popup.SetProgress(a, b); if (CancelRequested) throw new OperationCanceledException("user stop"); });
                        break;
                    }
                    catch (Exception ex)
                    {
                        Sys.Diag("bt: LISTEN FAILED ch=" + ch + " -> " + ex.GetType().Name + ": " + ex.Message);
                        popup.SetStatus("监听失败：" + ex.Message);
                        Thread.Sleep(3000);
                    }
                }
            });
            listenThread.IsBackground = true;
            listenThread.Start();

            // 启动时就检查热点：如果热点已经在跑（外部工具开的或上次遗留），
            // 立刻把热点接收通道（HotspotPort）挂起来，否则对端发文件会连接被拒。
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Thread.Sleep(1500);     // 等蓝牙 Serve 先就位
                    bool on = false;
                    try { on = Hotspot.IsOn; } catch { }
                    Sys.Diag("startup: hotspot on=" + on);

                    if (on && !HotspotOn)
                    {
                        HotspotOn = true;
                        try { HotspotSsid = Hotspot.Ssid; } catch { }
                    }

                    // 热点接收通道必须无条件监听：
                    // 即使本机不开热点（只是连着别人的热点），对方也可能
                    // 直接把文件发到本机的这个端口。
                    if (!hsStarted)
                    {
                        StartHotspotListener();
                        Sys.Diag("startup: hotspot listener started on :" + HotspotPort
                                 + " (hotspotOn=" + HotspotOn + ")");
                    }

                    // 开机就开热点：接收端常驻待命，对端发 PREPARE 时能秒回 READY，
                    // 不必再等它自己开热点（那一步在部分机器上要卡 60 秒）。
                    if (cfg.HotspotAlways && !on)
                    {
                        Sys.Diag("startup: hotspotalways=1, starting hotspot");
                        try { StartHotspot(); }
                        catch (Exception hex) { Sys.Diag("startup auto-hotspot failed: " + hex.Message); }
                    }
                }
                catch (Exception ex) { Sys.Diag("startup hotspot check failed: " + ex.Message); }
            });
        }

        /// 关于窗口：版本、来源、许可。倒手转卖的人改得掉标题，改不干净这里。
        void ShowAbout()
        {
            string ver = "1.0";
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
                System.Version v = asm.GetName().Version;
                if (v != null) ver = v.Major + "." + v.Minor + "." + v.Build;
            }
            catch { }

            string msg =
                "BtBeam  " + ver + "\n\n" +
                "两台 Windows 电脑直连传文件\n" +
                "不需要同一个网络，不需要账号，不经第三方服务器。\n\n" +
                "官网      https://mosink.top/workshop/\n" +
                "源码      https://github.com/MOS-ink/BtBeam\n" +
                "许可      PolyForm Noncommercial 1.0.0\n" +
                "          仅限个人非商业使用，禁止倒卖与商业集成\n\n" +
                "Copyright (c) 2026 MOS-ink (墨溟)";

            try
            {
                DialogResult r = MessageBox.Show(this.popup, msg, "关于 BtBeam",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (r == DialogResult.OK)
                {
                    try { Sys.OpenPath("https://mosink.top/workshop/"); } catch { }
                }
            }
            catch
            {
                MessageBox.Show(msg, "关于 BtBeam", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        void RestartListening()
        {
            // 换目录不需要重启线程，Serve 每轮都读 cfg.OutDir；
            // 这里只是刷新状态提示。
            popup.SetStatus("接收目录已切换到 " + cfg.OutDir);
        }

        /// 不依赖浮窗的独立提示（纯托盘模式收到文件时用）
        void ShowToastDirect(string path)
        {
            try
            {
                string name = Path.GetFileName(path);
                string sizeText = "";
                try
                {
                    long n = new FileInfo(path).Length;
                    sizeText = n >= 1024 * 1024
                        ? (n / 1024.0 / 1024.0).ToString("F1") + " MB"
                        : Math.Max(1, n / 1024).ToString() + " KB";
                }
                catch { }

                // ToastForm 自己创建句柄、自己定位，不继承 popup 的任何状态
                ToastForm t = new ToastForm("收到文件",
                    name + (sizeText.Length > 0 ? "   ·   " + sizeText : ""),
                    delegate { Sys.OpenPath(cfg.OutDir); });
                t.Popup();
                Sys.Diag("tray toast shown for " + name);
            }
            catch (Exception ex)
            {
                Sys.Diag("tray toast failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        void OnFileReceived(string path)
        {
            // 这个回调来自接收线程（蓝牙/热点的网络线程），必须封送到 UI 线程，
            // 否则创建 Toast 窗口会抛跨线程异常
            try
            {
                if (!popup.IsHandleCreated)
                {
                    // 浮窗从未显示过（纯托盘模式）：绝不能去碰 popup.Handle，
                    // 那会创建一个从未布局过的隐藏窗口，后续 Toast 定位全乱。
                    // 这里直接独立弹提示，完全不依赖 popup。
                    Sys.Diag("file received (tray-only mode): " + path);
                    ShowToastDirect(path);
                    return;
                }
                if (popup.InvokeRequired)
                {
                    popup.BeginInvoke(new Action<string>(OnFileReceived), path);
                    return;
                }
            }
            catch (Exception ex)
            {
                Sys.Diag("OnFileReceived marshal failed: " + ex.Message);
                return;
            }

            Sys.Diag("file received: " + path);

            popup.ResetProgress();
            string name = Path.GetFileName(path);
            string sizeText = "";
            try
            {
                long n = new FileInfo(path).Length;
                sizeText = n >= 1024 * 1024
                    ? (n / 1024.0 / 1024.0).ToString("F1") + " MB"
                    : Math.Max(1, n / 1024).ToString() + " KB";
            }
            catch { }

            popup.SetStatus("收到 " + name);

            try
            {
                ToastForm t = new ToastForm("收到文件",
                    name + (sizeText.Length > 0 ? "   ·   " + sizeText : ""),
                    delegate { Sys.OpenPath(cfg.OutDir); });
                t.Popup();
            }
            catch (Exception ex)
            {
                Sys.Diag("toast failed: " + ex.Message);
            }
        }

        public void SendFiles(string[] paths)
        {
            if (Selected == null)
            {
                popup.SetStatus("先选一台设备");
                return;
            }

            BtDevice target = Selected;
            int ch = cfg.Channel;
            string[] files = paths;

            if (Sending)
            {
                popup.SetStatus("正在发送中，先点「停止」再发新的");
                return;
            }

            CancelRequested = false;
            Sending = true;
            try { popup.Rebuild(); } catch { }   // 立刻显示"停止"按钮

            ThreadPool.QueueUserWorkItem(delegate
            {
                string phase = "准备";
                bool anyOk = false;
                string readyFromHosting = null;
                string restoreSsid = null;      // 断网后要恢复的原 Wi-Fi

                try
                {
                // ---- 第零步：比较两边流量，让"更忙"的一方保持在线 ----
                // 规则：流量大的一方开热点（不断网），流量小的一方当 STA 去连（断网）
                popup.SetStatus("正在检测网络状态 ...");
                double myRate = 0;
                try { myRate = Traffic.RateKBps(); Thread.Sleep(400); myRate = Traffic.RateKBps(); } catch { }

                double peerRate = 0;
                bool peerRateKnown = false;
                popup.SetStatus("正在联系对端 ...");

                // 只发一次命令：拿到 RATE 说明对端在线且支持协作；
                // 连不上就直接判失败，不再做第二次探测（那样要再等一遍链路建立）。
                string tr = null;
                try { tr = Link.SendCommand(target.Addr, ch, "TRAFFIC", 1500, 2000); }
                catch { }

                if (tr == null)
                {
                    if (CancelRequested)
                    {
                        Sys.Diag("send stopped by user during TRAFFIC probe");
                        popup.SetStatus("已停止");
                        return;
                    }
                    Sys.Diag("peer unreachable (no reply to TRAFFIC)");
                    popup.SetStatus("对端未响应（BtBeam 没启动？），发送取消");
                    return;
                }

                if (tr.StartsWith("RATE "))
                {
                    double v = 0;
                    if (double.TryParse(tr.Substring(5).Trim(), out v)) { peerRate = v; peerRateKnown = true; }
                    Sys.Diag("traffic reply ok: peer=" + ((int)peerRate) + " KB/s");
                }
                else
                {
                    // 有回包但内容不是 RATE：说明对端是老版本，仍然算"在线"
                    Sys.Diag("peer alive but unexpected reply: " + tr);
                }

                Sys.Diag("rate: mine=" + ((int)myRate) + " peer=" + (peerRateKnown ? ((int)peerRate).ToString() : "?") + " KB/s");

                // ---- 硬约束：已经在当热点主机（有客户端连着）就不能反向当 STA ----
                // 否则一旦去连对方热点，本机热点会被 Wi-Fi 驱动关掉，
                // 连着的对方直接掉线，双方彻底失联 —— 这表现为"未响应"。
                bool iAmHotspotHost = false;
                int hsClients = 0;
                try
                {
                    hsClients = Hotspot.ClientCount;
                    iAmHotspotHost = HotspotOn && hsClients > 0;
                }
                catch { }

                // 关键：如果我此刻正作为 STA 连着对方的热点，就绝不能再开自己的热点。
                // 否则本机适配器会同时有 192.168.137.1（我的 AP）和 192.168.137.x（对方 STA），
                // 往 192.168.137.1 发包会走本地路由 —— 文件直接发给了自己。
                bool iAmStaOnPeer = false;
                string curSsidForRole = null;
                try
                {
                    curSsidForRole = Wifi.CurrentSsid();
                    if (!string.IsNullOrEmpty(curSsidForRole))
                    {
                        string hostSsid = "";
                        try { hostSsid = Hotspot.Ssid; } catch { }
                        // 已经连着一个 192.168.137.x 网段的热点，说明我处于 STA 角色
                        string myWifiIp = Wifi.GetHotspotIp();
                        if (!string.IsNullOrEmpty(myWifiIp)
                            && myWifiIp.StartsWith("192.168.137.")
                            && myWifiIp != "192.168.137.1")
                        {
                            iAmStaOnPeer = true;
                        }
                    }
                }
                catch { }

                Sys.Diag("role: hotspotOn=" + HotspotOn + " clients=" + hsClients
                         + " iAmHost=" + iAmHotspotHost
                         + " ssid=" + (curSsidForRole == null ? "(none)" : curSsidForRole)
                         + " iAmSta=" + iAmStaOnPeer);

                // 我比对方忙（或对方数值拿不到但本机明显忙）-> 我来开热点。
                // 但如果我已经是对方热点的 STA，就必须保持 STA（否则会自己发给自己）。
                bool iShouldHost;
                if (iAmStaOnPeer)
                {
                    iShouldHost = false;
                    Sys.Diag("role: forced STA (I am already a client on peer hotspot)");
                }
                else
                {
                    iShouldHost = iAmHotspotHost
                                  || (peerRateKnown ? (myRate > peerRate) : (myRate > 200));
                }

                if (iAmHotspotHost)
                    Sys.Diag("role: forced HOST (already serving hotspot clients, must not become STA)");

                if (iShouldHost)
                {
                    try
                    {
                        phase = iAmHotspotHost ? "作为热点主机发送" : "反向托管";
                        popup.SetStatus("我这边更忙（" + ((int)myRate) + " vs " + (peerRateKnown ? ((int)peerRate).ToString() : "?")
                                        + " KB/s），由我开热点 ...");
                        string hostInfo = EnsureHotspot();          // ssid,pass,ip
                        Sys.Diag("reverse host: " + (hostInfo == null ? "(refused)" : hostInfo));

                        if (string.IsNullOrEmpty(hostInfo))
                        {
                            // 我连着对方热点，不能再开自己的（网段冲突）—— 直接用对方热点发
                            Sys.Diag("cannot host (STA on peer hotspot), sending as client instead");
                            popup.SetStatus("使用对端热点直接发送 ...");
                        }
                        else
                        {

                        string hostReply = Link.SendCommand(target.Addr, ch, "HOSTING " + hostInfo, 6000, 45000);
                        Sys.Diag("HOSTING -> " + (hostReply == null ? "(null)" : hostReply));

                        // 对方说"你来连我"：直接复用这个 READY，省一次蓝牙往返
                        if (!string.IsNullOrEmpty(hostReply) && hostReply.StartsWith("READY "))
                        {
                            Sys.Diag("HOSTING -> peer offered its hotspot, reuse as READY");
                            readyFromHosting = hostReply;
                        }

                        if (!string.IsNullOrEmpty(hostReply) && hostReply.StartsWith("MY_IP "))
                        {
                            string peerIp = hostReply.Substring(6).Trim();
                            if (peerIp.Length > 0 && peerIp != "FAIL")
                            {
                                phase = "反向发送";
                                Sys.Diag("reverse send -> " + peerIp + ":" + HotspotPort);
                                foreach (string pf in files)
                                {
                                    CheckCancel();
                                    if (!File.Exists(pf)) continue;
                                    popup.SetStatus("反向发送 " + Path.GetFileName(pf) + " → " + peerIp);
                                    Hotlink.Send(peerIp, HotspotPort, pf,
                                        delegate (long a, long b) { popup.SetProgress(a, b); if (CancelRequested) throw new OperationCanceledException("user stop"); });
                                    anyOk = true;
                                }
                                popup.SetStatus("发送完成（反向托管，" + files.Length + " 个文件）");
                                return;   // 成功，不再走常规路径
                            }
                        }
                        popup.SetStatus("反向托管未成功，改走常规流程 ...");
                        }
                    }
                    catch (Exception ex)
                    {
                        Sys.Diag("reverse failed: " + ex.Message);
                        popup.SetStatus("反向托管异常，改走常规流程 ...");
                    }
                }
                else
                {
                    Sys.Diag("peer is busier or equal, let peer host");
                }

                // ---- 第一步：遥控对端开热点 ----
                string ready = readyFromHosting;      // HOSTING 里已经拿到就不必再问
                if (string.IsNullOrEmpty(ready))
                {
                    popup.SetStatus("正在请求 " + target.Name + " 准备高速通道 ...");
                    try { ready = Link.PreparePeer(target.Addr, ch, 60000); } catch { }
                }
                else
                {
                    Sys.Diag("skip PREPARE, use READY from HOSTING round");
                }
                Sys.Diag("PREPARE -> " + (ready == null ? "(null)" : ready));

                if (!string.IsNullOrEmpty(ready) && ready.StartsWith("READY "))
                {
                    string info = ready.Substring(6).Trim();
                    string[] parts = info.Split(',');
                    if (parts.Length >= 3)
                    {
                        string ssid = parts[0];
                        string pass = parts[1];
                        string host = parts[2];

                        string oldSsid = Wifi.CurrentSsid();
                        Sys.Diag("peer hotspot " + ssid + " host=" + host + " oldWifi=" + (oldSsid == null ? "(none)" : oldSsid));

                        popup.SetStatus("对端热点就绪，正在接入 " + ssid + " ...");
                        bool joined = false;
                        try
                        {
                            joined = Wifi.Connect(ssid, pass, 45,
                                delegate (string s) { popup.SetStatus(s); });
                        }
                        catch (Exception ex) { Sys.Diag("join failed: " + ex.Message); }

                        if (joined)
                        {
                            foreach (string pf in files)
                            {
                                if (!File.Exists(pf)) continue;
                                try
                                {
                                    popup.SetStatus("高速发送 " + Path.GetFileName(pf));
                                    Hotlink.Send(host, HotspotPort, pf,
                                        delegate (long a, long b) { popup.SetProgress(a, b); if (CancelRequested) throw new OperationCanceledException("user stop"); });
                                    popup.SetStatus("已发送 " + Path.GetFileName(pf) + "（热点）");
                                }
                                catch (Exception ex) { popup.SetStatus("发送失败：" + ex.Message); }
                            }

                            // 传完切回原来的 Wi-Fi（具体恢复动作交给 finally 兜底）
                            if (!string.IsNullOrEmpty(oldSsid))
                            {
                                restoreSsid = oldSsid;
                                popup.SetStatus("已发送，稍后恢复 " + oldSsid);
                            }

                            popup.ResetProgress();
                            return;
                        }
                        popup.SetStatus("接入热点失败，回退蓝牙");
                    }
                }
                else if (!string.IsNullOrEmpty(ready) && ready.StartsWith("ERR"))
                {
                    popup.SetStatus("对端无法开热点，使用蓝牙");
                }
                else
                {
                    popup.SetStatus("对端未响应，使用蓝牙");
                }

                // ---- 回退：蓝牙 ----
                foreach (string pf in files)
                {
                    if (!File.Exists(pf)) continue;
                    try
                    {
                        popup.SetStatus("蓝牙发送 " + Path.GetFileName(pf) + " → " + target.Name);
                        Link.SendFile(target.Addr, ch, pf,
                            delegate (long a, long b) { popup.SetProgress(a, b); if (CancelRequested) throw new OperationCanceledException("user stop"); });
                        popup.SetStatus("已发送 " + Path.GetFileName(pf) + "（蓝牙）");
                    }
                    catch (Exception ex) { popup.SetStatus("蓝牙发送失败：" + ex.Message); }
                }
                    popup.ResetProgress();
                }
                catch (OperationCanceledException)
                {
                    Sys.Diag("send: CANCELLED by user at [" + phase + "]");
                    popup.SetStatus("传输已停止");
                }
                catch (Exception ex)
                {
                    Sys.Diag("send FAILED at [" + phase + "]: " + ex.GetType().Name + ": " + ex.Message);
                    popup.SetStatus("发送失败（" + phase + "）：" + ex.Message);
                }
                finally
                {
                    Sending = false;
                    CancelRequested = false;

                    // 关键兜底：只要这次传输动过 Wi-Fi，无论成功失败都恢复原网络
                    if (!string.IsNullOrEmpty(restoreSsid))
                    {
                        try
                        {
                            string cur = Wifi.CurrentSsid();
                            if (!string.Equals(cur, restoreSsid, StringComparison.OrdinalIgnoreCase))
                            {
                                Sys.Diag("wifi: fallback restore " + restoreSsid + " (currently " + (cur == null ? "none" : cur) + ")");
                                Wifi.Reconnect(restoreSsid);
                                popup.SetStatus("已恢复 Wi-Fi " + restoreSsid);
                            }
                        }
                        catch (Exception ex2) { Sys.Diag("wifi: restore failed " + ex2.Message); }
                    }
                    try { popup.ResetProgress(); } catch { }
                    try { popup.Rebuild(); } catch { }        // 隐藏"停止"按钮
                }
            });
        }

        // ---------------- 热点通道 ----------------
        public volatile bool HotspotOn = false;

        /// 用户点了"停止"，传输线程看到就中断
        public volatile bool CancelRequested = false;

        /// 当前是否有传输在进行（用于 UI 显示停止按钮 / 防重复触发）
        public volatile bool Sending = false;

        public void CancelSend()
        {
            CancelRequested = true;
            Sys.Diag("send: cancel requested by user");
            try { popup.SetStatus("正在停止传输 ..."); } catch { }
        }

        void CheckCancel()
        {
            if (CancelRequested) throw new OperationCanceledException("用户已停止传输");
        }
        public string HotspotSsid = "PeerLink";
        public string HotspotPass = "peerlink2026";
        public int HotspotPort = 53321;

        bool[] hsRunning = new bool[] { false };
        Thread hsThread;

        public void ToggleHotspot()
        {
            if (HotspotOn) StopHotspot();
            else StartHotspot();
        }

        /// 只启动 TCP 监听（热点已经开好时调用）
        bool hsStarted = false;

        void StartHotspotListener()
        {
            if (hsStarted)
            {
                Sys.Diag("hotspot listener already running, skip");
                return;
            }
            hsStarted = true;
            hsRunning[0] = true;
            hsThread = new Thread(delegate ()
            {
                try
                {
                    Hotlink.Serve(HotspotPort, cfg.OutDir, hsRunning,
                        delegate (string s) { popup.SetStatus(s); },
                        delegate (string p) { OnFileReceived(p); },
                        delegate (long a, long b) { popup.SetProgress(a, b); if (CancelRequested) throw new OperationCanceledException("user stop"); });
                }
                catch (Exception ex) { popup.SetStatus("热点通道异常: " + ex.Message); }
            });
            hsThread.IsBackground = true;
            hsThread.Start();
        }

        /// 确保热点已开并返回 "ssid,pass,ip"（供对端遥控调用，会阻塞 3-5 秒）
        /// 本机是否正作为客户端连着别人的热点（此时绝不能开自己的热点）
        public bool IsStaOnPeerHotspot()
        {
            try
            {
                string ip = Wifi.GetHotspotIp();
                // 192.168.137.x 且不是 .1 => 我是客户端
                return !string.IsNullOrEmpty(ip)
                       && ip.StartsWith("192.168.137.")
                       && ip != "192.168.137.1";
            }
            catch { return false; }
        }

        public string EnsureHotspot()
        {
            // 致命冲突守卫：如果我此刻是别人热点的客户端（192.168.137.x 非 .1），
            // 就绝不能开自己的热点 —— 两者网段完全相同（都是 192.168.137.1/24），
            // 开了之后本机路由会把 192.168.137.1 指向自己的 AP，再也到不了对方。
            if (IsStaOnPeerHotspot())
            {
                string curIp = "";
                try { curIp = Wifi.GetHotspotIp(); } catch { }
                Sys.Diag("EnsureHotspot: refused, I am STA on peer hotspot (ip=" + curIp + ")");
                popup.SetStatus("已连着对端热点，不能重复开热点");
                return null;
            }

            // 先问系统：热点是不是已经开着（可能是别的程序开的，也可能是上次遗留的）
            bool alreadyOn = false;
            try { alreadyOn = Hotspot.IsOn; } catch { }

            if (alreadyOn)
            {
                // 已经在跑：绝不能再 Start 一次，否则热点会重启、对端全部掉线
                string curSsid = "";
                try { curSsid = Hotspot.Ssid; } catch { }
                if (!HotspotOn)
                {
                    HotspotOn = true;
                    StartHotspotListener();
                }
                Sys.Diag("hotspot already on (ssid=" + curSsid + "), skip restart");
                if (!string.IsNullOrEmpty(curSsid)) HotspotSsid = curSsid;
                return HotspotSsid + "," + HotspotPass + "," + Hotspot.HostIp();
            }

            if (!HotspotOn)
            {
                Hotspot.Start(HotspotSsid, HotspotPass);
                HotspotOn = true;
                StartHotspotListener();
                Sys.Diag("hotspot auto-started by peer request");
            }
            return HotspotSsid + "," + HotspotPass + "," + Hotspot.HostIp();
        }

        public void StartHotspot()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    popup.SetStatus("正在启动热点 ...");
                    Hotspot.Start(HotspotSsid, HotspotPass);

                    hsRunning[0] = true;
                    hsThread = new Thread(delegate ()
                    {
                        try
                        {
                            Hotlink.Serve(HotspotPort, cfg.OutDir, hsRunning,
                                delegate (string s) { popup.SetStatus(s); },
                                delegate (string p) { OnFileReceived(p); },
                                delegate (long a, long b) { popup.SetProgress(a, b); if (CancelRequested) throw new OperationCanceledException("user stop"); });
                        }
                        catch (Exception ex) { popup.SetStatus("热点通道异常: " + ex.Message); }
                    });
                    hsThread.IsBackground = true;
                    hsThread.Start();

                    HotspotOn = true;
                    Sys.Diag("hotspot started: " + HotspotSsid + " ip=" + Hotspot.HostIp());
                    popup.SetStatus("热点已开 " + HotspotSsid + " / " + HotspotPass + "  @" + Hotspot.HostIp());
                    popup.Rebuild();
                }
                catch (Exception ex)
                {
                    HotspotOn = false;
                    Sys.Diag("hotspot start failed: " + ex.Message);
                    popup.SetStatus("热点启动失败: " + ex.Message);
                    popup.Rebuild();
                }
            });
        }

        public void StopHotspot()
        {
            hsRunning[0] = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Hotspot.Stop(); } catch { }
                HotspotOn = false;
                Sys.Diag("hotspot stopped");
                popup.SetStatus("热点已关闭");
                popup.Rebuild();
            });
        }

        public void SendViaHotspot(string path)
        {
            string host = Hotspot.HostIp();
            int port = HotspotPort;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    popup.SetStatus("热点通道发送 " + Path.GetFileName(path) + " -> " + host);
                    Hotlink.Send(host, port, path, delegate (long a, long b) { popup.SetProgress(a, b); if (CancelRequested) throw new OperationCanceledException("user stop"); });
                    popup.SetStatus("已发送 " + Path.GetFileName(path) + "（热点通道）");
                }
                catch (Exception ex)
                {
                    popup.SetStatus("热点发送失败: " + ex.Message);
                }
                finally { popup.ResetProgress(); }
            });
        }

        // 作为热点客户端：连上对方热点再发送
        public string PeerSsid = "PeerLink";
        public string PeerPass = "peerlink2026";

        public void JoinPeerAndSend(string path)
        {
            string ssid = PeerSsid;
            string pass = PeerPass;
            int port = HotspotPort;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    popup.SetStatus("正在连接对方热点 " + ssid + " ...");
                    bool ok = Wifi.Connect(ssid, pass, 45, delegate (string s) { popup.SetStatus(s); });
                    if (!ok)
                    {
                        popup.SetStatus("连接热点超时 —— 对方是否已开热点？密码是否正确？");
                        Sys.Diag("join failed: " + ssid);
                        return;
                    }

                    string myIp = Wifi.GetHotspotIp();
                    string host = "192.168.137.1";
                    Sys.Diag("joined " + ssid + " myip=" + myIp + " host=" + host);

                    popup.SetStatus("已连上热点 " + myIp + "，发送到 " + host);
                    Hotlink.Send(host, port, path, delegate (long a, long b) { popup.SetProgress(a, b); if (CancelRequested) throw new OperationCanceledException("user stop"); });
                    popup.SetStatus("已发送 " + Path.GetFileName(path) + "（热点客户端模式）");
                    Sys.Diag("sent via hotspot: " + Path.GetFileName(path));
                }
                catch (Exception ex)
                {
                    Sys.Diag("join/send failed: " + ex.Message);
                    popup.SetStatus("热点发送失败: " + ex.Message);
                }
                finally { popup.ResetProgress(); }
            });
        }

        void ExitApp()
        {
            running[0] = false;
            hsRunning[0] = false;
            try { Hotspot.Stop(); } catch { }
            try
            {
                Tray.NOTIFYICONDATA d = Tray.Build(msgWin.Handle, 1, trayIcon.Handle, "");
                Tray.Shell_NotifyIcon(Tray.NIM_DELETE, ref d);
            }
            catch { }
            try { Net.UnregisterHotKey(msgWin.Handle, 1); } catch { }
            try { msgWin.Dispose(); } catch { }
            ExitThread();
        }

        public static Icon MakeIcon()
        {
            try
            {
                int[] sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
                List<byte[]> imgs = new List<byte[]>();
                foreach (int s in sizes)
                {
                    using (Bitmap bmp = DrawGlyph(s))
                        imgs.Add(IcoImage(bmp, s));
                }

                int dirSize = 6 + 16 * imgs.Count;
                int total = dirSize;
                for (int i = 0; i < imgs.Count; i++) total += imgs[i].Length;

                byte[] d = new byte[total];
                d[2] = 1;
                d[4] = (byte)imgs.Count;

                int dataOff = dirSize;
                for (int i = 0; i < imgs.Count; i++)
                {
                    int e = 6 + 16 * i;
                    int s = sizes[i];
                    d[e + 0] = (byte)(s >= 256 ? 0 : s);
                    d[e + 1] = (byte)(s >= 256 ? 0 : s);
                    d[e + 2] = 0;
                    d[e + 3] = 0;
                    d[e + 4] = 1;
                    d[e + 5] = 0;
                    d[e + 6] = 32;
                    d[e + 7] = 0;
                    BitConverter.GetBytes(imgs[i].Length).CopyTo(d, e + 8);
                    BitConverter.GetBytes(dataOff).CopyTo(d, e + 12);
                    imgs[i].CopyTo(d, dataOff);
                    dataOff += imgs[i].Length;
                }
                return new Icon(new MemoryStream(d));
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        /// 圆角矩形路径（1000 基准坐标）
        static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            GraphicsPath gp = new GraphicsPath();
            float d = r * 2f;
            if (d > w) d = w;
            if (d > h) d = h;
            gp.AddArc(x, y, d, d, 180, 90);
            gp.AddArc(x + w - d, y, d, d, 270, 90);
            gp.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            gp.AddArc(x, y + h - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        /// 图标：墨底圆角方块 + 纸白设备 + 朱砂一抹束光
        /// 小尺寸（<=26）自动切极简版，只留「两点 + 一道弧」，否则会糊成一团
        static Bitmap DrawGlyph(int size)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float k = size / 1024f;              // 统一按 1024 设计
                Color ink   = Color.FromArgb(23, 23, 23);
                Color paper = Color.FromArgb(245, 242, 235);
                Color seal  = Color.FromArgb(170, 52, 41);

                // ---- 墨底圆角方块 ----
                using (GraphicsPath gp = RoundRect(0, 0, size, size, 230f * k))
                using (SolidBrush bg = new SolidBrush(ink))
                    g.FillPath(bg, gp);

                float cx = size / 2f, cy = size / 2f;
                bool simple = size <= 26;

                if (simple)
                {
                    // ===== 极简：一道纸白弧 + 两个朱砂点 =====
                    float ax = 266f * k, bx = 758f * k;
                    float byy = cy + 100f * k;
                    float wArc = Math.Max(1.7f, 170f * k);
                    using (Pen p = new Pen(paper, wArc))
                    {
                        p.StartCap = LineCap.Round;
                        p.EndCap = LineCap.Round;
                        g.DrawBezier(p,
                            ax, byy,
                            cx, cy - 250f * k,
                            cx, cy - 250f * k,
                            bx, byy);
                    }
                    float rr = 108f * k;
                    using (SolidBrush sb = new SolidBrush(seal))
                    {
                        g.FillEllipse(sb, ax - rr, byy - rr, rr * 2, rr * 2);
                        g.FillEllipse(sb, bx - rr, byy - rr, rr * 2, rr * 2);
                    }
                }
                else
                {
                    // ===== 完整：两台设备 + 朱砂拱弧 + 弧顶文件点 =====
                    float devW = 190f * k, devH = 370f * k;
                    float topY = cy - devH / 2f;
                    float lx = 138f * k, rx = 696f * k;

                    using (SolidBrush pb = new SolidBrush(paper))
                    {
                        g.FillPath(pb, RoundRect(lx, topY, devW, devH, 32f * k));
                        g.FillPath(pb, RoundRect(rx, topY, devW, devH, 32f * k));
                    }

                    // 设备上的两道刻线（半透明墨）
                    using (SolidBrush lb = new SolidBrush(Color.FromArgb(72, 23, 23, 23)))
                    {
                        for (int side = 0; side < 2; side++)
                        {
                            float x0 = (side == 0) ? lx : rx;
                            float l1 = devW - 64f * k;
                            g.FillPath(lb, RoundRect(x0 + 32f * k, topY + devH * 0.26f,
                                                     l1, Math.Max(1f, 22f * k), 11f * k));
                            g.FillPath(lb, RoundRect(x0 + 32f * k, topY + devH * 0.42f,
                                                     l1 * 0.66f, Math.Max(1f, 22f * k), 11f * k));
                        }
                    }

                    // 朱砂拱弧
                    float ax = lx + devW + 12f * k;
                    float bx = rx - 12f * k;
                    using (Pen p = new Pen(seal, Math.Max(2.6f, 68f * k)))
                    {
                        p.StartCap = LineCap.Round;
                        p.EndCap = LineCap.Round;
                        g.DrawBezier(p,
                            ax, cy + 6f * k,
                            cx, cy - 168f * k,
                            cx, cy - 168f * k,
                            bx, cy + 6f * k);
                    }

                    // 弧顶那颗正在传的文件
                    float pr = 54f * k;
                    using (SolidBrush pb2 = new SolidBrush(paper))
                        g.FillEllipse(pb2, cx - pr, cy - 168f * k - pr, pr * 2, pr * 2);
                }
            }
            return bmp;
        }

        static byte[] IcoImage(Bitmap bmp, int size)
        {
            int xorSize = size * size * 4;
            int andRow = ((size + 31) / 32) * 4;
            int andSize = andRow * size;
            byte[] d = new byte[40 + xorSize + andSize];

            BitConverter.GetBytes(40).CopyTo(d, 0);
            BitConverter.GetBytes(size).CopyTo(d, 4);
            BitConverter.GetBytes(size * 2).CopyTo(d, 8);   // XOR + AND 合并高度
            d[12] = 1;
            d[14] = 32;

            int off = 40;
            for (int y = size - 1; y >= 0; y--)              // ICO 用 bottom-up
            {
                for (int x = 0; x < size; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    d[off++] = c.B;
                    d[off++] = c.G;
                    d[off++] = c.R;
                    d[off++] = c.A;
                }
            }
            return d;   // AND mask 全 0 = 全部不透明，交给 alpha
        }
    }

    // =====================================================================
    // 入口
    // =====================================================================
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // 全局异常兜底：任何未处理异常都写日志，不让 Windows 弹出
            // ".NET Framework 未处理异常" 对话框把进程杀掉。
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                try
                {
                    Exception ex = e.ExceptionObject as Exception;
                    Sys.Diag("FATAL unhandled: " + (ex == null ? "(non-exception)" : ex.GetType().FullName + ": " + ex.Message));
                    if (ex != null)
                    {
                        string st = ex.StackTrace;
                        if (!string.IsNullOrEmpty(st))
                        {
                            string[] lines = st.Split('\n');
                            for (int i = 0; i < lines.Length && i < 12; i++)
                                Sys.Diag("  at " + lines[i].Trim());
                        }
                    }
                }
                catch { }
            };
            System.Windows.Forms.Application.ThreadException += delegate (object s, System.Threading.ThreadExceptionEventArgs e)
            {
                try
                {
                    Sys.Diag("UI thread exception: " + e.Exception.GetType().Name + ": " + e.Exception.Message);
                }
                catch { }
            };
            System.Windows.Forms.Application.SetUnhandledExceptionMode(
                System.Windows.Forms.UnhandledExceptionMode.CatchException);

            bool selfTest = args.Length > 0 && args[0] == "--selftest";
            bool uiCheck = args.Length > 0 && args[0] == "--uicheck";
            bool codList = args.Length > 0 && args[0] == "--cods";
            bool trayOnly = args.Length > 0 && args[0] == "--tray";

            if (selfTest || uiCheck || codList)
            {
                int rc = 0;
                try
                {
                    Console.WriteLine("local_mac=" + Wsa.LocalMac());
                    List<BtDevice> list = BtEnum.List(true);
                    Console.WriteLine("all_devices=" + list.Count);
                    foreach (BtDevice d in list)
                        Console.WriteLine("  cod=0x" + d.Cod.ToString("X6") + " major=" + d.MajorClass +
                                          " kind=" + d.KindName + " | " + d.Name + " | " + Wsa.FmtMac(d.Addr) +
                                          (d.Connected ? " | connected" : ""));
                    if (uiCheck)
                    {
                        Application.EnableVisualStyles();
                        Cfg c = Cfg.Load();
                        Console.WriteLine("outdir=" + c.OutDir);
                        Console.WriteLine("autostart=" + c.AutoStart);
                        PopupForm f = new PopupForm(null);
                        f.CreateControl();
                        Console.WriteLine("form_ok=true controls=" + f.Controls.Count +
                                          " size=" + f.ClientSize.Width + "x" + f.ClientSize.Height);
                        f.Dispose();

                        ToastForm t = new ToastForm("收到文件", "test.zip   ·   123 KB", null);
                        t.CreateControl();
                        Console.WriteLine("toast_ok=true size=" + t.ClientSize.Width + "x" + t.ClientSize.Height);
                        t.Dispose();

                        Icon ic = TrayApp.MakeIcon();
                        Console.WriteLine("icon_ok=true size=" + ic.Width + "x" + ic.Height);
                        string outp = Path.Combine(Sys.ExeDir(), "tray-icon-check.ico");
                        using (FileStream fs = File.Create(outp)) ic.Save(fs);
                        Console.WriteLine("icon_saved=" + outp + " bytes=" + new FileInfo(outp).Length);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("ERROR: " + ex.Message);
                    rc = 1;
                }
                Environment.Exit(rc);
                return;
            }

            // 单实例：第二次启动不再弹"已在运行"，而是把已有实例的浮窗叫出来
            bool created;
            Mutex mutex = new Mutex(true, "BtBeam_SingleInstance_v2", out created);
            if (!created)
            {
                try
                {
                    Net.PostMessage((IntPtr)Net.HWND_BROADCAST, Net.WM_SHOW, IntPtr.Zero, IntPtr.Zero);
                }
                catch { }
                return;
            }

            try { Sys.Diag("[main] start, exe=" + Sys.ExePath()); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new TrayApp(trayOnly));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "BtBeam 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                GC.KeepAlive(mutex);
            }
        }
    }
}
