# -*- coding: utf-8 -*-
"""一体化把手验证：把手热区可交互可拖动整面板，离开热区恢复穿透。"""
import ctypes, os, subprocess, sys, time

APP = os.path.join(os.path.dirname(os.path.abspath(__file__)), "token_speed.py")
TITLE = "Token Details"  # 与 token_speed.py 的 TITLE 保持一致（FindWindowW 锚点）

# 应用进程是 DPI-aware（真实坐标），测试必须同空间，否则 SetCursorPos 差一倍
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    ctypes.windll.user32.SetProcessDPIAware()

u32 = ctypes.WinDLL("user32", use_last_error=True)

class RECT(ctypes.Structure):
    _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long),
                ("r", ctypes.c_long), ("b", ctypes.c_long)]

def main_rect_ex():
    mh = u32.FindWindowW(None, TITLE)
    assert mh, "main window missing"
    rc = RECT()
    u32.GetWindowRect(mh, ctypes.byref(rc))
    return rc, u32.GetWindowLongW(mh, -20) & 0xFFFFFFFF

def move_to(x, y):
    u32.SetCursorPos(int(x), int(y))
    u32.mouse_event(0x0001, 0, 0, 0, 0)

p = subprocess.Popen([sys.executable, APP])
time.sleep(7)
rc0, ex0 = main_rect_ex()
assert ex0 & 0x20, "default must be click-through"

# 悬停把手热区（左上角 42x20 CSS 内）→ 穿透应解除
dpi = u32.GetDpiForWindow(u32.FindWindowW(None, TITLE)) or 96
gx, gy = rc0.l + round(20 * dpi / 96), rc0.t + round(10 * dpi / 96)
move_to(gx, gy); time.sleep(0.6)
_, ex1 = main_rect_ex()
assert not (ex1 & 0x20), f"grip hotspot did not unlock (ex=0x{ex1:08X})"

# 按住拖动 → 面板整体移动
u32.mouse_event(0x0002, 0, 0, 0, 0)     # LEFTDOWN
for i in range(1, 11):                  # +100,+60 分步
    move_to(gx + 10 * i, gy + 6 * i)
    time.sleep(0.05)
u32.mouse_event(0x0004, 0, 0, 0, 0)     # LEFTUP
time.sleep(0.5)
rc1, _ = main_rect_ex()
dx, dy = rc1.l - rc0.l, rc1.t - rc0.t
print(f"panel followed=({dx},{dy})")

# 鼠标移到面板中部 → 穿透恢复
move_to(rc1.l + (rc1.r - rc1.l) // 2, rc1.t + (rc1.b - rc1.t) // 2)
time.sleep(0.6)
_, ex2 = main_rect_ex()
print(f"ex grip=0x{ex1:08X}(unlocked) mid=0x{ex2:08X} moved=({dx},{dy})")
assert abs(dx - 100) <= 15 and abs(dy - 60) <= 15, "panel did not follow drag"
assert ex2 & 0x20, "click-through not restored off-grip"
move_to(rc1.r + 200, rc1.b + 200)
p.kill()
print("PASS: grip hotspot drags whole panel; off-grip stays click-through")
