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
// A neighbour: a second tenant with its own company, fiscal year, periods and counter, as the tests of the application leave
// them in a shared database. The tests below run as the migration role, which row-level security never restricts (in Supabase
// `postgres` has BYPASSRLS), so a statement that does not name its tenant changes the neighbour too. Running next to one finds them.
{
  const map = new Map();
  const remap = (text) => text
    .replace(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/g, (u) => { if (!map.has(u)) map.set(u, 'c0de' + u.slice(4)); return map.get(u); })
    .replace("'DEV'", "'NEIGHBOUR'");
  const seed = readFileSync(join(root, 'seed', 'dev_seed.sql'), 'utf8');
  const tests = readFileSync(join(root, 'tests', '001_protection.sql'), 'utf8');
  const setup = tests.match(/insert into core\.fiscal_year[\s\S]*?last_number\)\s*values \([^;]*;/);
  if (!setup) throw new Error('the setup block of the tests was not found');
  await db.exec(remap(seed));
  await db.exec(remap(setup[0]));
  const neighbour = remap('11111111-1111-4111-8111-111111111111');
  for (const n of [1, 2]) await db.exec(`update core.number_range_counter set last_number = ${n} where tenant_id = '${neighbour}'`);
  await db.exec("select set_config('app.tenant_id', '', false)");
  console.log('a neighbour tenant is in place:', neighbour);
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
