# CorineKit Fetcher

![CorineKit Fetcher](Fetcher/FetcherIcon.ico)

## 简介

CorineKit Fetcher 是一款基于 WPF 的图片抽选工具，专门用于随机浏览文件夹中的图片。支持多种图片格式，包括 JPG、PNG、BMP、GIF 等。

## 功能特性

- **随机抽选**: 按空格键随机抽选图片
- **图片缓存**: 可调节缓存大小，快速切换已浏览图片
- **多语言支持**: 中文、英文、日文三种语言
- **主题切换**: 6种预设主题背景（白色、深色、深紫色、深蓝色、军绿色、深棕色）
- **路径导航**: 快速导航到任意父级目录
- **键盘快捷键**:
  - ←/→ 方向键：前后切换缓存图片
  - ↑/↓ 方向键：按顺序切换图片
  - 空格键：随机抽选图片
  - Tab 键：显示/隐藏设置面板
  - 双击/中键：打开当前图片
- **会话恢复**: 关闭后自动记住上次浏览的文件夹
- **拖放支持**: 直接将文件夹拖入窗口打开

## 下载安装

### 最新版本

[![GitHub Release](https://img.shields.io/github/v/release/billye220670/CorineKit_Fetcher?label=Release)](https://github.com/billye220670/CorineKit_Fetcher/releases/latest)

下载exe文件：`Fetcher.exe`

### 系统要求

- Windows 10/11
- .NET 9.0 Windows Runtime

## 使用方法

1. 启动程序后，将包含图片的文件夹拖入窗口
2. 使用鼠标滚轮或方向键切换图片
3. 按空格键随机抽选新图片
4. 按 Tab 键打开设置面板，可调节：
   - 缓存页数
   - 背景颜色主题
   - 界面语言

## 支持的图片格式

- JPG/JPEG
- PNG
- BMP
- GIF

## 项目结构

```
CorineKit_Fetcher/
├── Fetcher.sln           # 解决方案文件
├── Fetcher/
│   ├── Fetcher.csproj    # 项目文件
│   ├── App.xaml          # 应用入口
│   ├── App.xaml.cs
│   ├── MainWindow.xaml   # 主窗口
│   ├── MainWindow.xaml.cs
│   ├── Settings.cs       # 设置管理
│   ├── LanguageResources.cs # 多语言资源
│   └── FetcherIcon.ico   # 程序图标
└── README.md
```

## 技术栈

- C# 9.0
- .NET 9.0 Windows
- WPF (Windows Presentation Foundation)

## 版本历史

### v1.0.2 (2026-02-03)

- 修复顺序浏览功能（上下方向键）导航卡死的问题
- 修复目录边界循环导航不正确的问题
- 改进缓存导航和顺序导航的索引同步
- 添加 CLAUDE.md 为未来 Claude Code 会话提供指导

### v1.0.1 (2026-02-03)

- 修复设置无法可靠保存的问题（添加备份机制）
- 更改设置时立即保存（缓存大小、上次路径）
- 添加错误日志以便调试保存/加载问题
- 添加 AGENTS.md 开发指南

### v1.0.0 (2026-01-31)

- 首次发布
- 基础图片浏览功能
- 多语言界面支持
- 主题切换功能
- 缓存机制

## 许可证

本项目采用 MIT 许可证开源。

## 作者

[billye220670](https://github.com/billye220670)

## 问题反馈

如有问题或建议，请访问 [GitHub Issues](https://github.com/billye220670/CorineKit_Fetcher/issues) 页面提交。
