# jellyfin-plugin-metashark

[![metashark](https://img.shields.io/github/v/release/cxfksword/jellyfin-plugin-metashark)](https://github.com/cxfksword/jellyfin-plugin-metashark/releases)
[![metashark](https://img.shields.io/badge/jellyfin-10.8.x-lightgrey)](https://github.com/cxfksword/jellyfin-plugin-metashark/releases)
[![metashark](https://img.shields.io/github/license/cxfksword/jellyfin-plugin-metashark)](https://github.com/cxfksword/jellyfin-plugin-metashark/main/LICENSE) 

jellyfin电影元数据插件，影片信息只要从豆瓣获取，并由TheMovieDb补充缺失的季数据和剧集数据。

![preview](doc/logo.png)

## 安装插件

只支持最新的`jellyfin 10.8.x`版本

添加插件存储库：

国内加速：https://ghproxy.com/https://github.com/cxfksword/jellyfin-plugin-metashark/releases/download/manifest/manifest_cn.json

国外访问：https://github.com/cxfksword/jellyfin-plugin-metashark/releases/download/manifest/manifest.json

## 如何使用

* 进入`控制台 -> 媒体库`，在元数据下载器中勾选**MetaShark**
* 假如网络原因访问TheMovieDb比较慢，也可以到插件配置页关闭从TheMovieDb获取数据
  

## How to build

1. Clone or download this repository

2. Ensure you have .NET Core SDK setup and installed

3. Build plugin with following command.

```sh
$ dotnet restore 
$ dotnet publish Jellyfin.Plugin.MetaShark/Jellyfin.Plugin.MetaShark.csproj
```


## How to test

1. Build the plugin

2. Create a folder, like `metashark` and copy  `bin/Debug/net6.0/*.dll` into it

3. Move folder `metashark` to jellyfin `data/plugin` folder

