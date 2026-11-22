#!/usr/bin/env bash
# 在 WSL(Linux) 里就地编译发布 Linux AOT 版本，产物直接落在仓库的
# FileShare.Desktop/bin/Release/net10.0/publish/linux-x64/ ——与 Windows 的
# publish/win-x64、win-x86 并列，VS Code 里一眼就能看到。
#
# 设计要点：
#   - 直接用 /mnt/d 的挂载路径编译，不把项目复制进 WSL。
#   - 发布参数与 FileShare.Desktop/Properties/PublishProfiles/linuxAot.txt 一致。
#   - 输出目录先清空再发布，避免和以前非 AOT 的 pdb 混在一起。
#   - 发布完只把可执行文件拷到 WSL 用户目录 ~/fileshare-test 并启动，
#     不拷其他发布文件（原生库由程序自己解压到数据目录）。
#
# 用法（在 WSL 里）：
#   bash tools/publish-linux-aot.sh            # 编译 + 拷贝 + 启动 GUI（后台）
#   bash tools/publish-linux-aot.sh --smoke    # 编译 + 拷贝 + 自检（渲染后自动退出）
#
# 这条流程实测可跑通，绕过了三个坑（都是「只读绕过」，不改仓库也不改环境）：
#   1) 仓库的 global.json 钉了 SDK 10.0.402，而 WSL 里可能只有 10.0.401；
#      SDK 是按**当前工作目录**向上找 global.json 的，所以从 /tmp 发起发布即可。
#   2) nuget.config 里的本地源写的是 Windows 路径 D:\StudyNode\nuget-local，
#      在 Linux 下会被拼成非法路径，用 /p:RestoreSources 覆盖成挂载路径。
#   3) Windows 生成的 obj/ 里记了 VS 的 fallback 包目录（D:\Program Files...），
#      Linux 复用会直接报错；用 --artifacts-path 把中间产物全部丢到 /tmp 隔离。
#      （-o 只决定最终产物落点，中间产物仍然隔离在 /tmp。）

set -euo pipefail

PROJECT="/mnt/d/StudyNode/localsend/FileShare.Desktop/FileShare.Desktop.csproj"
LOCAL_FEED="/mnt/d/StudyNode/nuget-local"
ARTIFACTS="/tmp/fs-artifacts"
OUT_DIR="/mnt/d/StudyNode/localsend/FileShare.Desktop/bin/Release/net10.0/publish/linux-x64"
DEST="$HOME/fileshare-test"
RUNLOG="/tmp/fileshare-run.log"

DOTNET="${DOTNET_ROOT:-$HOME/.dotnet}/dotnet"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
[ -x "$DOTNET" ] || { echo "找不到 dotnet（试过 \$HOME/.dotnet 和 PATH）"; exit 1; }

echo "==> dotnet: $DOTNET ($("$DOTNET" --version))"
echo "==> 输出目录: $OUT_DIR"

echo "==> 发布 AOT（中间产物隔离到 $ARTIFACTS）"
rm -rf "$ARTIFACTS"
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"
cd /tmp
"$DOTNET" publish "$PROJECT" \
    -c Release -r linux-x64 --self-contained true \
    -f net10.0 \
    /p:PublishAot=true \
    /p:PublishReadyToRun=true \
    /p:DebugType=None \
    /p:DebugSymbols=false \
    /p:RestoreSources="$LOCAL_FEED;https://api.nuget.org/v3/index.json" \
    --artifacts-path "$ARTIFACTS" \
    -o "$OUT_DIR"

EXE="$OUT_DIR/FileShare.Desktop"
[ -f "$EXE" ] || { echo "找不到产物 $EXE"; exit 1; }
chmod +x "$EXE"
echo "==> 产物:"
file "$EXE" | head -1
ls -lh "$OUT_DIR"

# 只把可执行文件拷到用户目录：发布目录保持原样（不删不改）。
# 这样同时也验证了「exe 同级没有 .so 也能跑」——原生库由程序自己解压到数据目录。
echo "==> 清空 $DEST 并拷入可执行文件（只拷单个文件）"
rm -rf "$DEST"
mkdir -p "$DEST"
cp "$EXE" "$DEST/"
chmod +x "$DEST/FileShare.Desktop"
ls -lh "$DEST/FileShare.Desktop"

cd "$DEST"
if [ -n "${DISPLAY:-}" ]; then
    if [ "${1:-}" = "--smoke" ]; then
        timeout 60 ./FileShare.Desktop --smoke || echo "（退出码 $?）"
    else
        echo "==> 启动应用（后台运行，关闭窗口即退出）"
        # 先关掉上一次跑起来的实例，避免叠加出一堆窗口
        pkill -f "$DEST/FileShare.Desktop" 2>/dev/null || true
        nohup ./FileShare.Desktop >"$RUNLOG" 2>&1 &
        APP_PID=$!
        sleep 3
        if kill -0 "$APP_PID" 2>/dev/null; then
            echo "==> 已启动：PID=$APP_PID  路径=$DEST/FileShare.Desktop  日志=$RUNLOG"
        else
            echo "!! 启动失败，日志如下："
            cat "$RUNLOG" 2>/dev/null || true
            exit 1
        fi
    fi
else
    echo "没有 DISPLAY，跳过运行（需要有 Linux 桌面环境或 WSLg）"
fi

echo
# 原生库与日志都在应用数据目录（Linux 是 ~/.config/<应用名>）
DATA_DIR="$HOME/.config/FileShare.Desktop"
NATIVE_DIR="$DATA_DIR/native"
echo "==> 数据目录 $DATA_DIR"
ls -A "$DATA_DIR" 2>/dev/null || echo "  (还没有数据目录)"
echo
echo "==> 原生库目录 $NATIVE_DIR"
ls -l "$NATIVE_DIR" 2>/dev/null || echo "  !! 没有 native/ 目录"
echo
echo "==> load_log.txt"
cat "$DATA_DIR/load_log.txt" 2>/dev/null || echo "  !! 没有 load_log.txt"
