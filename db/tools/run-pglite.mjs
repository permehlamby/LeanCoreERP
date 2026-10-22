// Applies db/migrations/*.sql and db/seed/dev_seed.sql to an in-process PostgreSQL (PGlite) twice (to prove the
// scripts can be run again), then runs db/tests/*.sql. A test file ends with a select of name, result and detail
// and a rollback. The run fails if any result is FAIL.
import { PGlite } from '@electric-sql/pglite';
import { btree_gist } from '@electric-sql/pglite/contrib/btree_gist';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const sqlFiles = (dir) => readdirSync(join(root, dir)).filter((f) => f.endsWith('.sql')).sort().map((f) => [join(dir, f), join(root, dir, f)]);

const db = new PGlite({ extensions: { btree_gist } });

for (let pass = 1; pass <= 2; pass++) {
  for (const [label, path] of [...sqlFiles('migrations'), ['seed/dev_seed.sql', join(root, 'seed', 'dev_seed.sql')]]) {
    try {
      await db.exec(readFileSync(path, 'utf8'));
    } catch (e) {
      console.error(`FAILED ${label} (pass ${pass}): ${e.message}`);
      process.exit(1);
    }
  }
  console.log(`pass ${pass}: migrations and seed applied`);
}
const applied = await db.query('select version from platform.schema_migration order by version');
console.log('applied versions:', applied.rows.map((r) => r.version).join(', '));

let failed = 0, passed = 0;
for (const [label, path] of sqlFiles('tests')) {
  let results;
  try {
    results = await db.exec(readFileSync(path, 'utf8'));
  } catch (e) {
    console.error(`FAILED ${label}: ${e.message}`);
    await db.exec('rollback').catch(() => {});
    failed++;
    continue;
  }
  const table = results.find((r) => r.fields?.some((f) => f.name === 'result'));
  console.log(`\n${label}`);
  for (const row of table?.rows ?? []) {
    const ok = row.result === 'PASS';
    ok ? passed++ : failed++;
    console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${row.name}${ok ? '' : `\n        ${row.detail}`}`);
  }
}
console.log(`\n${passed} passed, ${failed} failed`);
await db.close();
process.exit(failed ? 1 : 0);
