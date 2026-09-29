# 插件市场数据（market/）

这个目录就是「插件市场」的全部内容 —— **没有服务器**，市场就是你的 GitHub 仓库。

```
market/
  index.json                插件清单（本文件所在目录是"相对下载地址"的基准）
  packages/                 插件包，一个插件只留最新那一个 zip
    goldiamond.autogg-1.1.0.zip
```

## 加一个新插件 / 更新一个插件（3 步）

1. **把 zip 放进 `market/packages/`**
   - 命名建议：`<插件id>-<版本>.zip`，例如 `goldiamond.autogg-1.1.0.zip`（一眼看出装了哪版）
   - zip 结构：里面套一层 id 目录（`goldiamond.autogg/插件.dll` + `goldiamond.autogg/plugin.json`），
     或者文件直接放根目录也行，两种宿主都认
   - **更新插件时，把旧版本的 zip 删掉**（同一个 id 留多个也行，脚本会自动取版本号最高的那个并提示，但仓库会越堆越大）
   - 让 `plugin.json` 里的 `version` 和文件名对得上，不然以后自己会搞混
2. **重新生成清单**

   ```
   tools\rebuild-market-index.bat                            只扫本地 packages\
   tools\rebuild-market-index.bat -FromRepo                  从 GitHub 仓库扫（网页上传的 zip 用这个）
   tools\rebuild-market-index.bat -FromRepo -SyncPackages     顺便把 zip 存回本地
   ```

   脚本做三件事：读每个 zip 里的 `plugin.json` → 算 SHA256 和大小 → 生成 `index.json`；
   同一个 id 有多个版本时只保留版本号最高的。
3. **`push.bat` 推上去**

软件里【插件】→【插件市场】→【刷新】就能看到新版（客户端请求时带了时间戳，不受 CDN 缓存影响）。

## index.json 字段

| 字段 | 说明 |
|---|---|
| `schemaVersion` | 清单格式版本，现在是 1 |
| `updatedAt` | 生成时间（脚本自动写） |
| `plugins[]` | 插件条目数组 |
| └ `id` | 插件 id，必须和 zip 里 `plugin.json` 的 id 一致（用它判断装没装） |
| └ `name` / `author` / `description` | 界面显示用，来自 plugin.json |
| └ `version` | 版本号，**以 plugin.json 为准**。客户端拿它和已装版本比：新版显示「更新」，旧版显示「已装最新」 |
| └ `apiVersion` | 要求的契约版本；和软件里的契约不一致就不给装 |
| └ `capabilities` | 能力列表，安装确认框里给用户看 |
| └ `downloadUrl` | **写相对路径**（例如 `packages/xxx.zip`），换镜像 / 换分支 / 换仓库都不用改清单 |
| └ `sha256` / `size` | 下载后校验；对不上直接丢弃并提示 |
| └ `tags` | 搜索用 |

## 客户端怎么用

- 默认清单地址（jsDelivr 镜像，国内一般能通）：
  `https://cdn.jsdelivr.net/gh/goldiamond1031/MinecraftChatOverlay@main/market/index.json`
- 拿不到就自动试 GitHub 官方 raw：
  `https://raw.githubusercontent.com/goldiamond1031/MinecraftChatOverlay/main/market/index.json`
- 两个地址都能在软件里改（插件市场页底部）；换镜像 / 换分支 / 换仓库只改那儿。
- **别用 `api.github.com` 当客户端的源**：未登录只有 60 次/小时。上面两个是静态 CDN，随便拉。

## 提醒

插件包在本程序里运行，权限和本程序一样。清单里列出来 = 让用户下载并运行它，所以 `packages\` 里的东西请自己审过再放。