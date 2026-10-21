// Independent second implementation of the canonical form of 02, 9.3a (hash_version 2).
// Usage: node verify-hash-v2.mjs hash-v2.json          verifies the expected values of the vectors
//        node verify-hash-v2.mjs hash-v2.json --print  prints the vectors with freshly computed expected values
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';

const T = { ID: 'id', TEXT: 'text', INT: 'int', BOOL: 'bool', DATE: 'date', TS: 'ts', AMT: 'amt', RATE: 'rate', HEX: 'hex' };
const f = (name, type, nullable = false) => ({ name, type, nullable });

const DOCUMENT = [
  f('tenant_id', T.ID), f('company_id', T.ID), f('document_id', T.ID), f('ledger_id', T.ID),
  f('document_type', T.TEXT), f('is_system_generated', T.BOOL), f('number_series_id', T.ID),
  f('fiscal_year', T.INT), f('sequence_no', T.INT), f('document_number', T.TEXT), f('fiscal_period', T.INT),
  f('soft_close_override', T.BOOL), f('migration_batch_id', T.ID, true), f('detail_digest', T.HEX, true),
  f('document_date', T.DATE), f('posting_date', T.DATE), f('posted_at', T.TS), f('transaction_currency', T.TEXT),
  f('reversal_of_document_id', T.ID, true), f('reversal_reason_code', T.TEXT, true), f('auto_reversal_date', T.DATE, true),
  f('ic_transaction_id', T.ID, true), f('ic_counterpart_company_id', T.ID, true), f('origin', T.TEXT),
  f('source_system', T.TEXT), f('source_document_ref', T.TEXT, true), f('idempotency_key', T.TEXT),
  f('command_name', T.TEXT), f('posted_by', T.TEXT), f('correlation_id', T.TEXT), f('hash_version', T.INT),
];
const LINE = [
  f('tenant_id', T.ID), f('company_id', T.ID), f('document_id', T.ID), f('line_no', T.INT), f('ledger_id', T.ID),
  f('line_type', T.TEXT), f('account_id', T.ID), f('posting_date', T.DATE), f('fiscal_year', T.INT), f('fiscal_period', T.INT),
  f('reversed_line_no', T.INT, true), f('tax_code_id', T.ID, true),
  f('amount_tc', T.AMT), f('amount_lc', T.AMT), f('currency_lc', T.TEXT), f('rate_lc', T.RATE), f('rate_lc_table', T.RATE, true),
  f('amount_gc', T.AMT), f('currency_gc', T.TEXT), f('rate_gc', T.RATE), f('rate_gc_table', T.RATE, true),
  f('amount_rc', T.AMT, true), f('currency_rc', T.TEXT, true), f('rate_rc', T.RATE, true), f('rate_rc_table', T.RATE, true),
  f('rate_type', T.TEXT), f('rate_date', T.DATE), f('ic_counterpart_company_id', T.ID, true),
];
const TAX = [
  f('tenant_id', T.ID), f('company_id', T.ID), f('document_id', T.ID), f('line_no', T.INT),
  f('tax_code_id', T.ID), f('tax_code_version_id', T.ID), f('tax_type', T.TEXT), f('tax_rate', T.RATE),
  f('base_amount_tc', T.AMT), f('base_amount_lc', T.AMT), f('base_amount_gc', T.AMT), f('base_amount_rc', T.AMT, true),
  f('determined_tax_code_id', T.ID), f('tax_class_id', T.ID, true), f('origin_country', T.TEXT), f('destination_country', T.TEXT),
  f('partner_vat_status', T.TEXT, true), f('tax_date', T.DATE), f('vat_currency', T.TEXT),
  f('vat_base_amount', T.AMT), f('vat_tax_amount', T.AMT), f('vat_rate', T.RATE), f('vat_rate_date', T.DATE, true),
  f('is_override', T.BOOL), f('tax_amount_supplied_tc', T.AMT, true), f('rounding_mode', T.TEXT),
  f('origin_eu', T.BOOL), f('destination_eu', T.BOOL), f('deductible_pct', T.RATE),
  f('deduction_key', T.TEXT, true), f('deduction_key_rate', T.RATE, true), f('deduction_share', T.RATE),
  f('deduction_cap_amount_tc', T.AMT, true), f('gross_tax_amount_tc', T.AMT),
];
const TAXBASE = [
  f('tenant_id', T.ID), f('company_id', T.ID), f('document_id', T.ID), f('tax_line_no', T.INT), f('base_line_no', T.INT),
  f('base_amount_tc', T.AMT), f('nondeductible_tax_tc', T.AMT), f('nondeductible_line_no', T.INT, true),
];
const OPENITEM = [
  f('tenant_id', T.ID), f('company_id', T.ID), f('document_id', T.ID), f('line_no', T.INT), f('partner_id', T.ID, true),
  f('legacy_document_date', T.DATE, true), f('legacy_posting_date', T.DATE, true), f('legacy_document_ref', T.TEXT, true),
];

const sha = (buf) => createHash('sha256').update(buf).digest();

// Fixed-scale decimal from a decimal string, using BigInt only (no floating point).
function fixed(str, scale) {
  const m = /^(-?)(\d+)(?:\.(\d+))?$/.exec(String(str));
  if (!m) throw new Error(`not a decimal: ${str}`);
  let [, sign, whole, frac = ''] = m;
  frac = frac.replace(/0+$/, '');
  if (frac.length > scale) throw new Error(`${str} has more than ${scale} fraction digits`);
  frac = frac.padEnd(scale, '0');
  whole = whole.replace(/^0+(?=\d)/, '');
  const zero = /^0+$/.test(whole + frac);
  return (zero ? '' : sign) + whole + '.' + frac;
}

function encodeValue(spec, v) {
  if (v === null || v === undefined) {
    if (!spec.nullable) throw new Error(`${spec.name} is not nullable`);
    return null;
  }
  let s;
  switch (spec.type) {
    case T.ID: if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(v)) throw new Error(`bad id ${v}`); s = v; break;
    case T.TEXT: s = v; break;
    case T.INT: s = BigInt(v).toString(); break;
    case T.BOOL: s = v ? 'true' : 'false'; break;
    case T.DATE: if (!/^\d{4}-\d{2}-\d{2}$/.test(v)) throw new Error(`bad date ${v}`); s = v; break;
    case T.TS: if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$/.test(v)) throw new Error(`bad timestamp ${v}`); s = v; break;
    case T.AMT: s = fixed(v, 4); break;
    case T.RATE: s = fixed(v, 10); break;
    case T.HEX: if (!/^[0-9a-f]{64}$/.test(v)) throw new Error(`bad hex ${v}`); s = v; break;
  }
  return Buffer.from(s, 'utf8');
}

function field(bytes) {
  if (bytes === null) return Buffer.from([0xff, 0xff, 0xff, 0xff]);
  const len = Buffer.alloc(4); len.writeUInt32BE(bytes.length);
  return Buffer.concat([len, bytes]);
}
const block = (inner) => field(inner);
const nullField = () => field(null);

function record(schema, values) {
  for (const k of Object.keys(values)) if (!schema.some((s) => s.name === k)) throw new Error(`unknown column ${k}`);
  return Buffer.concat(schema.map((s) => {
    if (!(s.name in values)) throw new Error(`missing column ${s.name}`);
    return field(encodeValue(s, values[s.name]));
  }));
}

function lineHash(l) {
  const parts = [
    block(record(LINE, l.line)),
    l.tax ? block(record(TAX, l.tax)) : nullField(),
    block(Buffer.concat([...(l.tax_bases ?? [])].sort((a, b) => a.base_line_no - b.base_line_no).map((b) => block(record(TAXBASE, b))))),
    l.open_item ? block(record(OPENITEM, l.open_item)) : nullField(),
  ];
  return sha(Buffer.concat(parts));
}

function genesis(c) {
  return sha(Buffer.from(['genesis', c.tenant_id, c.company_id, c.chain_type, c.scope_key].join('|'), 'utf8'));
}

function compute(c) {
  const lines = [...c.lines].sort((a, b) => a.line.line_no - b.line.line_no);
  const lineHashes = lines.map(lineHash);
  const lineDigest = sha(Buffer.concat(lineHashes));
  const prev = c.prev_hash === 'GENESIS' ? genesis(c.chain) : Buffer.from(c.prev_hash, 'hex');
  const d = c.document;
  const text = (x) => Buffer.from(x, 'utf8');
  const row = sha(Buffer.concat([
    field(text('2')), field(text(d.tenant_id)), field(text(d.company_id)), field(text(d.ledger_id)),
    field(text(String(c.chain_seq))), field(text(prev.toString('hex'))),
    block(record(DOCUMENT, d)), field(text(lineDigest.toString('hex'))),
  ]));
  return {
    genesis: genesis(c.chain).toString('hex'),
    line_hashes: lineHashes.map((h) => h.toString('hex')),
    line_digest: lineDigest.toString('hex'),
    row_hash: row.toString('hex'),
  };
}

const [, , file, flag] = process.argv;
const vectors = JSON.parse(readFileSync(file, 'utf8'));
let failed = 0;
for (const c of vectors.cases) {
  const got = compute(c);
  if (flag === '--print') { c.expected = got; continue; }
  const ok = JSON.stringify(got) === JSON.stringify(c.expected);
  console.log(`${ok ? 'PASS' : 'FAIL'} ${c.name}`);
  if (!ok) { failed++; console.log(' got     ', JSON.stringify(got)); console.log(' expected', JSON.stringify(c.expected)); }
}
if (flag === '--print') console.log(JSON.stringify(vectors, null, 2));
process.exit(failed ? 1 : 0);
