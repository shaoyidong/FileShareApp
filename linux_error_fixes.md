# Linux 常见错误及解决方案

本文档记录了在 Linux 系统上运行应用程序时可能遇到的常见错误及其解决方案。

## 1. ICU 包缺失错误

### 错误信息
```
Couldn't find a valid ICU package installed on the system. Please install libicu (or icu-libs) using your package manager and try again.
```

### 解决方案
```bash
# 更新软件包列表
sudo apt update

# 安装 libicu 开发包
sudo apt install libicu-dev
```

## 2. libICE.so.6 库缺失错误

### 错误信息
```
Unhandled exception. System.DllNotFoundException: Unable to load shared library 'libICE.so.6' or one of its dependencies.
```

### 解决方案
```bash
# 更新软件包列表（一个好习惯）
sudo apt update

# 安装缺失的 libICE 库
sudo apt install libice6
```

## 3. libSM.so.6 库缺失错误

### 错误信息
```
Unhandled exception. System.DllNotFoundException: Unable to load shared library 'libSM.so.6' or one of its dependencies.
```

### 解决方案
```bash
# 更新软件包列表
sudo apt update

# 安装缺失的 libSM 库
sudo apt install libsm6
```

## 4. 字体缺失错误

### 错误信息
```
Unhandled exception. System.InvalidOperationException: Could not create glyphTypeface. Font family: $Default (key: ). Style: Normal. Weight: Normal. Stretch: Normal
```

### 解决方案
```bash
# 安装需要的字体
sudo apt install fonts-noto fonts-dejavu

# 更新字体缓存
sudo fc-cache -fv
```

## 总结

以上是在 Linux 系统上运行应用程序时可能遇到的一些常见错误及其解决方案。如果遇到其他错误，请参考相关文档或搜索解决方案。

**注意**：所有命令都需要以管理员权限运行（使用 sudo）。