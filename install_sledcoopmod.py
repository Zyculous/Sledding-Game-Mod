#!/usr/bin/env python3
"""
SledCoopMod Installer  –  v1.1.0

GUI installer for SledCoopMod, a BepInEx split-screen local co-op mod
for Sledding Game.

Platform : Debian / Ubuntu Linux
Requires : python3  +  python3-tk
           sudo apt install python3-tk

What it does
────────────
1. Locates (or lets you browse to) the game folder.
2. Downloads BepInEx 6 IL2CPP (Windows) from GitHub and extracts it
   into the game folder so it runs through Steam / Proton.
3. Sets the Steam launch option WINEDLLOVERRIDES="winhttp=n,b" %command%
   so Proton actually loads the BepInEx doorstop DLL.
4. Copies SledCoopMod.dll into BepInEx/plugins/.

After installation, launch the game once via Steam.  BepInEx will run,
generate its interop DLLs, and the mod will be active from that point on.
Check BepInEx/LogOutput.log to confirm the mod loaded.
"""

# ── tkinter availability check ────────────────────────────────────────────────
try:
    import tkinter as tk
    from tkinter import filedialog, messagebox, scrolledtext, ttk
except ImportError:
    import sys
    print("ERROR: python3-tk is not installed.", file=sys.stderr)
    print("Fix:   sudo apt install python3-tk", file=sys.stderr)
    sys.exit(1)

import json
import os
import re
import shutil
import threading
import urllib.error
import urllib.request
import zipfile
from pathlib import Path
from typing import Callable, List, Optional, Tuple

# ── Constants ─────────────────────────────────────────────────────────────────

APP_TITLE   = "SledCoopMod Installer"
APP_VERSION = "1.1.0"
MOD_DLL     = "SledCoopMod.dll"

# Steam App ID for "Sledding Game"
STEAM_APP_ID = "3438850"

# Launch option required for BepInEx to load under Proton on Linux.
# Without this Proton ignores the winhttp.dll doorstop and BepInEx never runs.
REQUIRED_LAUNCH_OPTION = 'WINEDLLOVERRIDES="winhttp=n,b" %command%'

# BepInEx bleeding-edge build server (has builds that support Unity 6 / metadata v31)
BEPINEX_BUILD_SERVER_URL = "https://builds.bepinex.dev/projects/bepinex_be"

# Fallback if build server is unreachable — be.755 supports Unity 6000.x (metadata v31)
BEPINEX_FALLBACK_URL = (
    "https://builds.bepinex.dev/projects/bepinex_be/755/"
    "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip"
)

# Minimum build number that supports IL2CPP metadata v31 (Unity 6000.x)
# be.697 / 6.0.0-pre.2 only supports metadata 23-29 and will crash on this game
BEPINEX_MIN_BUILD = 755

# GitHub Releases API (secondary source — pre.2/be.697 is the latest GitHub release,
# which does NOT support Unity 6.  We only use this as a last resort.)
BEPINEX_GITHUB_API_URL = "https://api.github.com/repos/BepInEx/BepInEx/releases"

# Pattern that matches the correct BepInEx bleeding-edge asset from the build server
BEPINEX_ASSET_RE = re.compile(
    r"BepInEx[_-]Unity\.IL2CPP[_-]win[_-]x64[_-].+\.zip", re.IGNORECASE
)

# Known Steam root directories (the folder that contains steamapps/)
STEAM_ROOT_CANDIDATES = [
    Path.home() / ".steam" / "debian-installation",
    Path.home() / ".steam" / "steam",
    Path.home() / ".local" / "share" / "Steam",
    Path("/mnt/games"),
]

# Game folder name inside steamapps/common/
GAME_FOLDER_NAME = "Sledding Game"

# Files that confirm a folder is the correct game
GAME_MARKERS = [
    "Sledding Game.exe",
    "Sledding Game_Data/app.info",
    "EOSBootstrapper.ini",
]

# File that confirms BepInEx core is already installed
BEPINEX_MARKER = "BepInEx/core/BepInEx.Core.dll"

# Log file written by BepInEx on first run
BEPINEX_LOG = "BepInEx/LogOutput.log"

# DLL search locations relative to this installer script
MOD_DLL_CANDIDATES = [
    "{script_dir}/{dll}",
    "{script_dir}/BepInEx/plugins/{dll}",
    "{script_dir}/SledCoopMod/bin/Release/net6.0/{dll}",
    "{script_dir}/SledCoopMod/bin/Debug/net6.0/{dll}",
]

# ── Colour palette ────────────────────────────────────────────────────────────
C_HEADER_BG = "#1a1a2e"
C_HEADER_FG = "#e94560"
C_LOG_BG    = "#0f0f1a"
C_LOG_FG    = "#ccccee"
C_OK        = "#22aa44"
C_WARN      = "#cc7700"
C_BTN_BG    = "#e94560"
C_BTN_FG    = "#ffffff"
C_BTN_ACT   = "#c73250"


# ── Standalone helpers ────────────────────────────────────────────────────────

def is_game_dir(path: Path) -> bool:
    """Return True when *path* contains at least one game marker file."""
    return any((path / m).exists() for m in GAME_MARKERS)


def find_mod_dll(script_dir: Path) -> Optional[Path]:
    """Search for SledCoopMod.dll relative to the installer script."""
    for template in MOD_DLL_CANDIDATES:
        candidate = Path(template.format(script_dir=script_dir, dll=MOD_DLL))
        if candidate.is_file():
            return candidate
    return None


def extract_zip(
    zip_path: Path,
    dest_dir: Path,
    on_progress: Optional[Callable[[float, str], None]] = None,
) -> None:
    """
    Extract *zip_path* directly into *dest_dir*.

    BepInEx IL2CPP zips contain files at the root level (BepInEx/, winhttp.dll,
    doorstop_config.ini, …) with no extra wrapper directory, so we extract
    straight to dest_dir without any prefix stripping.
    """
    with zipfile.ZipFile(zip_path) as zf:
        members = zf.namelist()
        total = max(len(members), 1)
        for i, member in enumerate(members):
            target = dest_dir / member
            if member.endswith("/"):
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with zf.open(member) as src, open(target, "wb") as dst:
                    shutil.copyfileobj(src, dst)
            if on_progress:
                on_progress((i + 1) / total, f"Extracting… {i + 1}/{total} files")


# ── Steam helpers ─────────────────────────────────────────────────────────────

def _parse_vdf_library_paths(vdf_path: Path) -> List[Path]:
    """
    Return a list of Steam library root paths found in a libraryfolders.vdf.
    Only handles the simple text-VDF format (not binary VDF).
    """
    paths: List[Path] = []
    try:
        text = vdf_path.read_text(encoding="utf-8", errors="replace")
        for m in re.finditer(r'"path"\s+"([^"]+)"', text):
            p = Path(m.group(1))
            if p.is_dir():
                paths.append(p)
    except OSError:
        pass
    return paths


def find_steam_game_paths() -> List[Path]:
    """
    Return all paths where the game folder might exist by scanning every
    known Steam library root (including those listed in libraryfolders.vdf).
    """
    roots: List[Path] = []

    # Start with known candidate roots
    for candidate in STEAM_ROOT_CANDIDATES:
        if candidate.is_dir():
            roots.append(candidate)

    # Also parse libraryfolders.vdf files to catch extra library drives
    vdf_locations = [
        root / "config" / "libraryfolders.vdf"
        for root in STEAM_ROOT_CANDIDATES
    ] + [
        root / "steamapps" / "libraryfolders.vdf"
        for root in STEAM_ROOT_CANDIDATES
    ]
    for vdf in vdf_locations:
        if vdf.is_file():
            for lib_root in _parse_vdf_library_paths(vdf):
                if lib_root not in roots:
                    roots.append(lib_root)

    # Build candidate game paths from each root
    game_paths: List[Path] = []
    for root in roots:
        for sub in ("steamapps/common", "SteamApps/common"):
            p = root / sub / GAME_FOLDER_NAME
            if p not in game_paths:
                game_paths.append(p)

    return game_paths


def find_localconfig_files() -> List[Path]:
    """
    Return all localconfig.vdf files found under known Steam user-data paths.
    """
    results: List[Path] = []
    for root in STEAM_ROOT_CANDIDATES:
        userdata = root / "userdata"
        if not userdata.is_dir():
            continue
        for user_dir in userdata.iterdir():
            cfg = user_dir / "config" / "localconfig.vdf"
            if cfg.is_file():
                results.append(cfg)
    return results


def get_launch_options(localconfig: Path, app_id: str) -> str:
    """
    Read the current Steam launch options for *app_id* from *localconfig*.
    Returns an empty string if not set or file can't be parsed.
    Unescapes VDF escape sequences (\\\" → \") before returning.
    """
    try:
        text = localconfig.read_text(encoding="utf-8", errors="replace")
        # Find the app entry block inside the "apps" section
        apps_idx = text.lower().find('"apps"')
        if apps_idx < 0:
            return ""
        apps_text = text[apps_idx:]
        app_idx = apps_text.find(f'"{app_id}"')
        if app_idx < 0:
            return ""
        block_start = apps_text.find("{", app_idx)
        if block_start < 0:
            return ""
        # Find the matching closing brace (one level)
        depth = 0
        pos = block_start
        while pos < len(apps_text):
            c = apps_text[pos]
            if c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth == 0:
                    break
            pos += 1
        block = apps_text[block_start:pos]
        # Match value including escaped quotes (\")
        m = re.search(r'"LaunchOptions"\s+"((?:[^"\\]|\\.)*)"', block)
        if m:
            return m.group(1).replace('\\"', '"').replace("\\\\", "\\")
        return ""
    except OSError:
        return ""


def set_launch_options(localconfig: Path, app_id: str, options: str) -> bool:
    """
    Set the Steam launch options for *app_id* in *localconfig.vdf*.
    Creates a backup (.vdf.bak) before modifying.  Returns True on success.

    Inner double-quotes in *options* are escaped to \\" for valid VDF storage.
    """
    # Escape for VDF string value (backslash then backslash, then quotes)
    vdf_value = options.replace("\\", "\\\\").replace('"', '\\"')

    try:
        text = localconfig.read_text(encoding="utf-8", errors="replace")
        original = text

        apps_idx = text.lower().find('"apps"')
        if apps_idx < 0:
            return False

        apps_text = text[apps_idx:]
        app_idx = apps_text.find(f'"{app_id}"')

        if app_idx >= 0:
            block_start_rel = apps_text.find("{", app_idx)
            if block_start_rel < 0:
                return False

            # Find closing brace of app block
            depth = 0
            pos = block_start_rel
            while pos < len(apps_text):
                c = apps_text[pos]
                if c == "{":
                    depth += 1
                elif c == "}":
                    depth -= 1
                    if depth == 0:
                        break
                pos += 1

            abs_start = apps_idx + block_start_rel
            abs_end   = apps_idx + pos  # index of the closing }

            block = text[abs_start:abs_end]
            if '"LaunchOptions"' in block:
                # Replace existing value (handles escaped quotes in old value)
                new_block = re.sub(
                    r'"LaunchOptions"\s+"(?:[^"\\]|\\.)*"',
                    f'"LaunchOptions"\t\t"{vdf_value}"',
                    block,
                )
                text = text[:abs_start] + new_block + text[abs_end:]
            else:
                # Insert before the closing brace
                lines = block.split("\n")
                indent = "\t\t\t\t\t\t"  # default: 6 tabs
                for line in lines:
                    stripped = line.lstrip("\t")
                    if stripped.startswith('"') and len(line) > len(stripped):
                        indent = line[: len(line) - len(stripped)]
                        break
                insert_str = f'{indent}"LaunchOptions"\t\t"{vdf_value}"\n'
                text = text[:abs_end] + insert_str + text[abs_end:]
        else:
            # App not in list yet — insert a new block before the apps closing brace
            apps_open = apps_text.find("{")
            if apps_open < 0:
                return False
            depth = 0
            pos = apps_open
            while pos < len(apps_text):
                c = apps_text[pos]
                if c == "{":
                    depth += 1
                elif c == "}":
                    depth -= 1
                    if depth == 0:
                        break
                pos += 1
            abs_close = apps_idx + pos
            new_entry = (
                f'\t\t\t\t\t"{app_id}"\n'
                f'\t\t\t\t\t{{\n'
                f'\t\t\t\t\t\t"LaunchOptions"\t\t"{vdf_value}"\n'
                f'\t\t\t\t\t}}\n'
                f'\t\t\t\t'
            )
            text = text[:abs_close] + new_entry + text[abs_close:]

        if text == original:
            return True  # Nothing changed — already set correctly

        # Write backup then overwrite
        localconfig.with_suffix(".vdf.bak").write_text(original, encoding="utf-8")
        localconfig.write_text(text, encoding="utf-8")
        return True
    except OSError:
        return False


def has_required_launch_option(localconfig: Path, app_id: str) -> bool:
    """Return True if *localconfig* already contains the required WINEDLLOVERRIDES."""
    current = get_launch_options(localconfig, app_id)
    return "winhttp=n,b" in current.lower() or "winhttp" in current.lower()


def get_bepinex_version(game: Path) -> str:
    """
    Read the installed BepInEx version from its log file.
    Returns a string like '6.0.0-be.697' or '' if not determinable.
    """
    log_path = game / BEPINEX_LOG
    if log_path.is_file():
        try:
            # Read first 2 KB — version line is near the top
            text = log_path.read_text(encoding="utf-8", errors="replace")[:2048]
            m = re.search(r"BepInEx (6[\d.\-a-z+]+) -", text)
            if m:
                return m.group(1).strip()
        except OSError:
            pass
    return ""


def is_bepinex_outdated(version: str) -> bool:
    """
    Return True if the installed BepInEx version is too old to support
    Unity 6000.x (IL2CPP metadata version 31).
    be.697 / 6.0.0-pre.2 only supports metadata 23-29.
    """
    if not version:
        return False  # can't tell; don't force reinstall
    # Bleeding-edge builds: 'be.NNN' build number
    m = re.search(r"be\.(\d+)", version)
    if m and int(m.group(1)) < BEPINEX_MIN_BUILD:
        return True
    # GitHub pre-release tags (pre.1 / pre.2 == be.577 / be.697)
    if re.search(r"pre\.[12]\b", version):
        return True
    return False


# ── Main application ──────────────────────────────────────────────────────────

class InstallerApp(tk.Tk):

    def __init__(self) -> None:
        super().__init__()
        self.title(APP_TITLE)
        self.resizable(False, False)
        self.geometry("620x740")

        self._thread: Optional[threading.Thread] = None
        # localconfig files found during detection
        self._localconfigs: List[Path] = []

        ttk.Style(self).theme_use("clam")
        self._build_ui()
        # Defer auto-detect so the window is visible first
        self.after(150, self._auto_detect)

    # ── UI ─────────────────────────────────────────────────────────────────────

    def _build_ui(self) -> None:
        P = 12

        # Header
        hdr = tk.Frame(self, bg=C_HEADER_BG)
        hdr.pack(fill=tk.X)
        tk.Label(
            hdr, text="  SledCoopMod", font=("Sans", 16, "bold"),
            bg=C_HEADER_BG, fg=C_HEADER_FG, pady=10,
        ).pack(side=tk.LEFT)
        tk.Label(
            hdr, text=f"v{APP_VERSION}  ·  Split-Screen Local Co-op Mod",
            font=("Sans", 9), bg=C_HEADER_BG, fg="#8888aa",
        ).pack(side=tk.LEFT, padx=8)

        body = ttk.Frame(self, padding=P)
        body.pack(fill=tk.BOTH, expand=True)

        # Game folder row
        frm_path = ttk.LabelFrame(body, text=" Game Folder ", padding=(P, 6))
        frm_path.pack(fill=tk.X, pady=(0, 8))

        self._path_var = tk.StringVar()
        ttk.Entry(frm_path, textvariable=self._path_var, width=48).pack(
            side=tk.LEFT, fill=tk.X, expand=True, padx=(0, 4)
        )
        ttk.Button(frm_path, text="Browse…", command=self._browse).pack(
            side=tk.LEFT, padx=(0, 4)
        )
        ttk.Button(frm_path, text="Detect", command=self._auto_detect).pack(side=tk.LEFT)

        # Status badges
        frm_status = ttk.LabelFrame(body, text=" Current Status ", padding=(P, 6))
        frm_status.pack(fill=tk.X, pady=(0, 8))

        ttk.Label(frm_status, text="BepInEx 6 IL2CPP:").grid(
            row=0, column=0, sticky=tk.W, pady=2
        )
        self._lbl_bepinex = ttk.Label(frm_status, text="–", foreground="gray")
        self._lbl_bepinex.grid(row=0, column=1, sticky=tk.W, padx=12)

        ttk.Label(frm_status, text="SledCoopMod DLL:").grid(
            row=1, column=0, sticky=tk.W, pady=2
        )
        self._lbl_mod = ttk.Label(frm_status, text="–", foreground="gray")
        self._lbl_mod.grid(row=1, column=1, sticky=tk.W, padx=12)

        ttk.Label(frm_status, text="Steam launch option:").grid(
            row=2, column=0, sticky=tk.W, pady=2
        )
        self._lbl_launch = ttk.Label(frm_status, text="–", foreground="gray")
        self._lbl_launch.grid(row=2, column=1, sticky=tk.W, padx=12)

        # Options
        frm_opts = ttk.LabelFrame(body, text=" What to Install ", padding=(P, 6))
        frm_opts.pack(fill=tk.X, pady=(0, 8))

        self._opt_bepinex = tk.BooleanVar(value=True)
        self._opt_mod     = tk.BooleanVar(value=True)
        self._opt_launch  = tk.BooleanVar(value=True)

        ttk.Checkbutton(
            frm_opts,
            text="Download & install BepInEx 6 IL2CPP  (mod loader — required)",
            variable=self._opt_bepinex,
        ).pack(anchor=tk.W)
        ttk.Checkbutton(
            frm_opts,
            text="Install SledCoopMod.dll into BepInEx/plugins/",
            variable=self._opt_mod,
        ).pack(anchor=tk.W, pady=(4, 0))
        ttk.Checkbutton(
            frm_opts,
            text='Set Steam launch option  (WINEDLLOVERRIDES — required for Proton)',
            variable=self._opt_launch,
        ).pack(anchor=tk.W, pady=(4, 0))

        # Progress bar
        frm_prog = ttk.Frame(body)
        frm_prog.pack(fill=tk.X, pady=(0, 4))

        self._pct_var = tk.DoubleVar()
        ttk.Progressbar(
            frm_prog, variable=self._pct_var, maximum=100, length=590,
        ).pack(fill=tk.X)
        self._lbl_prog = ttk.Label(
            frm_prog, text="Ready.", foreground="gray", font=("Sans", 9)
        )
        self._lbl_prog.pack(anchor=tk.W, pady=(2, 0))

        # Log
        frm_log = ttk.LabelFrame(body, text=" Log ", padding=(P, 4))
        frm_log.pack(fill=tk.BOTH, expand=True, pady=(0, 8))

        self._log_widget = scrolledtext.ScrolledText(
            frm_log,
            height=11,
            state=tk.DISABLED,
            font=("Monospace", 9),
            bg=C_LOG_BG,
            fg=C_LOG_FG,
            relief=tk.FLAT,
            insertbackground=C_LOG_FG,
        )
        self._log_widget.pack(fill=tk.BOTH, expand=True)

        # Action buttons
        frm_btns = ttk.Frame(body)
        frm_btns.pack(fill=tk.X)

        ttk.Button(
            frm_btns, text="Re-check", command=self._refresh_status
        ).pack(side=tk.LEFT)

        ttk.Button(
            frm_btns, text="Exit", command=self.destroy
        ).pack(side=tk.RIGHT, padx=(4, 0))

        self._install_btn = tk.Button(
            frm_btns,
            text="  Install  ",
            font=("Sans", 10, "bold"),
            bg=C_BTN_BG,
            fg=C_BTN_FG,
            activebackground=C_BTN_ACT,
            activeforeground=C_BTN_FG,
            relief=tk.FLAT,
            cursor="hand2",
            command=self._start_install,
        )
        self._install_btn.pack(side=tk.RIGHT)

    # ── Game detection ─────────────────────────────────────────────────────────

    def _auto_detect(self) -> None:
        # 1. Installer is already inside the game folder
        here = Path(__file__).resolve().parent
        if is_game_dir(here):
            self._set_path(here)
            return

        # 2. Search all Steam libraries
        for p in find_steam_game_paths():
            if is_game_dir(p):
                self._set_path(p)
                return

        self._log(
            "Could not auto-detect the game folder.\n"
            "Click Browse… and select the folder containing "
            "'Sledding Game.exe'."
        )

    def _browse(self) -> None:
        cur = self._path_var.get() or str(Path.home())
        chosen = filedialog.askdirectory(title="Select game folder", initialdir=cur)
        if chosen:
            self._set_path(Path(chosen))

    def _set_path(self, path: Path) -> None:
        self._path_var.set(str(path))
        self._log(f"Game folder: {path}")
        self._refresh_status()

    # ── Status ─────────────────────────────────────────────────────────────────

    def _refresh_status(self) -> None:
        game = Path(self._path_var.get())

        # BepInEx
        bep_installed = (game / BEPINEX_MARKER).exists()
        bep_version   = get_bepinex_version(game)
        bep_outdated  = is_bepinex_outdated(bep_version)

        if bep_installed and not bep_outdated:
            ver_str = f"  ({bep_version})" if bep_version else ""
            self._lbl_bepinex.config(
                text=f"✓ Installed{ver_str}", foreground=C_OK
            )
            self._opt_bepinex.set(False)
        elif bep_installed and bep_outdated:
            self._lbl_bepinex.config(
                text=(
                    f"⚠ Outdated: {bep_version}  "
                    f"— Unity 6 needs be.{BEPINEX_MIN_BUILD}+  (click Install to upgrade)"
                ),
                foreground=C_WARN,
            )
            self._opt_bepinex.set(True)   # auto-select upgrade
        else:
            self._lbl_bepinex.config(text="Not installed", foreground=C_WARN)
            self._opt_bepinex.set(True)

        # Mod DLL
        mod_path = game / "BepInEx" / "plugins" / MOD_DLL
        if mod_path.exists():
            kb = mod_path.stat().st_size // 1024
            self._lbl_mod.config(text=f"✓ Installed  ({kb} KB)", foreground=C_OK)
        else:
            self._lbl_mod.config(text="Not installed", foreground=C_WARN)

        # Steam launch option
        self._localconfigs = find_localconfig_files()
        launch_ok = any(
            has_required_launch_option(lc, STEAM_APP_ID) for lc in self._localconfigs
        )
        if launch_ok:
            self._lbl_launch.config(text="✓ Set", foreground=C_OK)
            self._opt_launch.set(False)
        elif self._localconfigs:
            self._lbl_launch.config(
                text=f"Not set  ({len(self._localconfigs)} localconfig file(s) found)",
                foreground=C_WARN,
            )
            self._opt_launch.set(True)
        else:
            self._lbl_launch.config(
                text="localconfig.vdf not found — set manually in Steam",
                foreground=C_WARN,
            )
            self._opt_launch.set(False)

    # ── Logging ────────────────────────────────────────────────────────────────

    def _log(self, msg: str) -> None:
        def _append() -> None:
            w = self._log_widget
            w.config(state=tk.NORMAL)
            w.insert(tk.END, msg + "\n")
            w.see(tk.END)
            w.config(state=tk.DISABLED)
        self.after(0, _append)

    def _progress(self, pct: float, label: str = "") -> None:
        def _update() -> None:
            self._pct_var.set(pct)
            if label:
                self._lbl_prog.config(text=label)
        self.after(0, _update)

    def _set_install_btn(self, enabled: bool) -> None:
        state = tk.NORMAL if enabled else tk.DISABLED
        self.after(0, lambda: self._install_btn.config(state=state))

    # ── Install flow ───────────────────────────────────────────────────────────

    def _start_install(self) -> None:
        if self._thread and self._thread.is_alive():
            return

        game = Path(self._path_var.get())
        if not game.is_dir():
            messagebox.showerror(
                "Invalid path", "Please select a valid game folder first."
            )
            return
        if not is_game_dir(game):
            if not messagebox.askyesno(
                "Unrecognised folder",
                f"{game}\n\ndoesn't look like the Sledding Game folder.\n\n"
                "Continue anyway?",
            ):
                return

        self._thread = threading.Thread(
            target=self._worker, args=(game,), daemon=True
        )
        self._thread.start()

    def _worker(self, game: Path) -> None:
        self._set_install_btn(False)
        try:
            do_bep    = self._opt_bepinex.get()
            do_mod    = self._opt_mod.get()
            do_launch = self._opt_launch.get()

            if not do_bep and not do_mod and not do_launch:
                self._log("Nothing selected – tick at least one option.")
                return

            # ── Step 1: BepInEx ────────────────────────────────────────────
            if do_bep:
                existing_ver = get_bepinex_version(game)
                if existing_ver and is_bepinex_outdated(existing_ver):
                    self._log(
                        f"\n⚠ Outdated BepInEx detected: {existing_ver}\n"
                        f"  be.{BEPINEX_MIN_BUILD}+ is required for Unity 6000.x\n"
                        f"  (metadata version 31 — current version only supports 23-29)\n"
                        f"  Upgrading now…"
                    )
                else:
                    self._log("\n▶ Installing BepInEx 6 IL2CPP…")

                self._log("▶ Fetching latest BepInEx build from builds.bepinex.dev…")
                self._progress(0, "Fetching BepInEx build info…")
                url = self._resolve_bepinex_url()

                zip_dest = game / "_sledcoopmod_bepinex.zip"
                self._log("▶ Downloading BepInEx…")
                self._download(url, zip_dest, max_pct=55.0)

                self._log("▶ Extracting BepInEx into game folder…")
                self._progress(57, "Extracting BepInEx…")

                def _bep_progress(frac: float, label: str) -> None:
                    self._progress(57 + frac * 13, label)

                extract_zip(zip_dest, game, on_progress=_bep_progress)
                zip_dest.unlink(missing_ok=True)
                self._log("✓ BepInEx installed.")
                self._progress(71, "BepInEx installed.")

            # ── Step 2: Steam launch option ────────────────────────────────
            if do_launch:
                self._progress(73, "Setting Steam launch option…")
                self._log(
                    "\n▶ Setting Steam launch option…\n"
                    f"  {REQUIRED_LAUNCH_OPTION}"
                )
                # Steam must NOT be running when we edit localconfig.vdf,
                # otherwise Steam will overwrite our changes on exit.
                # We proceed anyway and warn if Steam is running.
                steam_running = self._is_steam_running()
                if steam_running:
                    self._log(
                        "  ⚠ Steam appears to be running. Close Steam before\n"
                        "    clicking Install so the launch option is saved."
                    )

                configs = find_localconfig_files() or self._localconfigs
                if not configs:
                    self._log(
                        "  ✗ localconfig.vdf not found. Set the launch option\n"
                        "    manually in Steam → Library → Sledding Game\n"
                        "    → Properties → Launch Options:\n"
                        f"    {REQUIRED_LAUNCH_OPTION}"
                    )
                else:
                    set_count = 0
                    for lc in configs:
                        if set_launch_options(lc, STEAM_APP_ID, REQUIRED_LAUNCH_OPTION):
                            self._log(f"  ✓ Written to {lc}")
                            set_count += 1
                        else:
                            self._log(f"  ✗ Failed to write to {lc}")
                    if set_count:
                        self._log(
                            f"✓ Launch option set in {set_count} localconfig file(s).\n"
                            "  A .vdf.bak backup was made before each change."
                        )
                self._progress(80, "Steam launch option done.")

            # ── Step 3: Mod DLL ────────────────────────────────────────────
            if do_mod:
                base = 82 if (do_bep or do_launch) else 5
                self._progress(base, "Locating SledCoopMod.dll…")
                self._log("\n▶ Installing SledCoopMod.dll…")

                script_dir = Path(__file__).resolve().parent
                src = find_mod_dll(script_dir)
                if src is None:
                    raise FileNotFoundError(
                        f"Cannot find {MOD_DLL}.\n\n"
                        "Place SledCoopMod.dll in the same folder as this installer "
                        "script and try again.\n\n"
                        f"Searched in:\n" +
                        "\n".join(
                            t.format(script_dir=script_dir, dll=MOD_DLL)
                            for t in MOD_DLL_CANDIDATES
                        )
                    )

                plugins_dir = game / "BepInEx" / "plugins"
                plugins_dir.mkdir(parents=True, exist_ok=True)
                dest = plugins_dir / MOD_DLL
                shutil.copy2(src, dest)
                kb = dest.stat().st_size // 1024
                self._log(f"  {src}")
                self._log(f"  → {dest}  ({kb} KB)")
                self._log("✓ SledCoopMod.dll installed.")

            self._progress(100, "Installation complete!")
            self._log(
                "\n✅ All done!\n"
                "   1. Close Steam (if open).\n"
                "   2. Reopen Steam, then launch Sledding Game normally.\n"
                "   3. BepInEx will initialise on first launch.\n"
                "   4. Check BepInEx/LogOutput.log — you should see [SledCoopMod]\n"
                "      lines confirming the mod loaded.\n"
                "\n"
                "   If BepInEx/LogOutput.log is still not created:\n"
                f"   → Verify Steam launch options contain:  {REQUIRED_LAUNCH_OPTION}"
            )
            self.after(0, self._refresh_status)
            self.after(
                0,
                lambda: messagebox.showinfo(
                    "Installation complete",
                    "SledCoopMod has been installed!\n\n"
                    "Close Steam, reopen it, then launch Sledding Game.\n"
                    "BepInEx will run on first launch and the mod will be active.\n\n"
                    "Check BepInEx/LogOutput.log to verify the mod loaded.\n\n"
                    "If no log appears, confirm Steam launch options contain:\n"
                    f"{REQUIRED_LAUNCH_OPTION}",
                ),
            )

        except Exception as exc:
            self._log(f"\n✗ Error: {exc}")
            self._progress(0, f"Error — see log for details.")
            self.after(0, lambda: messagebox.showerror("Installation failed", str(exc)))
        finally:
            self._set_install_btn(True)

    @staticmethod
    def _is_steam_running() -> bool:
        """Return True if a Steam process is currently running."""
        try:
            import subprocess
            result = subprocess.run(
                ["pgrep", "-x", "steam"], capture_output=True, timeout=3
            )
            return result.returncode == 0
        except Exception:
            return False

    # ── Network helpers ────────────────────────────────────────────────────────

    def _resolve_bepinex_url(self) -> str:
        """
        Return a download URL for the latest BepInEx IL2CPP Windows x64 build.

        Priority:
        1. builds.bepinex.dev  — bleeding-edge, supports Unity 6 / metadata v31
        2. BEPINEX_FALLBACK_URL — hardcoded be.755 from the same server
        """
        # ── 1. BepInEx build server ─────────────────────────────────────────
        try:
            req = urllib.request.Request(
                BEPINEX_BUILD_SERVER_URL,
                headers={"User-Agent": f"SledCoopMod-Installer/{APP_VERSION}"},
            )
            with urllib.request.urlopen(req, timeout=12) as resp:
                html = resp.read().decode("utf-8", errors="replace")

            # Parse /projects/bepinex_be/NNN/BepInEx-Unity.IL2CPP-win-x64-*.zip links
            link_re = re.compile(
                r'href="(/projects/bepinex_be/(\d+)/[^"]*Unity\.IL2CPP[^"]*win[^"]*x64[^"]*\.zip[^"]*)"',
                re.IGNORECASE,
            )
            matches = link_re.findall(html)
            if matches:
                # Sort by build number descending; take the highest
                matches.sort(key=lambda t: int(t[1]), reverse=True)
                path, build_num = matches[0]
                url = f"https://builds.bepinex.dev{path}"
                self._log(f"  Found build #{build_num} on builds.bepinex.dev")
                return url
            self._log("  No matching asset on build server — using fallback URL.")
        except Exception as exc:
            self._log(f"  Build server unavailable ({exc}) — using fallback URL.")

        return BEPINEX_FALLBACK_URL

    def _download(self, url: str, dest: Path, max_pct: float = 60.0) -> None:
        """Download *url* to *dest*, reporting progress up to *max_pct* percent."""
        req = urllib.request.Request(
            url,
            headers={"User-Agent": f"SledCoopMod-Installer/{APP_VERSION}"},
        )
        with urllib.request.urlopen(req, timeout=120) as resp, \
                open(dest, "wb") as fh:
            total = int(resp.headers.get("Content-Length", 0))
            done  = 0
            while True:
                chunk = resp.read(65536)
                if not chunk:
                    break
                fh.write(chunk)
                done += len(chunk)
                if total:
                    mb_done  = done  / 1_048_576
                    mb_total = total / 1_048_576
                    self._progress(
                        done / total * max_pct,
                        f"Downloading… {mb_done:.1f} / {mb_total:.1f} MB",
                    )

        self._log(f"  Downloaded {dest.name}  ({done // 1024} KB)")


# ── Entry point ────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    app = InstallerApp()
    app.mainloop()
