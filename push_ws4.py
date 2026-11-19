import io, json, hashlib, paramiko

TP = r"D:\Projects\03-网站内容\site\tools.json"
EXE = r"D:\Projects\02-开发项目\lanbridge\BtBeam.exe"
HOST, PORT, USER, PWD = "mosink.top", 22, "normal", "1"

data = io.open(EXE, "rb").read()
sha = hashlib.sha256(data).hexdigest()
kb = round(len(data) / 1024)

tools = json.load(io.open(TP, encoding="utf-8"))
for t in tools:
    if t.get("name") == "BtBeam":
        for d in t.get("downloads", []):
            d["size"] = "%d KB" % kb
            d["sha256"] = sha
io.open(TP, "w", encoding="utf-8", newline="\n").write(
    json.dumps(tools, ensure_ascii=False, indent=2) + "\n")

c = paramiko.SSHClient()
c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
c.connect(HOST, PORT, USER, PWD, timeout=20, look_for_keys=False, allow_agent=False)

def sudo(cmd):
    si, so, se = c.exec_command("sudo -S -p '' " + cmd, timeout=60)
    si.write(PWD + "\n"); si.flush()
    return so.read().decode("utf-8", "replace").strip()

sftp = c.open_sftp()
sftp.put(EXE, "/tmp/p.exe")
sftp.put(TP, "/tmp/p.json")
sftp.close()
sudo("install -m 644 -o root -g root /tmp/p.exe /var/www/personal/workshop/files/BtBeam.exe")
sudo("install -m 644 -o root -g root /tmp/p.json /var/www/personal/workshop/tools.json")
print("remote:", sudo("sha256sum /var/www/personal/workshop/files/BtBeam.exe"))
print("local :", sha, "(%d KB)" % kb)
c.close()
