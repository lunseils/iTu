@echo off
rem 一键发布：两份产物 + 桌面快捷方式。
rem 桌面 iTu 图标固定指向 dist\iTu-standalone\iTu.exe，每次发布覆盖同名文件后
rem 双击桌面图标就是当前构建；再调 DesktopLink 刷新版本备注与图标缓存。
rem
rem 用法：publish.cmd                    发布，结束后停一下让窗口留着
rem       publish.cmd --no-pause         发布后直接退出（脚本调用用这个）
rem       publish.cmd --force            发布前先结束正在运行的 iTu
rem
rem 注意：本文件必须按 GBK + CRLF 保存。cmd 用系统代码页逐字节解析批处理，
rem UTF-8 或 LF 换行都会让中文字节被当成命令切断。

setlocal
cd /d "%~dp0"

rem dotnet CLI 的中文输出是 UTF-8，在 GBK 控制台里会乱码；让它说英文，本脚本自己的提示仍是中文
set "DOTNET_CLI_UI_LANGUAGE=en"

set "NOPAUSE="
set "FORCE="
:parse
if "%~1"=="" goto :parsed
if /i "%~1"=="--no-pause" set "NOPAUSE=1"
if /i "%~1"=="--force" set "FORCE=1"
shift
goto :parse
:parsed

set "DIST=%~dp0dist"
set "STANDALONE=%DIST%\iTu-standalone\iTu.exe"
set "LINKER=%~dp0..\desktoplink\bin\Release\net10.0-windows\DesktopLink.exe"

echo [1/6] 检查是否已有 iTu 在运行
tasklist /fi "imagename eq iTu.exe" 2>nul | "%SystemRoot%\System32\findstr.exe" /i /c:"iTu.exe" >nul
if not errorlevel 1 (
  if defined FORCE (
    echo       正在结束运行中的 iTu ...
    taskkill /f /im iTu.exe >nul 2>&1
    timeout /t 2 /nobreak >nul
  ) else (
    echo.
    echo [中止] 检测到 iTu 正在运行，它锁着 dist 里的 exe，发布必然失败。
    echo        请右键托盘图标退出后重跑本脚本；或加 --force 自动结束
    echo        （正在进行的截图/录制会中断）。
    if not defined NOPAUSE pause
    exit /b 1
  )
)

echo [2/6] 清理旧产物
if exist "%DIST%\iTu" rmdir /s /q "%DIST%\iTu"
if exist "%DIST%\iTu-standalone" rmdir /s /q "%DIST%\iTu-standalone"

echo [3/6] 发布轻量版（框架依赖，需系统已装 .NET 10 Desktop Runtime）
dotnet publish iTu.csproj -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -p:DebugType=none -o "%DIST%\iTu"
if errorlevel 1 goto :fail

echo [4/6] 发布独立版（自包含压缩单文件）
dotnet publish iTu.csproj -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=none -o "%DIST%\iTu-standalone"
if errorlevel 1 goto :fail

echo [5/6] 构建桌面快捷方式工具
dotnet build "%~dp0..\desktoplink\desktoplink.csproj" -c Release -v q
if errorlevel 1 goto :fail

echo [6/6] 刷新桌面快捷方式
"%LINKER%" "%STANDALONE%" iTu "截图 / 录制 / 识图"
if errorlevel 1 goto :fail

echo.
echo 产物：
for %%F in ("%DIST%\iTu\iTu.exe" "%STANDALONE%") do @echo   %%~zF 字节  %%~fF
echo.
echo 完成。桌面上的 iTu 图标已指向本次构建。
if not defined NOPAUSE pause
exit /b 0

:fail
echo.
echo [失败] 上面有报错，产物可能不完整。
if not defined NOPAUSE pause
exit /b 1
