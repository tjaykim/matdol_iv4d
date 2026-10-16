@echo off
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
