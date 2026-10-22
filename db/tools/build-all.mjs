// Concatenates db/migrations/*.sql in order into db/dist/finance-core-schema.sql, so the whole schema can be pasted
// into one SQL editor window (for example the Supabase SQL Editor). The seed is built separately.
import { mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const files = readdirSync(join(root, 'migrations')).filter((f) => f.endsWith('.sql')).sort();
const header = `-- Finance core schema, generated from db/migrations (${files.join(', ')}).\n-- Do not edit: change the numbered files and run db/tools/build-all.mjs. Safe to run again.\n\n`;
const body = files.map((f) => `-- ==================== ${f} ====================\n${readFileSync(join(root, 'migrations', f), 'utf8')}`).join('\n');
mkdirSync(join(root, 'dist'), { recursive: true });
writeFileSync(join(root, 'dist', 'finance-core-schema.sql'), header + body);
writeFileSync(join(root, 'dist', 'finance-core-dev-seed.sql'), readFileSync(join(root, 'seed', 'dev_seed.sql'), 'utf8'));
console.log(`wrote db/dist/finance-core-schema.sql (${files.length} migrations) and db/dist/finance-core-dev-seed.sql`);
