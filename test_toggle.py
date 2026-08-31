# -*- coding: utf-8 -*-
"""验证 toggle 后 0x20（穿透位）真的被清除。启动→读样式→注入热键→再读。"""
import ctypes, subprocess, sys, time

u32 = ctypes.WinDLL("user32", use_last_error=True)

def exstyle():
    h = u32.FindWindowW(None, "ZCode Token 速度")
    if not h:
        return None, None
    return h, u32.GetWindowLongW(h, -20) & 0xFFFFFFFF

def send_hotkey():
    VK_CTRL, VK_ALT, VK_T = 0x11, 0x12, 0x54
    for k in (VK_CTRL, VK_ALT, VK_T):
        u32.keybd_event(k, 0, 0, 0)
    for k in (VK_T, VK_ALT, VK_CTRL):
        u32.keybd_event(k, 0, 2, 0)  # KEYEVENTF_KEYUP

p = subprocess.Popen([sys.executable, r"C:\Users\木\Desktop\talk\token_speed.py"])
time.sleep(6)
h, ex = exstyle()
assert h, "window not found"
print(f"initial:  ex=0x{ex:08X}  TRANSPARENT({'SET' if ex & 0x20 else 'clear'})")
send_hotkey()
time.sleep(1.2)
h2, ex2 = exstyle()
print(f"toggle1:  ex=0x{ex2:08X}  TRANSPARENT({'SET' if ex2 & 0x20 else 'clear'})")
assert ex2 is not None and not (ex2 & 0x20), "0x20 NOT cleared — fix failed"
send_hotkey()
time.sleep(1.2)
h3, ex3 = exstyle()
print(f"toggle2:  ex=0x{ex3:08X}  TRANSPARENT({'SET' if ex3 & 0x20 else 'clear'})")
assert ex3 & 0x20, "0x20 NOT re-set"
p.kill()
print("PASS: toggle clears and restores WS_EX_TRANSPARENT")
