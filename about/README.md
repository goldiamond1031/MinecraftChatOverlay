# about/ —— 「关于」页的鸣谢名单

软件左侧导航栏的「关于」页会从这里拉名单。**没有服务器**：这个文件推到 GitHub 仓库的 `about/` 目录，软件直接读它，和插件市场（`market/`）完全是同一套机制。

## 文件

- `about.json` —— 两份名单都在这一个文件里（打赏鸣谢 + 插件开发鸣谢）
- `announcement.json` —— 开屏公告（启动时弹一次的那个窗）

## 开屏公告（announcement.json）

软件每次启动会静默拉一次这个文件，**拉到的东西比上次弹过的更新**就弹一个窗。同一条公告只弹一次 ——
软件把「弹过哪一条」记在本机配置里（字段见下面「软件记在本机的两个字段」）：

- **判新旧比的是 `updatedAt`**：它比上次弹过的那条（`LastSeenAnnouncementAt`）新才弹。
- 公告**没写 `updatedAt`** 时，才退回比 `id`（`LastSeenAnnouncementId`）。
- 所以**改内容时 `updatedAt` 一定要改成当前时间**；`id` 换不换只影响排查，不影响判新旧。

为什么不比 id：id 只能说明「内容换过」，不能说明新旧 —— GitHub 源拉不到、退回还没同步的 jsDelivr 镜像时，
那条的 id 不同但内容更旧，按 id 就会把已经看过的旧公告又弹一遍。

| 字段 | 必填 | 说明 |
|---|---|---|
| `schemaVersion` | 是 | 固定写 `1` |
| `id` | 是 | 公告的唯一标识，改内容就换一个（比如带日期：`2026-10-05-hotfix`）。**判新旧不看它**，只用于排查 |
| `updatedAt` | 是 | 更新时间，ISO 格式。两个源（jsDelivr 镜像 / GitHub 本体）都拉到东西时靠它取更新的那份；**换了内容一定要改成当前时间**（原因见文末「缓存」）。两边时间戳一模一样时，软件会取 GitHub 本体那份（镜像只会更旧），但别指望这个兜底 —— 正常做法就是每改一次改一次时间 |
| `enabled` | 是 | `false` 就等于没公告（内容留着下次改回来） |
| `title` | 是 | 标题，窗里的大字 |
| `body` | 是 | 正文，**要换行就写 `\n`**（JSON 里的转义换行） |
| `link` | 否 | 有链接的话窗里多一个「查看详情」按钮 |
| `linkText` | 否 | 按钮文字，不写就显示「查看详情」 |

不弹了？检查这几条：`enabled` 是不是 `true`、`updatedAt` 有没有改成比上次弹过的时间新、文件有没有推到 `main` 分支、正文 / 标题有没有写。

## 怎么发一条公告（两个工具）

不用手改 JSON：

- `tools\announcement.bat` —— 双击开一个窗口，填标题 / 正文 / 链接，点【提交并推送】。日常发公告用这个。
- `tools\publish-announcement.bat` / `publish-announcement.ps1` —— 命令行版（`.bat` 只是把参数原样转给 `.ps1`）：

  ```
  publish-announcement.bat -Show                       看当前这条是什么
  publish-announcement.bat -Title "更新提示" -Body "更新了 1.2.8" -Link "https://github.com/goldiamond1031/MinecraftChatOverlay/releases" -LinkText "有可用更新"
  publish-announcement.bat -Disable                    停用（内容留着，改天能改回来）
  publish-announcement.bat -Title x -Body y -DryRun    只看它要写的 JSON，不写文件、不推送
  ```

  正文长就写进一个 txt，用 `-BodyFile body.txt`（正文里要换行就直接换行）。

两个工具共用同一套逻辑：`updatedAt` 自动填「现在」、`id` 自动按时间生成、**同时写开发目录和仓库副本两份**
（否则下次 `push.bat` 的 robocopy 会用旧的那份盖回去）、只 add / commit / push `about/announcement.json` 这一个文件。
脚本会自己找 git 仓库：脚本上一级有 `.git` 就用它，否则用 `C:\Github\MinecraftChatOverlay`。

### 软件记在本机的两个字段（判新旧就靠它们）

在 `%AppData%\MinecraftChatOverlay\settings.json` 里：

| 字段 | 作用 |
|---|---|
| `LastSeenAnnouncementAt` | 上次弹过的公告的 `updatedAt`。**判新旧主要比它** |
| `LastSeenAnnouncementId` | 上次弹过的公告的 `id`。只在公告没写 `updatedAt` 时用来兜底 |

想让自己再看一遍某条公告：把 `LastSeenAnnouncementAt` 删掉（或改成很早的时间）再启动软件即可。

## 怎么改名单

1. 打开 `about.json`，按下面的字段表改。
2. 改完**把 `updatedAt` 改成当前时间**（比如 `2026-10-01T12:00:00+08:00`）。这一步不能省，原因见下面「缓存」。
3. commit + push 到 `main` 分支。几十秒后软件里点【刷新名单】就能看到。

就这么多。不需要跑任何脚本（不像市场的 index.json 要 rebuild）。

## 字段表

### 顶层

| 字段 | 必填 | 说明 |
|---|---|---|
| `schemaVersion` | 是 | 固定写 `1` |
| `updatedAt` | 是 | 清单更新时间，ISO 格式。软件靠它判断两份缓存哪份新 |
| `rewards` | 是 | 打赏鸣谢数组，可以为空 `[]` |
| `pluginDevs` | 是 | 插件开发鸣谢数组，可以为空 `[]` |

### rewards 里的一条

| 字段 | 必填 | 说明 |
|---|---|---|
| `name` | 是 | 名字（空名字的条目会被软件忽略） |
| `amount` | 否 | 金额，写成什么样就显示什么样，比如 `"¥20"`、`"¥ 6.66"` |
| `message` | 否 | 留言 |
| `time` | 否 | 时间，随便写，比如 `"2026-09-01"` |

### pluginDevs 里的一条

| 字段 | 必填 | 说明 |
|---|---|---|
| `name` | 是 | 名字（空名字的条目会被软件忽略） |
| `plugin` | 否 | 他做了什么，比如 `"自动 GG 插件"` |
| `link` | 否 | 主页 / 仓库链接，软件里可以点开 |
| `note` | 否 | 备注 |

## 填多个人的例子（照抄这个改就行）

**`time`、`amount`、`message` 都是可选的**，不填就不显示那一截；只有 `name` 必填。

多个人的写法：把每一项用 `{ ... }` 包起来，**项与项之间加逗号**，最后一项后面不加：

```json
{
    "schemaVersion": 1,
    "updatedAt": "2026-10-01T12:00:00+08:00",
    "rewards": [
        {
            "name": "阿伟",
            "amount": "¥50",
            "message": "软件真好用",
            "time": "2026-10-01"
        },
        {
            "name": "某位路过的朋友",
            "amount": "¥20"
        },
        {
            "name": "只留个名字的人"
        }
    ],
    "pluginDevs": [
        {
            "name": "张三",
            "plugin": "自动 GG 插件",
            "link": "https://github.com/zhangsan",
            "note": "第一个第三方插件作者"
        },
        {
            "name": "李四",
            "plugin": "击杀播报美化"
        }
    ]
}
```

几条容易踩的：

- 金额随便写：`¥20`、`20元`、或者干脆不写（不写就不显示金额那一截）
- **名字为空的那一项会被直接忽略**（`"name": ""` 或整条 `null` 都会跳过）
- 数组可以是空的：`"rewards": []`，界面上会显示「名单现在是空的」
- 别在最后一项后面留逗号。留着也不会报错（软件解析很宽容），但严格来说不是合法 JSON，GitHub 之类的地方可能提示格式问题

## 缓存是怎么回事（为什么要改 updatedAt）

软件拉清单时会**同时**请求两个地址，取 `updatedAt` 更新的那份：

- 主源：`cdn.jsdelivr.net/gh/goldiamond1031/MinecraftChatOverlay@main/about/about.json`（jsDelivr 镜像，国内一般能通）
- 备用：`raw.githubusercontent.com/goldiamond1031/MinecraftChatOverlay/main/about/about.json`（GitHub 官方 raw）

jsDelivr 对分支 URL 的缓存可能压很久——你刚推完新名单，主源拿到的可能还是旧文件，而 raw 通常几分钟内就是新的。软件靠 `updatedAt` 判断谁新用谁，所以**每次改完名单记得把 `updatedAt` 改成当前时间**，否则主源上那份旧的可能一直赢。

## 换仓库 / 换分支

名单地址目前写死在软件里（`Services/About/AboutClient.cs` 顶部的两个常量）。要换仓库或换分支，改那两个 URL 再编译即可。

## JSON 语法小抄

- 字符串要双引号；最后一项后面**不要**逗号（不过软件解析很宽容，尾逗号和 `//` 注释都不报错）
- 改完可以把内容贴到 jsonlint.com 之类校验一下再推
