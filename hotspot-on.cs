// hotspot-on.cs —— 只开/只关热点，不自动退出（常驻到关闭为止）
// 用法：
//   hotspot-on.exe on  PeerLink peerlink2026    开启热点
//   hotspot-on.exe off                          关闭热点
using System;
using System.Threading;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

class HotspotOn
{
    static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "on";
        string ssid = args.Length > 1 ? args[1] : "PeerLink";
        string pass = args.Length > 2 ? args[2] : "peerlink2026";

        try
        {
            ConnectionProfile cp = NetworkInformation.GetInternetConnectionProfile();
            if (cp == null)
            {
                Console.WriteLine("ERR 当前没有 Internet 连接配置，无法开热点");
                return;
            }
            Console.WriteLine("internet-profile=" + cp.ProfileName);

            NetworkOperatorTetheringManager mgr =
                NetworkOperatorTetheringManager.CreateFromConnectionProfile(cp);
            Console.WriteLine("state=" + mgr.TetheringOperationalState);

            if (mode == "status")
            {
                Console.WriteLine("state=" + mgr.TetheringOperationalState);
                if (mgr.TetheringOperationalState.ToString() == "On")
                {
                    NetworkOperatorTetheringAccessPointConfiguration cur0 = mgr.GetCurrentAccessPointConfiguration();
                    Console.WriteLine("ssid=" + cur0.Ssid);
                    Console.WriteLine("pass=" + cur0.Passphrase);
                    Console.WriteLine("clients=" + mgr.ClientCount);
                }
                return;
            }
            if (mode == "off")
            {
                if (mgr.TetheringOperationalState.ToString() == "On")
                    mgr.StopTetheringAsync().AsTask().Wait(20000);
                Console.WriteLine("stopped=" + mgr.TetheringOperationalState);
                return;
            }

            // 顺序必须 Stop -> Configure -> Start，否则 ICS 会崩
            if (mgr.TetheringOperationalState.ToString() == "On")
            {
                Console.WriteLine("already on, restarting ...");
                mgr.StopTetheringAsync().AsTask().Wait(20000);
                Thread.Sleep(2000);
            }

            NetworkOperatorTetheringAccessPointConfiguration cfg =
                new NetworkOperatorTetheringAccessPointConfiguration();
            cfg.Ssid = ssid;
            cfg.Passphrase = pass;

            Console.WriteLine("configuring " + ssid + " ...");
            if (!mgr.ConfigureAccessPointAsync(cfg).AsTask().Wait(15000))
            {
                Console.WriteLine("ERR 配置超时");
                return;
            }
            Thread.Sleep(800);

            Console.WriteLine("starting ...");
            NetworkOperatorTetheringOperationResult r =
                mgr.StartTetheringAsync().AsTask().Result;
            Console.WriteLine("start=" + r.Status);
            Thread.Sleep(3000);

            NetworkOperatorTetheringManager m2 =
                NetworkOperatorTetheringManager.CreateFromConnectionProfile(
                    NetworkInformation.GetInternetConnectionProfile());
            Console.WriteLine("final=" + m2.TetheringOperationalState);
            if (m2.TetheringOperationalState.ToString() == "On")
            {
                NetworkOperatorTetheringAccessPointConfiguration cur = m2.GetCurrentAccessPointConfiguration();
                Console.WriteLine("ssid=" + cur.Ssid);
                Console.WriteLine("pass=" + cur.Passphrase);
                Console.WriteLine("clients=" + m2.ClientCount);
                Console.WriteLine("OK 热点已开启，保持中（关闭请运行：hotspot-on.exe off）");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERR " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}

