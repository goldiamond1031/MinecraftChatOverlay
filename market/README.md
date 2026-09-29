# 插件市场数据（market/）

这个目录就是「插件市场」的全部内容 —— **没有服务器**，市场就是你的 GitHub 仓库。

```
market/
  index.json          插件清单（本文件所在目录是相对下载地址的基准）
  packages/           插件包，一个插件一个 zip
    AutoGG-1.1.0.zip
```

## 加 / 更新一个插件

1. 把插件 zip 放进 `market/packages/`（网页上传、或者本地放进去都行）
2. 跑一次重建脚本，让它重新算清单：

   ```
   tools\rebuild-market-index.bat                       只扫本地 packages\
   tools\rebuild-market-index.bat -FromRepo             从 GitHub 仓库扫（网页上传的用这个）
   tools\rebuild-market-index.bat -FromRepo -SyncPackages   顺便把 zip 存回本地
   ```

   脚本会：读每个 zip 里的 `plugin.json` → 算 SHA256 → 生成 `index.json`
3. `push.bat` 把改动推上去

软件里「插件 → 插件市场 → 刷新」就能看到（CDN 有几分钟缓存，刚推完可能要多刷一次）。

## index.json 字段

| 字段 | 说明 |
|---|---|
| `schemaVersion` | 清单格式版本，现在是 1 |
| `updatedAt` | 生成时间（脚本自动写） |
| `plugins[]` | 插件条目数组 |
| └ `id` | 插件 id，必须和 zip 里 `plugin.json` 的 id 一致（用它判断装没装） |
| └ `name` / `author` / `description` | 界面显示用，来自 plugin.json |
| └ `version` | 版本号，**以 plugin.json 为准**（客户端拿它和已装版本比，决定显示「安装」还是「更新」） |
| └ `apiVersion` | 要求的契约版本；和软件里的契约不一致就不给装 |
| └ `capabilities` | 能力列表，安装确认框里给用户看 |
| └ `downloadUrl` | **写相对路径**（例如 `packages/xxx.zip`）。这样换镜像 / 换分支 / 换仓库都不用改清单 |
| └ `sha256` / `size` | 下载后校验；对不上直接丢弃 |
| └ `tags` | 搜索用 |

## 客户端怎么用

- 默认清单地址（jsDelivr 镜像，国内一般能通）：
  `https://cdn.jsdelivr.net/gh/goldiamond1031/MinecraftChatOverlay@main/market/index.json`
- 拿不到就自动试 GitHub 官方 raw：
  `https://raw.githubusercontent.com/goldiamond1031/MinecraftChatOverlay/main/market/index.json`
- 两个地址都能在软件里改（插件市场页底部），换镜像 / 换分支 / 换仓库只改那儿。
- **别用 `api.github.com` 当客户端的源**：未登录只有 60 次/小时。上面两个是静态 CDN，随便拉。

## 提醒

插件包在本程序里运行，权限和本程序一样。清单里列出来等于让用户下载并运行它，所以 `packages/` 里的东西请自己审过再放。