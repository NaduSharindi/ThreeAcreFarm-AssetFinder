@echo off
REM Prima Three Acre Farms Asset Finder - local development start-up
cd /d "%~dp0"
echo.
echo   Prima Three Acre Farms - Fixed Asset Finder
echo   Open in browser:  http://localhost:5016
echo   Sign in: admin / Admin@2026   staff / Staff@2026   auditor / Auditor@2026   (Development only)
echo.
dotnet run --project src\PrimaFarm.AssetFinder.Web --launch-profile http
