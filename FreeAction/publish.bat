@echo off
chcp 65001 >nul
title FreeAction 一键打包发布

echo ============================================
echo   FreeAction 一键打包发布
echo ============================================
echo.

:: 编译
echo [1/3] 编译插件...
cd /d "%~dp0"
dotnet build FreeAction.csproj -c Release
if %errorlevel% neq 0 (
    echo.
    echo [错误] 编译失败！
    pause
    exit /b 1
)
echo 编译成功。
echo.

:: 复制到 repo 目录
echo [2/3] 复制到仓库目录...
copy /Y "bin\Release\FreeAction\latest.zip" "repo\FreeAction\latest.zip" >nul
if %errorlevel% neq 0 (
    echo [错误] 复制 latest.zip 失败！
    pause
    exit /b 1
)
echo 复制完成。
echo.

:: 更新 repo.json 时间戳
echo [3/3] 更新仓库时间戳...
powershell -Command "$json = Get-Content 'repo\repo.json' -Raw | ConvertFrom-Json; $json[0].LastUpdated = [int][double]::Parse((Get-Date -UFormat %%s)); $json | ConvertTo-Json -Depth 10 | Set-Content 'repo\repo.json' -Encoding UTF8"
echo 更新完成。
echo.

echo ============================================
echo   打包发布完成！
echo ============================================
echo.
echo 仓库文件位置:
echo   %~dp0repo\repo.json
echo   %~dp0repo\FreeAction\latest.zip
echo   %~dp0repo\FreeAction\icon.png
echo.
echo 如需启动仓库服务器，运行:
echo   start-repo.bat
echo.
echo 在卫月中添加仓库地址:
echo   http://localhost:8848/repo.json
echo.
pause
