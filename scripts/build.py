"""在干净的 Windows 环境里跑 dotnet build / publish。

bash(MSYS) 会把含反斜杠的环境变量做路径转换，导致 dotnet 拿到的
LOCALAPPDATA / TEMP 是 Unix 风格路径，NuGet 拼路径时报
"Value cannot be null (Parameter 'path1')"。

用法（在项目根目录执行）:
    python scripts/build.py build
    python scripts/build.py publish
    python scripts/build.py build --no-incremental
"""
import os
import subprocess
import sys

# 项目根 = 本脚本所在目录（scripts/）的上一级，换机器/换盘符都不用改
PROJ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOTNET = r"C:\Program Files\dotnet\dotnet.exe"
USER_DIR = r"C:\Users\lwy"

# AOT 链接器需要的 MSVC / SDK 路径
MSVC = r"D:\visual studio\vs2026\VC\Tools\MSVC\14.51.36231"
SDK = r"C:\Program Files (x86)\Windows Kits\10"
SDKVER = "10.0.26100.0"


def clean_env():
    env = dict(os.environ)

    # 把 MSYS/Git-Bash 的痕迹全部清掉：dotnet 会据此误判宿主平台，
    # AOT 时报 "Cross-OS native compilation is not supported"
    for k in ("MSYSTEM", "MINGW_PREFIX", "MINGW_CHOST", "MSYS", "OSTYPE",
              "TERM", "TERM_PROGRAM", "SHELL", "BASH_ENV", "POSIXLY_CORRECT"):
        env.pop(k, None)

    env["OS"] = "Windows_NT"
    # PATH 只留 Windows 原生路径 + MSVC/SDK 的 bin（AOT 要 link.exe）
    env["PATH"] = ";".join([
        rf"{MSVC}\bin\Hostx64\x64",
        rf"{SDK}\bin\{SDKVER}\x64",
        r"C:\Windows\System32",
        r"C:\Windows",
        r"C:\Windows\System32\Wbem",
        r"C:\Program Files\dotnet",
    ])

    # 强制成 Windows 风格路径（不做 MSYS 转换）
    env["USERPROFILE"] = USER_DIR
    env["APPDATA"] = rf"{USER_DIR}\AppData\Roaming"
    env["LOCALAPPDATA"] = rf"{USER_DIR}\AppData\Local"
    env["TEMP"] = rf"{USER_DIR}\AppData\Local\Temp"
    env["TMP"] = env["TEMP"]
    env["HOME"] = USER_DIR
    env["LIB"] = rf"{MSVC}\lib\x64;{SDK}\lib\{SDKVER}\um\x64;{SDK}\lib\{SDKVER}\ucrt\x64"
    env["INCLUDE"] = (
        rf"{MSVC}\include;{SDK}\Include\{SDKVER}\ucrt;"
        rf"{SDK}\Include\{SDKVER}\um;{SDK}\Include\{SDKVER}\shared"
    )
    return env


def main():
    if len(sys.argv) < 2:
        print("用法: python scripts/build.py build|publish [额外参数...]")
        return 1

    action = sys.argv[1]
    extra = sys.argv[2:]

    # --no-restore：这台机器上 NuGet restore 会报 "path1 null"（SDK 被沙箱搞坏），
    # 依赖没变时直接用 obj/project.assets.json 跳过还原。
    # 但刚 clone 的仓库没有这个文件，那时必须让 dotnet 自己还原，否则直接失败。
    assets = os.path.join(PROJ, "obj", "project.assets.json")
    no_restore = ["--no-restore"] if os.path.exists(assets) else []
    if not no_restore:
        print("[build.py] 没找到 obj/project.assets.json，本次会让 dotnet 先做 restore")
    else:
        print("[build.py] 已有 project.assets.json，跳过 restore")

    if action == "build":
        cmd = [DOTNET, "build"] + no_restore + extra
    elif action == "publish":
        # IlcUseEnvironmentalTools：跳过 findvcvarsall.bat 探测（沙箱拦注册表 → 探测失败），
        # 直接用我们 env 里塞好的 MSVC / SDK 路径
        cmd = [DOTNET, "publish", "-c", "Release", "-r", "win-x64", "--self-contained",
               "-p:IlcUseEnvironmentalTools=true"] + no_restore + extra
    else:
        print(f"不认识的动作: {action}")
        return 1

    print("[build.py] env LOCALAPPDATA =", clean_env()["LOCALAPPDATA"])
    print("[build.py] 执行:", " ".join(cmd))
    print("-" * 60)

    proc = subprocess.run(cmd, cwd=PROJ, env=clean_env())
    print("-" * 60)
    print(f"[build.py] 退出码 {proc.returncode}")

    # 发布目录里的 pdb 有 70+ MB，运行时完全用不到。
    # 它是 ILCompiler 直接写进去的，不走 MSBuild 发布清单，Target 删不掉，只能发布后删。
    if action == "publish" and proc.returncode == 0:
        pub = os.path.join(PROJ, "bin", "Release", "net10.0", "win-x64", "publish")
        if os.path.isdir(pub):
            freed = 0
            for f in os.listdir(pub):
                if f.lower().endswith(".pdb"):
                    fp = os.path.join(pub, f)
                    freed += os.path.getsize(fp)
                    os.remove(fp)
            if freed:
                print(f"[build.py] 已删除 pdb，释放 {freed / 1048576:.1f} MB")

    # 顺手报一下产物时间
    dll = os.path.join(PROJ, "bin", "Debug", "net10.0", "LargeHardLiteLauncher.dll")
    exe = os.path.join(PROJ, "bin", "Release", "net10.0", "win-x64", "publish", "LargeHardLiteLauncher.exe")
    for p in (dll, exe):
        if os.path.exists(p):
            import datetime
            t = datetime.datetime.fromtimestamp(os.path.getmtime(p))
            print(f"[build.py] {os.path.basename(p)}  {t:%Y-%m-%d %H:%M:%S}  {os.path.getsize(p)/1048576:.2f} MB")

    return proc.returncode


if __name__ == "__main__":
    sys.exit(main())
