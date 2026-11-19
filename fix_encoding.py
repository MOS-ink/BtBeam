# -*- coding: utf-8 -*-
"""
修中文文件名乱码

根因：Hotlink.ReadLine 用 sb.Append((char)b) 逐字节转字符。
UTF-8 的中文是 3 字节（"测" = E6 B5 8B），这样拆开后
"测试.txt" 就变成 "æµ‹è¯•.txt"（mojibake）。

蓝牙通道的 Link.ReadLine 用的是 List<byte> + Encoding.UTF8.GetString，
一直是对的——所以只有热点通道传中文名会乱。

同时补一层文件名清洗，避免对端送来的名字含 Windows 非法字符时落盘失败。
"""
import io

P = r"D:\Projects\02-开发项目\lanbridge\BtBeam.cs.full"
t = io.open(P, encoding="utf-8-sig").read()
done = []

# ---------- 1) 修 Hotlink.ReadLine ----------
old = '''        static string ReadLine(System.IO.Stream s)
        {
            StringBuilder sb = new StringBuilder();
            while (sb.Length < 4096)
            {
                int b = s.ReadByte();
                if (b < 0) break;
                if (b == '\\n') break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }'''

new = '''        static string ReadLine(System.IO.Stream s)
        {
            // 必须按字节收齐再整体 UTF-8 解码。
            // 逐字节 (char)b 会把多字节的中文拆散："测试.txt" 变成 "æµ‹è¯•.txt"。
            List<byte> buf = new List<byte>();
            while (buf.Count < 8192)
            {
                int b = s.ReadByte();
                if (b < 0) break;
                if (b == '\\n') break;
                buf.Add((byte)b);
            }
            if (buf.Count == 0) return "";
            return Encoding.UTF8.GetString(buf.ToArray()).TrimEnd('\\r');
        }'''

if old in t:
    t = t.replace(old, new); done.append("Hotlink.ReadLine 改为 UTF-8 整体解码")
else:
    done.append("MISS ReadLine（原文不匹配）")

# ---------- 2) Sys 里加 SafeFileName ----------
anchor = '        public static string ExeDir()'
safe_fn = '''        /// 把对端送来的文件名收拾成能落盘的：去路径、换非法字符、截长度。
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

''' + anchor

if anchor in t and 'SafeFileName' not in t:
    t = t.replace(anchor, safe_fn, 1)
    done.append("Sys.SafeFileName 已加入")
else:
    done.append("MISS SafeFileName 锚点")

# ---------- 3) 两处 fname 走清洗 ----------
o1 = 'string fname = mm.Groups[1].Value;'
n1 = 'string fname = Sys.SafeFileName(mm.Groups[1].Value);'
if o1 in t:
    t = t.replace(o1, n1); done.append("蓝牙接收端 fname 已清洗")
else:
    done.append("MISS 蓝牙 fname")

o2 = 'string fname = json.Substring(n1 + 8, n2 - (n1 + 8));'
n2_ = 'string fname = Sys.SafeFileName(json.Substring(n1 + 8, n2 - (n1 + 8)));'
if o2 in t:
    t = t.replace(o2, n2_); done.append("热点接收端 fname 已清洗")
else:
    done.append("MISS 热点 fname")

io.open(P, "w", encoding="utf-8-sig", newline="\n").write(t)
for d in done:
    print(" -", d)
print("written", len(t), "chars")
