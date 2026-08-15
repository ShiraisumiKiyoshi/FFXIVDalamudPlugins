@echo off
chcp 65001 >nul
title FreeAction 插件仓库服务器 (端口 8848)

echo ============================================
echo   FreeAction 插件仓库本地服务器
echo   端口: 8848
echo   仓库地址: http://localhost:8848/repo.json
echo ============================================
echo.
echo 请在卫月设置中添加以下仓库地址:
echo   http://localhost:8848/repo.json
echo.
echo 按 Ctrl+C 停止服务器
echo.

cd /d "%~dp0repo"

:: 使用 PowerShell 启动 HTTP 服务器（无需 Python）
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$listener = New-Object System.Net.HttpListener; ^
   $listener.Prefixes.Add('http://localhost:8848/'); ^
   $listener.Start(); ^
   Write-Host '服务器已启动: http://localhost:8848/'; ^
   Write-Host '按 Ctrl+C 停止'; ^
   while ($listener.IsListening) { ^
     $context = $listener.GetContext(); ^
     $req = $context.Request; ^
     $res = $context.Response; ^
     $path = $req.Url.LocalPath; ^
     if ($path -eq '/') { $path = '/repo.json' }; ^
     $filePath = '.' + $path; ^
     $filePath = $filePath -replace '/','\'; ^
     Write-Host $req.HttpMethod $req.Url.LocalPath; ^
     if (Test-Path $filePath -PathType Leaf) { ^
       $bytes = [System.IO.File]::ReadAllBytes($filePath); ^
       if ($filePath -like '*.json') { $res.ContentType = 'application/json' } ^
       elseif ($filePath -like '*.zip') { $res.ContentType = 'application/zip' } ^
       elseif ($filePath -like '*.png') { $res.ContentType = 'image/png' } ^
       else { $res.ContentType = 'application/octet-stream' }; ^
       $res.ContentLength64 = $bytes.Length; ^
       $res.OutputStream.Write($bytes, 0, $bytes.Length) ^
     } else { ^
       $res.StatusCode = 404; ^
       $msg = [System.Text.Encoding]::UTF8.GetBytes('404 Not Found: ' + $path); ^
       $res.OutputStream.Write($msg, 0, $msg.Length) ^
     }; ^
     $res.Close() ^
   }"
