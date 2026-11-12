@echo off
chcp 65001 >nul
title Compilation du Compteur de coffres
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Le SDK .NET 10 n'est pas installe.
  echo Telecharge-le ici : https://dotnet.microsoft.com/download/dotnet/10.0  ^(".NET SDK", Windows x64^)
  pause
  exit /b 1
)
echo Compilation en cours (1 a 2 minutes la premiere fois)...
dotnet publish src\CompteurCoffres\CompteurCoffres.csproj -c Release -o dist
if errorlevel 1 (
  echo.
  echo La compilation a echoue : voir le message ci-dessus.
  pause
  exit /b 1
)
move /y dist\CompteurCoffres.ReplayReader.exe dist\CompteurCoffres.exe >nul
echo.
echo Fini : dist\CompteurCoffres.exe
pause
