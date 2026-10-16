@echo off
dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
