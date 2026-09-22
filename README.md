# Token Details

ZCode Token 用量监控工具（Windows 桌面托盘程序）。读取 ZCode 本地数据库
（`~/.zcode/cli/db/db.sqlite` 的 `model_usage` 表），以悬浮面板 + 详情窗口展示
token 的速度与总量统计。

## 下载安装（普通用户）

不用装 Python，直接下载打包好的 exe：

1. 打开 [Releases 页面](https://github.com/1234fsdc/token-details/releases)
2. 找到最新版本，下载 `TokenDetails.exe`
3. 双击运行，托盘区出现图标，悬停即可看到速度面板，双击图标打开详情窗口

前提：本机已安装并使用过 [ZCode](https://zcode.com/)，数据库文件存在。

## 从源码运行

```bash
python token_speed.py              # 图形窗口（悬浮面板 + 详情三页）
python token_speed.py --once       # 打印一帧数据后退出
python token_speed.py --self-check # 跑 tps 计算与三页渲染断言
```

依赖：`pystray`、`webview`（打包命令见 `build.bat`）。

## 打包

双击 `build.bat`，产物为 `dist/TokenDetails.exe`（PyInstaller 单文件）。
