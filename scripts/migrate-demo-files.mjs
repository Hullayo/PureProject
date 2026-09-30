/** 使用产品统一迁移器把 static/demo*.pm 重写为当前 schema。 */
import fs from 'node:fs';
import path from 'node:path';
import { parsePmText, CURRENT_PM_SCHEMA } from '$lib/utils/pm-schema';

const staticDir = path.resolve('static');
const files = fs.readdirSync(staticDir).filter(name => /^demo.*\.pm$/i.test(name));

for (const name of files) {
  const file = path.join(staticDir, name);
  const result = parsePmText(fs.readFileSync(file, 'utf8'), name);
  if (!result.ok) {
    throw new Error(`${name} 迁移失败：${result.errors.map(error => `${error.path}: ${error.message}`).join('; ')}`);
  }
  fs.writeFileSync(file, `${JSON.stringify(result.pm, null, 2)}\n`, 'utf8');
  console.log(`${name} -> schema v${CURRENT_PM_SCHEMA}`);
}
