# Ubuntu 18.04 编译 AOT 步骤

## 一、安装 .NET 10 SDK

1. 下载 Microsoft 包存储库配置文件：
   ```bash
   wget https://packages.microsoft.com/config/ubuntu/18.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
   ```

2. 安装配置文件：
   ```bash
   sudo dpkg -i packages-microsoft-prod.deb
   ```

3. 更新包列表：
   ```bash
   sudo apt-get update
   ```

4. 下载 .NET 安装脚本：
   ```bash
   wget https://dot.net/v1/dotnet-install.sh
   ```

5. 赋予脚本执行权限：
   ```bash
   chmod +x dotnet-install.sh
   ```

6. 执行安装脚本，安装 .NET 10.0：
   ```bash
   ./dotnet-install.sh --channel 10.0
   ```

7. 添加 .NET 环境变量到 .bashrc：
   ```bash
   echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.bashrc
   ```

7. 添加 .NET 到 PATH 环境变量：
   ```bash
   echo 'export PATH=$PATH:$HOME/.dotnet' >> ~/.bashrc
   ```

8. 重新加载 .bashrc 使环境变量生效：
   ```bash
   source ~/.bashrc
   ```

9. 验证 .NET 安装是否成功：
   ```bash
   dotnet --info
   ```

## 二、安装 AOT 编译所需本地 C/C++ 编译工具链

安装构建工具、Clang 和 zlib 开发库：

```bash
sudo apt install -y build-essential clang zlib1g-dev
```

## 三、编译项目

进入项目目录后执行以下命令：

```bash
dotnet publish -c Release -r linux-x64 \
   --self-contained true \
   /p:PublishAot=true \
   /p:PublishReadyToRun=true \
   /p:DebugType=None \
   /p:DebugSymbols=false \
   -o "bin/Release/net10.0/publish/linux-x64/"
```

## 说明

- **AOT 编译**：提前编译 (Ahead-of-Time) 可以显著提高应用启动速度和减少内存占用。
- **自包含部署**：`--self-contained true` 选项会将 .NET 运行时一起打包，使应用可以在没有安装 .NET 的系统上运行。
- **ReadyToRun**：`/p:PublishReadyToRun=true` 选项会生成预先编译的代码，进一步提高启动性能。
- **调试信息**：禁用调试信息可以减小输出文件大小。

编译完成后，可执行文件将位于 `bin/Release/net10.0/publish/linux-x64/` 目录中。