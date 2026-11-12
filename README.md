# Snipaste 截图识字

轻量的 Windows 截图识字与翻译助手。它读取系统剪贴板中的图片，可搭配 Snipaste、Windows 截图工具等使用。识别结果可编辑、复制，也可选中其中一段再翻译。

## 下载和使用

适用于 64 位 Windows 10 或 11。从 [版本下载](https://github.com/rejoice-zx/snipaste-ocr/releases/latest) 获取 Windows ZIP，解压后双击 `Start-SnipasteOcr.cmd` 启动托盘。

1. 框选截图，把图片复制到剪贴板。
2. 按 `Ctrl+Alt+T` 打开识字窗口。
3. 按需编辑原文，或点「翻译」查看译文。

从窗口或托盘菜单打开「设置」，可以修改识字快捷键、翻译语种和接口。

## 从源码构建

在仓库目录打开 PowerShell。构建脚本使用系统的 .NET Framework 编译器。

```powershell
powershell.exe -NoProfile -File .\build.ps1
.\SnipasteOcr.exe --hotkey
```

## 识字和翻译接口

- 识字默认使用 Windows 本机 OCR，支持自动识别中英文。也可在设置里接入支持图片输入的兼容视觉模型。
- 翻译默认使用 MyMemory，默认中英互翻。可以手动指定语种，或接入兼容的文本模型。普通文本模型使用内置提示词处理 OCR 误断行和拆词。
- 模型接口可填写 API 基地址或完整的 `chat/completions` 地址。服务提供 `/models` 时可从设置中获取模型列表，也可手填模型名。

使用视觉模型识字时会发送截图。使用翻译接口时，点击「翻译」才会发送原文。接口配置保存在本机的 `settings.json`，API 密钥使用当前 Windows 用户的凭据加密。
