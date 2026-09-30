# Git 版本管理工作流

## 先建立正确的心智模型

Git 管的是“有意义的修改节点”，不是每次按下 `Ctrl+S` 都生成一个版本。

```text
编辑文件 -> 查看差异 -> 验证 -> 提交（本地版本） -> 推送（远程备份）
```

- 工作区：正在编辑、还没决定保存成版本的内容。
- 提交（commit）：一个有名称、时间和唯一编号的本地检查点。
- 分支（branch）：为一项功能或修复开出的独立工作线。
- 标签（tag）：给发布版本贴固定名称，例如 `v1.2.0`。
- 远程仓库（remote）：GitHub、Gitee 等异地协作与备份服务。

提交和推送是两件事。`git commit` 后已经可以在本机回退；只有 `git push` 后，版本才有异地备份。

## 本项目的日常用法

正式 Git 工作目录是：

```text
C:\Users\34890\Desktop\porjectmanagement\v1.2
```

每完成一个可以清楚描述的小步骤，按下面的顺序操作：

```powershell
cd C:\Users\34890\Desktop\porjectmanagement\v1.2
pnpm git:status
pnpm verify
.\save-version.cmd "feat: 增加看板状态复制"
git push
```

`save-version.cmd` 会显示修改、检查明显的空白字符错误、暂存文件并创建提交，但不会自动上传到外部服务。它优先使用项目自带的 Node，不受系统 pnpm 版本影响。`git push` 只有在配置远程仓库后才需要运行。

常用命令：

```powershell
# 看哪些文件改了
pnpm git:status

# 看具体改了什么
git diff

# 看最近 20 个版本
pnpm git:history

# 保存一个本地版本
.\save-version.cmd "fix: 修复日历跨月显示"

# 安装了与项目匹配的 pnpm 时，也可以使用
pnpm git:save -- "fix: 修复日历跨月显示"
```

## 如何写提交说明

推荐格式是 `类型: 做了什么`，一句话只描述一个主题：

```text
feat: 增加批量复制状态任务
fix: 修复拖拽后排序错误
refactor: 整理同步状态计算
docs: 补充部署说明
test: 增加数据迁移回归测试
chore: 更新构建配置
```

不要使用“改了一些问题”“最新版”“最终版”这类以后无法检索的名称。还没有完成但必须临时保存时，可以使用 `wip: ...`，完成后再整理。

## 功能分支：替代 v1.3、v1.4 文件夹

开发风险较大的功能前，从稳定的 `main` 创建分支：

```powershell
git switch main
git switch -c feature/calendar-redesign
```

在分支上正常提交。验证完成后合回主线：

```powershell
git switch main
git merge --no-ff feature/calendar-redesign
git branch -d feature/calendar-redesign
git push
```

以后不再复制整个项目目录来保存版本。历史代码交给提交和标签，`node_modules`、`build`、`target`、便携运行时和密钥由 `.gitignore` 排除。

## 安全回退

先查看历史和目标编号：

```powershell
pnpm git:history
git show <提交编号>
```

几种常见情况：

```powershell
# 尚未提交：只撤销某一个文件。该文件未提交的修改会丢失。
git restore path/to/file

# 临时收起未完成修改，稍后继续
git stash push -m "正在做的日历改造"
git stash pop

# 撤销一个已经提交、可能也已推送的版本；推荐用于共享历史
git revert <提交编号>

# 从旧版本取回单个文件，然后再提交为新版本
git restore --source <提交编号> -- path/to/file
```

不要把 `git reset --hard` 当作日常回退命令。它会直接丢弃未提交修改，也会改写本地历史。

## 发布版本使用标签

发布通过验证的版本时创建带说明的标签：

```powershell
git tag -a v1.2.0 -m "Jianxiang V1.2.0"
git push origin v1.2.0
```

分支代表还会继续变化的工作线，标签代表一个固定发布点。

## 接入 GitHub 或 Gitee 服务

建议创建一个“私有”的空仓库，不要让网站自动生成 README、LICENSE 或 `.gitignore`，然后在本地执行：

```powershell
git remote add origin <你的私有仓库地址>
git push -u origin main
```

验证是否配置成功：

```powershell
git remote -v
git status --short --branch
```

选择 GitHub 或 Gitee 均可：GitHub 适合国际生态与 Actions，Gitee 在中国大陆网络通常更稳定。远程仓库应启用双重验证；API Key、Token、密码、`.env`、签名私钥和证书不得提交。发现密钥已经进入提交历史时，仅删除文件不够，应立即吊销并更换密钥。

## 推荐节奏

- 开始工作：`git status`，确认没有遗留修改；需要隔离时创建功能分支。
- 开发过程中：频繁保存文件，但只在完成一个小目标时提交。
- 提交前：看 `git diff`，运行与改动相称的检查或测试。
- 完成当天工作：确保有本地提交，并推送到私有远程仓库。
- 发布时：主分支全量验证，创建版本标签，再推送标签。

