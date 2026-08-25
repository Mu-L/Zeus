# Zeus.Templates

模板源码里的 `template.json` 会保留版本占位符，桌面和 Modbus 模板的 `zeus.json` 也由共享文件在打包阶段复制。

调试模板时不要直接安装 `templates` 源码目录，先打包再安装 nupkg：

```bash
dotnet pack Zeus.sln -c Release -o artifacts/nuget
dotnet nuget add source "$PWD\artifacts\nuget" --name ZeusLocal
dotnet pack src/Zeus.Templates/Zeus.Templates.csproj -c Release -o artifacts/template-smoke
dotnet new uninstall Zeus.Templates
dotnet new install artifacts/template-smoke/Zeus.Templates.0.20.0.nupkg
```
