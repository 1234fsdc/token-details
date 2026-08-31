# -*- coding: utf-8 -*-
"""端到端拖拽验证：解锁穿透→按住面板拖动→窗口位置必须变化。"""
import ctypes, subprocess, sys, time

u32 = ctypes.WinDLL("user32", use_last_error=True)

class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]

class RECT(ctypes.Structure):
    _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long),
                ("r", ctypes.c_long), ("b", ctypes.c_long)]

def rect():
    h = u32.FindWindowW(None, "ZCode Token 速度")
    assert h, "window not found"
    rc = RECT()
    u32.GetWindowRect(h, ctypes.byref(rc))
    return rc

def send_hotkey():
    for k in (0x11, 0x12, 0x54):
        u32.keybd_event(k, 0, 0, 0)
    for k in (0x54, 0x12, 0x11):
        u32.keybd_event(k, 0, 2, 0)

def move_to(x, y):
    # mouse_event 相对模式更简单：直接 SetCursorPos + MOVE 事件
    u32.SetCursorPos(int(x), int(y))
    u32.mouse_event(0x0001, 0, 0, 0, 0)  # MOVE 让系统刷新

p = subprocess.Popen([sys.executable, r"C:\Users\木\Desktop\talk\token_speed.py"])
time.sleep(6)
rc0 = rect()
cx, cy = (rc0.l + rc0.r) // 2, (rc0.t + rc0.b) // 2

send_hotkey(); time.sleep(0.8)          # 解锁穿透
ex = u32.GetWindowLongW(u32.FindWindowW(None, "ZCode Token 速度"), -20) & 0xFFFFFFFF
assert not (ex & 0x20), "unlock failed"

move_to(cx, cy); time.sleep(0.3)
u32.mouse_event(0x0002, 0, 0, 0, 0)     # LEFTDOWN
for i in range(1, 11):                  # 分 10 步移动 +120,+80
    move_to(cx + 12 * i, cy + 8 * i)
    time.sleep(0.05)
u32.mouse_event(0x0004, 0, 0, 0, 0)     # LEFTUP
time.sleep(0.5)

rc1 = rect()
dx, dy = rc1.l - rc0.l, rc1.t - rc0.t
print(f"before=({rc0.l},{rc0.t}) after=({rc1.l},{rc1.t}) moved=({dx},{dy})")
assert abs(dx - 120) <= 15 and abs(dy - 80) <= 15, "drag failed"

send_hotkey(); time.sleep(0.8)          # 恢复穿透
ex2 = u32.GetWindowLongW(u32.FindWindowW(None, "ZCode Token 速度"), -20) & 0xFFFFFFFF
assert ex2 & 0x20, "re-lock failed"
p.kill()
print("PASS: drag moves window and click-through restores")
