import { spawnSync } from 'node:child_process';

function git(args, options = {}) {
  return spawnSync('git', args, {
    cwd: process.cwd(),
    encoding: 'utf8',
    stdio: options.capture ? 'pipe' : 'inherit',
  });
}

function fail(message, result) {
  console.error(`\n${message}`);
  if (result?.stderr) console.error(result.stderr.trim());
  process.exit(result?.status || 1);
}

const message = process.argv.slice(2).filter(arg => arg !== '--').join(' ').trim();
if (!message) {
  console.error('用法: .\\save-version.cmd "feat: 简要说明本次修改"');
  console.error('或:   pnpm git:save -- "feat: 简要说明本次修改"');
  process.exit(1);
}

const repository = git(['rev-parse', '--show-toplevel'], { capture: true });
if (repository.status !== 0) fail('当前目录不是 Git 仓库。', repository);

const status = git(['status', '--short'], { capture: true });
if (status.status !== 0) fail('无法读取 Git 状态。', status);
if (!status.stdout.trim()) {
  console.log('没有需要保存的修改。');
  process.exit(0);
}

console.log('即将保存以下修改：');
console.log(status.stdout.trimEnd());

const whitespace = git(['diff', '--check'], { capture: true });
if (whitespace.status !== 0) fail('发现空白字符错误，请修复后再保存。', whitespace);

const staged = git(['add', '-A']);
if (staged.status !== 0) fail('暂存修改失败。', staged);

const commit = git(['commit', '-m', message]);
if (commit.status !== 0) fail('创建提交失败；修改仍保留在暂存区。', commit);

console.log('\n已保存本地版本：');
git(['log', '-1', '--oneline', '--decorate']);

const remote = git(['remote', 'get-url', 'origin'], { capture: true });
if (remote.status === 0) {
  console.log('本地版本尚未自动上传；确认后运行 git push。');
} else {
  console.log('尚未配置 origin 远程仓库；当前版本只保存在这台电脑。');
}

