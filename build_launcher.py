import os
import subprocess
import sys

import re

launcher_dir = r"E:\Civilization DEV\launcher"
assets_dir = os.path.join(launcher_dir, "assets")
cs_path = os.path.join(launcher_dir, "Launcher.cs")

# Automatically inject latest git commit SHA into Launcher.cs
try:
    sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=launcher_dir, text=True).strip()
    if sha:
        with open(cs_path, "r", encoding="utf-8") as f:
            cs_text = f.read()
        new_cs_text = re.sub(r'CurrentLauncherSha\s*=\s*"[^"]*"', f'CurrentLauncherSha = "{sha}"', cs_text)
        if new_cs_text != cs_text:
            with open(cs_path, "w", encoding="utf-8") as f:
                f.write(new_cs_text)
            print(f"Updated Launcher.cs CurrentLauncherSha to {sha}")
except Exception as ex:
    print(f"Warning: could not retrieve git SHA: {ex}")

base_resources = [
    "/resource:ui.html,ui.html",
    "/resource:manifest.json,manifest.json",
    "/resource:news.json,news.json",
    "/resource:icon.ico,icon.ico"
]

essential_assets = [
    "assets/icon.png",
    "assets/minecraft-rus.ttf",
    "assets/news_banner_1.png",
    "assets/news_banner_1.svg",
    "assets/news_banner_patch.png",
    "assets/news_banner_patch.svg",
    "assets/news_banner_patch_green.svg",
    "assets/баннеры новой новости/jam1.svg",
    "assets/баннеры новой новости/janer.svg",
    "assets/баннеры новой новости/ses3.svg",
]

for ident in essential_assets:
    rel_path = ident.replace("/", os.sep)
    full_path = os.path.join(launcher_dir, rel_path)
    if os.path.isfile(full_path):
        base_resources.append(f'/resource:{rel_path},{ident}')

csc_path = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
args = [
    csc_path,
    "/target:winexe",
    "/platform:x64",
    "/optimize+",
    "/win32icon:icon.ico",
    "/win32manifest:app.manifest",
    '/out:CV Launcher.exe',
    "/r:System.IO.Compression.FileSystem.dll",
    "/r:System.Web.Extensions.dll",
    "/r:System.Windows.Forms.dll",
    "/r:System.Drawing.dll"
] + base_resources + ["AssemblyInfo.cs", "Launcher.cs"]

print(f"Embedding {len(base_resources)} resources into CV Launcher.exe...")
result = subprocess.run(args, cwd=launcher_dir, capture_output=True, text=True, errors="replace")
try:
    print(result.stdout)
except Exception:
    sys.stdout.buffer.write(result.stdout.encode('utf-8', errors='replace') + b'\n')

if result.stderr:
    try:
        print(result.stderr)
    except Exception:
        sys.stderr.buffer.write(result.stderr.encode('utf-8', errors='replace') + b'\n')

if result.returncode == 0:
    print("[SUCCESS] CV Launcher.exe built successfully with ALL assets embedded!")
else:
    print(f"[ERROR] Build failed with code {result.returncode}")
    sys.exit(result.returncode)
